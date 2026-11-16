using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Spooling;

namespace LabelPrinter.Agent.Core.Erp;

public sealed record SyncNote(string Code, Guid? JobId = null, string? Detail = null);

/// <summary>What one cycle did. Codes are stable strings so callers and tests can assert on them.</summary>
public sealed class SyncReport
{
    public const string Accepted = "ACCEPTED", Rejected = "REJECTED", Conflict = "CONFLICT", Submitted = "SUBMITTED",
        Cancelled = "CANCELLED", Abandoned = "ABANDONED", Blocked = "BLOCKED", Anomaly = "ANOMALY", ErpError = "ERP_ERROR",
        Quarantined = "QUARANTINED", Interrupted = "INTERRUPTED";

    public List<SyncNote> Notes { get; } = [];
    /// <summary>A call got no usable answer; remaining network steps of the cycle were skipped.</summary>
    public bool ErpUnavailable { get; set; }
    /// <summary>Set when the ERP answered with a status that is not about one job (401, 403, 426, 429...).</summary>
    public ErpException? ErpFailure { get; set; }
    public bool Paused { get; set; }
    /// <summary>True when the cycle moved something: callers may poll again soon instead of backing off.</summary>
    public bool DidWork => Notes.Any(n => n.Code is Accepted or Submitted or Cancelled or Abandoned or Rejected);

    public bool Has(string code, Guid? jobId = null) => Notes.Any(n => n.Code == code && (jobId is null || n.JobId == jobId));
    internal void Add(string code, Guid? jobId = null, string? detail = null) => Notes.Add(new SyncNote(code, jobId, detail));
}

public sealed record ErpSyncOptions
{
    /// <summary>Reconcile runs at start, after LEASE_LOST, and at least this often.</summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>A blocked outbox is retried after base × 2^(blocks−1), capped.</summary>
    public TimeSpan BlockRetryBase { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan BlockRetryCap { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Drives a <see cref="JobLedger"/> from the ERP agent API. The ledger stays the authority on what may be
/// submitted; nothing the ERP answers can make this class submit a job that is not locally Accepted.
///
/// Policy choices made here (not dictated by the ERP):
/// - /accept is called only for jobs whose acceptance record is already durable.
/// - A job is submitted only after the ERP confirmed it holds the job as accepted_by_agent. Without that
///   confirmation it waits, and fails unsubmitted once its authorization window ends.
/// - Reconcile directives are advisory. "resume" changes nothing; "report_events" only trims the outbox;
///   "abandon" puts a never-submitted job on hold until /accept says why (cancel, gone, or not at all).
/// - An ERP answer that is neither success nor "try later" (404, 409, 422, incomplete result) blocks that job's
///   outbox durably. Nothing is dropped; delivery is retried with capped exponential backoff.
/// - Evidence that the journal is older than the ERP's view quarantines the ledger: no submissions until an
///   operator clears it.
/// One cycle is not thread-safe; run cycles from a single loop.
/// </summary>
public sealed class ErpSync(JobLedger ledger, IErpClient erp, ISpooler spooler, TimeProvider clock, ErpSyncOptions? options = null)
{
    /// <summary>Optional local admission check (e.g. "is this the configured printer, language and DPI?").
    /// Returns a reason code to refuse a structurally valid job before its payload is downloaded.</summary>
    public Func<JobEnvelope, string?>? Admit { get; init; }

    private static readonly string AcceptedWire = JobStateMachine.WireName(JobState.Accepted);
    private static readonly HashSet<string> ServerSubmittedStates = ["submitting", "submitted_to_spooler", "confirmed_completed"];
    private readonly ErpSyncOptions _options = options ?? new ErpSyncOptions();
    private readonly HashSet<Guid> _hold = []; // ERP said "abandon"; not submitted until /accept explains
    private bool _needReconcile = true;        // always reconcile first after a (re)start
    private DateTimeOffset _lastReconcile = DateTimeOffset.MinValue;

    public SyncReport RunCycle(bool claim = true, int claimMax = 1)
    {
        var report = new SyncReport();
        foreach (var id in ledger.MarkInterruptedSubmissions()) report.Add(SyncReport.Interrupted, id);

        Net(report, () => { if (ReconcileDue()) Reconcile(report); });
        Net(report, () => DeliverAccepts(report));
        Net(report, () => DeliverEvents(report));
        if (claim && ledger.QuarantineReason is null) Net(report, () => Claim(report, claimMax));
        Net(report, () => DeliverAccepts(report));
        SubmitReady(report); // local work proceeds even when the ERP is unreachable
        Net(report, () => DeliverEvents(report));
        return report;
    }

    private bool ReconcileDue() => _needReconcile || clock.GetUtcNow() - _lastReconcile >= _options.ReconcileInterval;

    private static void Net(SyncReport report, Action step)
    {
        if (report.ErpUnavailable) return;
        try { step(); }
        catch (ErpUnavailableException) { report.ErpUnavailable = true; }
        catch (ErpException ex)
        {
            // Not about one job (authentication, version, suspension, rate limit). Stop talking for this cycle.
            report.Add(SyncReport.ErpError, detail: $"{ex.Status} {ex.ErrorCode}");
            report.ErpFailure = ex;
            report.ErpUnavailable = true;
        }
    }

    private bool BlockedNow(JobSnapshot job)
    {
        if (job.Block is not { } block) return false;
        var factor = Math.Pow(2, Math.Min(block.Count - 1, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(_options.BlockRetryBase.Ticks * factor, _options.BlockRetryCap.Ticks));
        return clock.GetUtcNow() < block.At + delay;
    }

    private void Block(SyncReport report, Guid jobId, string code)
    {
        ledger.BlockOutbox(jobId, code);
        report.Add(SyncReport.Blocked, jobId, code);
    }

    private void Quarantine(SyncReport report, Guid jobId, string code)
    {
        ledger.Quarantine(code);
        report.Add(SyncReport.Quarantined, jobId, code);
    }

    private void Reconcile(SyncReport report)
    {
        var pendingEvents = ledger.PendingEvents().Select(e => e.JobId).ToHashSet();
        var jobs = ledger.Jobs()
            .Where(j => !JobStateMachine.IsTerminal(j.State) || !j.AcceptAcknowledged || pendingEvents.Contains(j.Key.JobId))
            .ToList();

        foreach (var chunk in jobs.Chunk(IErpClient.MaxReconcileJobs))
        {
            var directives = erp.Reconcile([.. chunk.Select(j => new LocalJobReport(j.Key.JobId, JobStateMachine.WireName(j.State), j.LastSeq))]);
            foreach (var d in directives)
            {
                var job = ledger.Find(d.JobId);
                if (job is null) continue;
                switch (d.Action)
                {
                    case ReconcileAction.ReportEvents:
                        // The server holds the job past "delivered" and has applied events below FromSeq.
                        var upTo = (d.FromSeq ?? 1) - 1;
                        if (upTo > job.LastSeq)
                        {
                            // The ERP has seen events this journal never produced: the journal is older than reality.
                            Quarantine(report, d.JobId, "ERP_AHEAD_OF_JOURNAL");
                            break;
                        }
                        ledger.AcknowledgeAccept(d.JobId);
                        if (upTo >= 1) ledger.AcknowledgeEvents(d.JobId, upTo);
                        break;
                    case ReconcileAction.Abandon:
                        if (job.State == JobState.Accepted) _hold.Add(d.JobId);
                        break;
                    case ReconcileAction.Resume:
                        break; // the ERP also says this for Unknown jobs; it grants nothing here
                }
            }
        }
        _needReconcile = false;
        _lastReconcile = clock.GetUtcNow();
    }

    private void DeliverAccepts(SyncReport report)
    {
        // Jobs on hold are asked again even if their accept was acknowledged: the answer says why to abandon.
        var targets = ledger.PendingAccepts().Select(p => p.JobId)
            .Concat(_hold.Where(id => ledger.Find(id) is { State: JobState.Accepted }))
            .Distinct().ToList();

        foreach (var id in targets)
        {
            var job = ledger.Find(id)!;
            var held = _hold.Contains(id);
            if (!held && BlockedNow(job)) continue;

            AcceptResponse response;
            try
            {
                response = erp.Accept(id, job.DeliveryAttempt);
            }
            catch (ErpException ex) when (ex.ErrorCode == "JOB_NOT_FOUND")
            {
                // A 404 acknowledges nothing. The job is not submitted (no confirmation) and stays owed.
                if (held && ledger.Abandon(id, "ERP_ABANDON")) report.Add(SyncReport.Abandoned, id, "ERP_ABANDON");
                _hold.Remove(id);
                Block(report, id, "JOB_NOT_FOUND");
                continue;
            }
            catch (ErpException ex) when (ex.ErrorCode is "LEASE_LOST" or "JOB_EXPIRED")
            {
                if (job.State != JobState.Accepted)
                {
                    ledger.AcknowledgeAccept(id); // the job exists; its events report what happened
                }
                else if (ex.ErrorCode == "LEASE_LOST" && !held)
                {
                    _needReconcile = true; // either a redelivery is on its way or the server gave the job up
                }
                else
                {
                    if (ledger.Abandon(id, "ERP_" + ex.ErrorCode)) report.Add(SyncReport.Abandoned, id, ex.ErrorCode);
                    ledger.AcknowledgeAccept(id);
                }
                _hold.Remove(id);
                continue;
            }

            _hold.Remove(id);
            if (response.State == AcceptedWire)
            {
                ledger.AcknowledgeAccept(id);
                if (response.CancelRequested && ledger.Cancel(id)) report.Add(SyncReport.Cancelled, id);
            }
            else if (job.State == JobState.Accepted && ServerSubmittedStates.Contains(response.State))
            {
                // The ERP recorded a submission this journal does not know about.
                Quarantine(report, id, "ERP_AHEAD_OF_JOURNAL");
            }
            else
            {
                // E.g. "unknown" after the server gave up on delivery. The agent knows it never submitted the
                // job, so it stops and says so.
                if (ledger.Abandon(id, "ERP_STATE_" + response.State.ToUpperInvariant()))
                    report.Add(SyncReport.Abandoned, id, response.State);
                ledger.AcknowledgeAccept(id);
            }
        }
    }

    private void DeliverEvents(SyncReport report)
    {
        var waitingForAccept = ledger.PendingAccepts().Select(p => p.JobId).ToHashSet();
        foreach (var group in ledger.PendingEvents().GroupBy(e => e.JobId).ToList())
        {
            var id = group.Key;
            if (waitingForAccept.Contains(id) || BlockedNow(ledger.Find(id)!)) continue;
            foreach (var batch in group.OrderBy(e => e.Seq).Chunk(IErpClient.MaxEventsPerRequest))
            {
                try
                {
                    var response = erp.PostEvents(id, batch);
                    var answered = response.Results.Where(x => x.Applied || x.Duplicate || x.Stale).Select(x => x.EventId).ToHashSet();
                    // Applied, duplicate and stale all mean the ERP has durably seen the event.
                    var delivered = batch.TakeWhile(e => answered.Contains(e.EventId)).ToList();
                    if (delivered.Count > 0) ledger.AcknowledgeEvents(id, delivered[^1].Seq);
                    if (delivered.Count < batch.Length) { Block(report, id, "INCOMPLETE_RESULT"); break; }
                }
                catch (ErpException ex) when (ex.Status is 404 or 409 or 422)
                {
                    Block(report, id, ex.ErrorCode ?? "HTTP_" + ex.Status.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                }
            }
        }
    }

    private void Claim(SyncReport report, int max)
    {
        var response = erp.Claim(Math.Clamp(max, 1, IErpClient.MaxClaim));
        report.Paused = response.Paused;

        foreach (var envelope in response.Jobs)
        {
            var id = envelope.JobId;
            if (ledger.Find(id) is not null)
            {
                // Redelivery: the ledger answers from its journal; no download, no new attempt.
                if (ledger.Accept(envelope, ReadOnlyMemory<byte>.Empty).Outcome == AcceptOutcome.Conflict)
                {
                    // Same id, different content: the identity is contested, so nothing is submitted under it.
                    ledger.Abandon(id, "IDENTITY_CONTENT_CONFLICT");
                    report.Add(SyncReport.Conflict, id);
                }
                continue;
            }

            var errors = EnvelopeValidator.ValidateEnvelope(envelope, ledger.AgentId);
            if (errors.Count > 0) { Reject(report, envelope, "ENVELOPE_INVALID", Describe(errors)); continue; }
            if (Admit?.Invoke(envelope) is { } refusal) { Reject(report, envelope, refusal, null); continue; }

            byte[]? payload;
            try { payload = FetchVerified(envelope); }
            catch (ErpException ex) when (ex.Status is 404 or 410 or 422)
            {
                report.Add(SyncReport.Rejected, id, ex.ErrorCode ?? "PAYLOAD_FETCH_FAILED"); // the ERP's own answer; nothing to report back
                continue;
            }
            if (payload is null) { Reject(report, envelope, "PAYLOAD_HASH_MISMATCH", null); continue; }

            var result = ledger.Accept(envelope, payload);
            if (result.Outcome == AcceptOutcome.AcceptedNew) report.Add(SyncReport.Accepted, id);
            else Reject(report, envelope, result.Errors.FirstOrDefault()?.Code ?? "REJECTED", Describe(result.Errors));
        }

        foreach (var id in response.CancelJobIds)
            if (ledger.Find(id) is not null && ledger.Cancel(id)) report.Add(SyncReport.Cancelled, id);
    }

    /// <summary>Remembers the refusal durably so it can be reported as "failed"; if the envelope has no usable
    /// identity it is only noted (there is nothing truthful to report it under).</summary>
    private void Reject(SyncReport report, JobEnvelope envelope, string code, string? detail)
    {
        var recorded = ledger.RecordRejected(envelope, code);
        report.Add(SyncReport.Rejected, envelope.JobId, recorded ? code : code + " (not reportable)" + (detail is null ? "" : " " + detail));
    }

    /// <summary>One refetch on mismatch, as the ERP contract suggests; then the job is refused.</summary>
    private byte[]? FetchVerified(JobEnvelope envelope)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var payload = erp.GetPayload(envelope.JobId);
            var headerOk = payload.Sha256Header is null || payload.Sha256Header == envelope.Output.Sha256;
            if (headerOk && EnvelopeValidator.ValidatePayload(envelope.Output, payload.Bytes).Count == 0) return payload.Bytes;
        }
        return null;
    }

    private void SubmitReady(SyncReport report)
    {
        foreach (var job in ledger.Jobs().Where(j => j.State == JobState.Accepted))
        {
            var id = job.Key.JobId;
            if (clock.GetUtcNow() > job.Envelope.AgentMustNotSubmitAfter)
            {
                if (ledger.Abandon(id, "AUTHORIZATION_EXPIRED")) report.Add(SyncReport.Abandoned, id, "AUTHORIZATION_EXPIRED");
                _hold.Remove(id);
            }
            else if (job.AcceptAcknowledged && !_hold.Contains(id) && ledger.QuarantineReason is null)
            {
                var result = ledger.Submit(id, spooler);
                report.Add(SyncReport.Submitted, id, JobStateMachine.WireName(result.State));
            }
        }
    }

    private static string Describe(IReadOnlyList<ValidationError> errors) => string.Join(",", errors.Select(e => e.Code + ":" + e.Field));
}
