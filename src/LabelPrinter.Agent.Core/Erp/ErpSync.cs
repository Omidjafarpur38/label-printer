using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Spooling;

namespace LabelPrinter.Agent.Core.Erp;

public sealed record SyncNote(string Code, Guid? JobId = null, string? Detail = null);

/// <summary>What one cycle did. Codes are stable strings so callers and tests can assert on them.</summary>
public sealed class SyncReport
{
    public const string Accepted = "ACCEPTED", Rejected = "REJECTED", Conflict = "CONFLICT", Submitted = "SUBMITTED",
        Cancelled = "CANCELLED", Abandoned = "ABANDONED", Blocked = "BLOCKED", Anomaly = "ANOMALY", ErpError = "ERP_ERROR";

    public List<SyncNote> Notes { get; } = [];
    /// <summary>A call got no usable answer; remaining network steps of the cycle were skipped.</summary>
    public bool ErpUnavailable { get; set; }
    public bool Paused { get; set; }

    public bool Has(string code, Guid? jobId = null) => Notes.Any(n => n.Code == code && (jobId is null || n.JobId == jobId));
    internal void Add(string code, Guid? jobId = null, string? detail = null) => Notes.Add(new SyncNote(code, jobId, detail));
}

/// <summary>
/// Drives a <see cref="JobLedger"/> from the ERP agent API. The ledger stays the authority on what may be
/// printed; nothing the ERP answers can make this class submit a job that is not locally Accepted.
///
/// Policy choices made here (not dictated by the ERP):
/// - /accept is called only for jobs whose acceptance record is already durable.
/// - A job is submitted only after the ERP confirmed it holds the job as accepted_by_agent. Without that
///   confirmation it waits, and fails unprinted once its authorization window ends.
/// - Reconcile directives are advisory. "resume" changes nothing; "abandon" stops a never-submitted job;
///   "report_events" only trims the outbox.
/// - Events are sent in sequence; a rejected batch blocks that job's stream until the next reconcile.
/// One cycle is not thread-safe; run cycles from a single loop.
/// </summary>
public sealed class ErpSync(JobLedger ledger, IErpClient erp, ISpooler spooler, TimeProvider clock)
{
    private static readonly string AcceptedWire = JobStateMachine.WireName(JobState.Accepted);
    private readonly HashSet<Guid> _blocked = [];
    private bool _needReconcile = true; // always reconcile first after a (re)start

    public SyncReport RunCycle(bool claim = true, int claimMax = 1)
    {
        var report = new SyncReport();
        Net(report, () => { if (_needReconcile) Reconcile(report); });
        Net(report, () => DeliverAccepts(report));
        Net(report, () => DeliverEvents(report));
        if (claim) Net(report, () => Claim(report, claimMax));
        Net(report, () => DeliverAccepts(report));
        SubmitReady(report); // local work proceeds even when the ERP is unreachable
        Net(report, () => DeliverEvents(report));
        return report;
    }

    private static void Net(SyncReport report, Action step)
    {
        if (report.ErpUnavailable) return;
        try { step(); }
        catch (ErpUnavailableException) { report.ErpUnavailable = true; }
        catch (ErpException ex)
        {
            // Not specific to one job (authentication, version, suspension...). Stop talking for this cycle.
            report.Add(SyncReport.ErpError, detail: $"{ex.Status} {ex.ErrorCode}");
            report.ErpUnavailable = true;
        }
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
                        ledger.AcknowledgeAccept(d.JobId);
                        var upTo = (d.FromSeq ?? 1) - 1;
                        if (upTo > job.LastSeq) report.Add(SyncReport.Anomaly, d.JobId, "server is ahead of the local journal");
                        else if (upTo >= 1) ledger.AcknowledgeEvents(d.JobId, upTo);
                        break;
                    case ReconcileAction.Abandon:
                        if (ledger.Abandon(d.JobId, "ERP_ABANDON")) report.Add(SyncReport.Abandoned, d.JobId, "ERP_ABANDON");
                        break;
                    case ReconcileAction.Resume:
                        break; // the ERP also says this for Unknown jobs; it grants nothing here
                }
            }
        }
        _blocked.Clear();
        _needReconcile = false;
    }

    private void DeliverAccepts(SyncReport report)
    {
        foreach (var pending in ledger.PendingAccepts())
        {
            var id = pending.JobId;
            var local = ledger.Find(id)!.State;
            AcceptResponse response;
            try
            {
                response = erp.Accept(id, pending.Attempt);
            }
            catch (ErpException ex) when (ex.ErrorCode is "LEASE_LOST" or "JOB_EXPIRED" or "JOB_NOT_FOUND")
            {
                if (local != JobState.Accepted)
                {
                    ledger.AcknowledgeAccept(id); // nothing left to protect; let the events report what happened
                }
                else if (ex.ErrorCode == "LEASE_LOST")
                {
                    _needReconcile = true; // either a redelivery is on its way or the server gave the job up
                }
                else
                {
                    if (ledger.Abandon(id, "ERP_" + ex.ErrorCode)) report.Add(SyncReport.Abandoned, id, ex.ErrorCode);
                    ledger.AcknowledgeAccept(id);
                }
                continue;
            }

            if (response.State == AcceptedWire)
            {
                ledger.AcknowledgeAccept(id);
                if (response.CancelRequested && ledger.Cancel(id)) report.Add(SyncReport.Cancelled, id);
            }
            else
            {
                // The server already moved this job elsewhere (e.g. "unknown" after it gave up on delivery).
                // The agent knows it never submitted it, so it stops here and says so.
                if (ledger.Abandon(id, "ERP_STATE_" + response.State.ToUpperInvariant()))
                    report.Add(SyncReport.Abandoned, id, response.State);
                ledger.AcknowledgeAccept(id);
            }
        }
    }

    private void DeliverEvents(SyncReport report)
    {
        var waitingForAccept = ledger.PendingAccepts().Select(p => p.JobId).ToHashSet();
        foreach (var group in ledger.PendingEvents().GroupBy(e => e.JobId))
        {
            var id = group.Key;
            if (waitingForAccept.Contains(id) || _blocked.Contains(id)) continue;
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
                catch (ErpException ex) when (ex.ErrorCode == "JOB_NOT_FOUND")
                {
                    ledger.AcknowledgeEvents(id, batch[^1].Seq); // nobody to tell; the journal keeps the history
                    report.Add(SyncReport.Anomaly, id, "ERP does not know this job");
                }
                catch (ErpException ex) when (ex.Status is 409 or 422)
                {
                    Block(report, id, ex.ErrorCode ?? ex.Status.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                }
            }
        }
    }

    private void Block(SyncReport report, Guid jobId, string code)
    {
        _blocked.Add(jobId);
        report.Add(SyncReport.Blocked, jobId, code);
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
                    // Same id, different content: the identity is contested, so nothing is printed under it.
                    ledger.Abandon(id, "IDENTITY_CONTENT_CONFLICT");
                    report.Add(SyncReport.Conflict, id);
                }
                continue;
            }

            var errors = EnvelopeValidator.ValidateEnvelope(envelope, ledger.AgentId);
            if (errors.Count > 0) { report.Add(SyncReport.Rejected, id, Describe(errors)); continue; }

            byte[]? payload;
            try { payload = FetchVerified(envelope); }
            catch (ErpException ex) { report.Add(SyncReport.Rejected, id, ex.ErrorCode ?? "PAYLOAD_FETCH_FAILED"); continue; }
            if (payload is null) { report.Add(SyncReport.Rejected, id, "PAYLOAD_HASH_MISMATCH"); continue; }

            var result = ledger.Accept(envelope, payload);
            if (result.Outcome == AcceptOutcome.AcceptedNew) report.Add(SyncReport.Accepted, id);
            else report.Add(SyncReport.Rejected, id, Describe(result.Errors));
        }

        foreach (var id in response.CancelJobIds)
            if (ledger.Find(id) is not null && ledger.Cancel(id)) report.Add(SyncReport.Cancelled, id);
    }

    /// <summary>One refetch on mismatch, as the ERP contract suggests; then the job is left unaccepted.</summary>
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
            }
            else if (job.AcceptAcknowledged)
            {
                var result = ledger.Submit(id, spooler);
                report.Add(SyncReport.Submitted, id, JobStateMachine.WireName(result.State));
            }
        }
    }

    private static string Describe(IReadOnlyList<ValidationError> errors) => string.Join(",", errors.Select(e => e.Code + ":" + e.Field));
}
