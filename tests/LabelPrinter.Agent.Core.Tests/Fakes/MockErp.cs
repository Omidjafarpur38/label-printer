using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Erp;
using LabelPrinter.Agent.Core.Jobs;

namespace LabelPrinter.Agent.Core.Tests.Fakes;

/// <summary>
/// In-memory stand-in for the ERP agent API. The rules are transcribed from print.service.ts at the baseline
/// commit (claim / getPayload / accept / applyEvents / reconcile / sweepExpired, AGENT_TRANS, TERMINAL), so
/// that agent behaviour is tested against what the ERP does rather than what its contract draft says.
/// It is a mock: no HTTP, no JSON, no authentication, no database.
/// </summary>
public sealed class MockErp(TimeProvider clock) : IErpClient
{
    public enum Fault { None, RequestLost, ResponseLost }

    public sealed class ServerJob
    {
        public required JobEnvelope Envelope;
        public required byte[] Payload;
        public string State = "queued";
        public int DeliveryAttempts;
        public DateTimeOffset LeaseUntil;
        public int LastSeq;
        public bool CancelRequested;
        public readonly List<(Guid EventId, int Seq, string State, bool Applied, string? ErrorCode, string? EvidenceLevel)> AgentEvents = [];
    }

    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    private static readonly HashSet<string> Terminal = ["confirmed_completed", "failed", "cancelled", "expired"];
    private static readonly Dictionary<string, string[]> AgentTrans = new()
    {
        ["delivered"] = ["accepted_by_agent", "failed", "cancelled", "unknown"],
        ["accepted_by_agent"] = ["submitting", "failed", "cancelled", "unknown"],
        ["submitting"] = ["submitted_to_spooler", "failed", "unknown"],
        ["submitted_to_spooler"] = ["confirmed_completed", "unknown"],
        ["unknown"] = ["confirmed_completed", "failed", "submitted_to_spooler"],
    };

    private readonly Dictionary<Guid, ServerJob> _jobs = [];
    private readonly List<Guid> _order = [];
    private readonly Dictionary<string, Queue<Fault>> _faults = [];

    public bool Paused { get; set; }
    public bool Down { get; set; }
    public Dictionary<string, int> Calls { get; } = [];
    /// <summary>When set, payload bytes are altered for this many downloads.</summary>
    public int CorruptPayloadDownloads { get; set; }
    /// <summary>One-shot: answer the next events call without a result for its last event (not ERP behaviour;
    /// used to check that the agent only acknowledges events the answer actually covers).</summary>
    public bool OmitLastEventResultOnce { get; set; }

    public ServerJob Job(Guid id) => _jobs[id];
    public int CallCount(string op) => Calls.GetValueOrDefault(op);

    /// <summary>User side: create a queued job (createTestPrint / reprint).</summary>
    public ServerJob Enqueue(JobEnvelope envelope, byte[] payload)
    {
        var job = new ServerJob { Envelope = envelope, Payload = payload };
        _jobs.Add(envelope.JobId, job);
        _order.Add(envelope.JobId);
        return job;
    }

    /// <summary>User side: cancel(). Returns the outcome string of the ERP.</summary>
    public string UserCancel(Guid id)
    {
        var j = _jobs[id];
        if (j.State == "queued") { j.State = "cancelled"; return "cancelled"; }
        if (j.State is "delivered" or "accepted_by_agent") { j.CancelRequested = true; return "cancel_requested"; }
        return j.State is "submitting" or "submitted_to_spooler" ? "too_late" : "not_cancellable";
    }

    /// <summary>Makes the ERP answer 404 for this job (wrong database, deleted row). Returns it for <see cref="Restore"/>.</summary>
    public ServerJob Remove(Guid id) { var j = _jobs[id]; _jobs.Remove(id); _order.Remove(id); return j; }
    public void Restore(ServerJob job) { _jobs.Add(job.Envelope.JobId, job); _order.Add(job.Envelope.JobId); }

    public MockErp FailNext(string op, Fault fault)
    {
        if (!_faults.TryGetValue(op, out var q)) _faults[op] = q = new Queue<Fault>();
        q.Enqueue(fault);
        return this;
    }

    private T Call<T>(string op, Func<T> body)
    {
        Calls[op] = CallCount(op) + 1;
        if (Down) throw new ErpUnavailableException("mock ERP is down");
        var fault = _faults.TryGetValue(op, out var q) && q.Count > 0 ? q.Dequeue() : Fault.None;
        if (fault == Fault.RequestLost) throw new ErpUnavailableException(op + ": request lost");
        var result = body();
        if (fault == Fault.ResponseLost) throw new ErpUnavailableException(op + ": response lost");
        return result;
    }

    private DateTimeOffset Now => clock.GetUtcNow();

    private void SweepExpired()
    {
        foreach (var j in _jobs.Values.Where(j => j.Envelope.AuthorizationExpiresAt < Now))
        {
            if (j.State == "queued") j.State = "expired";
            else if (j.State == "delivered") j.State = "unknown"; // expired_without_ack
        }
    }

    public ClaimResponse Claim(int max) => Call("claim", () =>
    {
        SweepExpired();
        var cancel = _order.Select(id => _jobs[id]).Where(j => j.CancelRequested && j.State is ("delivered" or "accepted_by_agent"))
            .Select(j => j.Envelope.JobId).ToList();
        if (Paused) return new ClaimResponse([], cancel, true);

        var jobs = new List<JobEnvelope>();
        foreach (var j in _order.Select(id => _jobs[id])
                     .Where(j => j.Envelope.AuthorizationExpiresAt > Now && (j.State == "queued" || (j.State == "delivered" && j.LeaseUntil < Now)))
                     .Take(max).ToList())
        {
            var attempt = j.DeliveryAttempts + 1;
            if (attempt > ContractInfo.MaxDeliveryAttempts) { j.State = "unknown"; continue; } // delivery_attempts_exhausted
            j.State = "delivered";
            j.DeliveryAttempts = attempt;
            j.LeaseUntil = Now + Lease;
            jobs.Add(j.Envelope with { DeliveryAttempt = attempt, LeaseUntil = j.LeaseUntil });
        }
        return new ClaimResponse(jobs, cancel, false);
    });

    public PayloadResponse GetPayload(Guid jobId) => Call("payload", () =>
    {
        if (!_jobs.TryGetValue(jobId, out var j) || j.State is not ("delivered" or "accepted_by_agent")) throw new ErpException(404, "JOB_NOT_FOUND");
        if (j.Envelope.AuthorizationExpiresAt < Now) throw new ErpException(410, "JOB_EXPIRED");
        var bytes = (byte[])j.Payload.Clone();
        if (CorruptPayloadDownloads > 0) { CorruptPayloadDownloads--; bytes[0] ^= 0xFF; }
        return new PayloadResponse(bytes, j.Envelope.Output.Sha256);
    });

    public AcceptResponse Accept(Guid jobId, int attempt) => Call("accept", () =>
    {
        if (!_jobs.TryGetValue(jobId, out var j)) throw new ErpException(404, "JOB_NOT_FOUND");
        if (j.State is "accepted_by_agent" or "submitting" or "submitted_to_spooler" or "confirmed_completed" or "unknown")
            return new AcceptResponse(j.State, j.CancelRequested, true);
        if (j.State != "delivered" || j.DeliveryAttempts != attempt) throw new ErpException(409, "LEASE_LOST");
        if (j.Envelope.AuthorizationExpiresAt < Now) throw new ErpException(410, "JOB_EXPIRED");
        j.State = "accepted_by_agent";
        j.LeaseUntil = j.Envelope.AuthorizationExpiresAt;
        return new AcceptResponse(j.State, j.CancelRequested, false);
    });

    public EventsResponse PostEvents(Guid jobId, IReadOnlyList<StatusEvent> events) => Call("events", () =>
    {
        if (events.Count is < 1 or > IErpClient.MaxEventsPerRequest) throw new ErpException(422, "SYSTEM_VALIDATION_ERROR");
        var results = new List<EventResult>();
        foreach (var ev in events.OrderBy(e => e.Seq)) // each event its own transaction; a hard error stops the batch
        {
            if (!_jobs.TryGetValue(jobId, out var j)) throw new ErpException(404, "JOB_NOT_FOUND");
            var state = JobStateMachine.WireName(ev.State);

            var byId = _jobs.Values.SelectMany(x => x.AgentEvents.Select(e => (Job: x, Ev: e))).FirstOrDefault(x => x.Ev.EventId == ev.EventId);
            if (byId.Job is not null)
            {
                if (byId.Job == j && byId.Ev.Seq == ev.Seq && byId.Ev.State == state) { results.Add(new(ev.EventId, false, true, false)); continue; }
                throw new ErpException(409, "EVENT_CONFLICT");
            }
            var bySeq = j.AgentEvents.FirstOrDefault(e => e.Seq == ev.Seq);
            if (bySeq.EventId != Guid.Empty)
            {
                if (bySeq.State == state) { results.Add(new(ev.EventId, false, true, false)); continue; }
                throw new ErpException(409, "EVENT_CONFLICT");
            }
            if (state == "confirmed_completed" && !Evidence.IsKnownLevel(ev.Evidence?.Level)) throw new ErpException(422, "EVIDENCE_REQUIRED");

            void Record(bool applied) => j.AgentEvents.Add((ev.EventId, ev.Seq, state, applied, ev.ErrorCode, ev.Evidence?.Level));
            if (ev.Seq <= j.LastSeq) { Record(false); results.Add(new(ev.EventId, false, false, true)); continue; }
            if (Terminal.Contains(j.State)) { Record(false); results.Add(new(ev.EventId, false, false, true)); continue; }
            if (!AgentTrans.GetValueOrDefault(j.State, []).Contains(state)) throw new ErpException(409, "TRANSITION_INVALID");
            Record(true);
            j.State = state;
            j.LastSeq = ev.Seq;
            results.Add(new(ev.EventId, true, false, false));
        }
        if (OmitLastEventResultOnce) { OmitLastEventResultOnce = false; results.RemoveAt(results.Count - 1); }
        var cur = _jobs.GetValueOrDefault(jobId);
        return new EventsResponse(results, cur?.State, cur?.LastSeq ?? 0);
    });

    public IReadOnlyList<ReconcileDirective> Reconcile(IReadOnlyList<LocalJobReport> jobs) => Call("reconcile", () =>
    {
        if (jobs.Count > IErpClient.MaxReconcileJobs) throw new ErpException(422, "SYSTEM_VALIDATION_ERROR");
        var directives = new List<ReconcileDirective>();
        foreach (var lj in jobs)
        {
            if (!_jobs.TryGetValue(lj.JobId, out var j)) { directives.Add(new(lj.JobId, ReconcileAction.Abandon, null)); continue; }
            var localSubmitted = lj.State is "submitting" or "submitted_to_spooler" or "confirmed_completed";
            if (j.State == "delivered" && (lj.State == "accepted_by_agent" || localSubmitted))
            {
                j.State = "accepted_by_agent"; // reconcile_implicit_ack (no expiry check in the ERP)
                j.LeaseUntil = j.Envelope.AuthorizationExpiresAt;
            }
            if (localSubmitted) directives.Add(new(lj.JobId, ReconcileAction.ReportEvents, j.LastSeq + 1));
            else if (Terminal.Contains(j.State) || j.CancelRequested) directives.Add(new(lj.JobId, ReconcileAction.Abandon, null));
            else directives.Add(new(lj.JobId, ReconcileAction.Resume, null));
        }
        return directives;
    });
}
