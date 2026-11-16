using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Journal;
using LabelPrinter.Agent.Core.Spooling;

namespace LabelPrinter.Agent.Core;

public enum AcceptOutcome
{
    /// <summary>Persisted now. Only this outcome and <see cref="AlreadyKnown"/> may be acknowledged to the ERP.</summary>
    AcceptedNew,
    /// <summary>Same identity, same immutable content: nothing changed, existing state returned.</summary>
    AlreadyKnown,
    /// <summary>Same identity, different immutable content.</summary>
    Conflict,
    /// <summary>Failed validation; nothing persisted.</summary>
    Rejected,
}

public sealed record AcceptResult(AcceptOutcome Outcome, JobState? State, IReadOnlyList<ValidationError> Errors);

public enum SubmitOutcome
{
    /// <summary>The spooler was invoked exactly once; see <see cref="SubmitResult.State"/> for what is known.</summary>
    SpoolerInvoked,
    /// <summary>Ended as Failed before any spooler call (expired authorization, local payload missing or altered).</summary>
    FailedBeforeSubmission,
    /// <summary>The job was not in Accepted; nothing was done. Covers duplicates, Unknown, terminal states.</summary>
    NotSubmittable,
}

public sealed record SubmitResult(SubmitOutcome Outcome, JobState State);

public sealed record RecoveryReport(
    int RecordsReplayed,
    long TornBytesDiscarded,
    IReadOnlyList<Guid> MarkedUnknown,
    IReadOnlyList<Guid> Resumable,
    int BlobsRemoved,
    int BlobCleanupFailures);

/// <summary>The journal could not be written. The operation did not happen.</summary>
public sealed class JournalWriteException(string message, Exception inner) : IOException(message, inner);

/// <summary>A previous write failed in a way that leaves the file state unknown. Reopen to recover.</summary>
public sealed class LedgerFaultedException() : InvalidOperationException("Ledger is faulted after a storage failure; reopen it.");

public sealed class JournalMismatchException(string message) : IOException(message);

/// <summary>Submission is suspended because the journal may be older than reality. See docs/durability.md.</summary>
public sealed class LedgerQuarantinedException(string reason)
    : InvalidOperationException("Ledger is quarantined (" + reason + "); an operator must clear it.");

/// <summary>
/// The portable agent core: one append-only journal is the single source of truth for acceptance, identity and
/// content hash, state, and the outbox of acknowledgements still owed to the ERP. Every change is written and
/// flushed to the journal first and applied to memory second, through the same code path that replays the
/// journal on start, so memory can never be ahead of disk.
/// All members are serialized by one lock; the spooler call happens inside it (one submission at a time).
/// </summary>
public sealed class JobLedger : IDisposable
{
    private sealed class Job
    {
        public required JobEnvelope Envelope;
        public required string ContentHash;
        public JobState State = JobState.Accepted;
        public int Attempt;
        public bool AcceptAcked;
        public int LastSeq;
        public OutboxBlock? Block;
        public readonly List<StatusEvent> Pending = [];
    }

    private readonly Lock _gate = new();
    private readonly IJournalStore _journal;
    private readonly IBlobStore _blobs;
    private readonly TimeProvider _clock;
    private readonly Func<Guid> _newId;
    private readonly Dictionary<Guid, Job> _jobs = [];
    private readonly List<Guid> _order = [];
    private Guid _agentId;
    private string? _origin;
    private string? _quarantine;
    private long _goodLength;
    private long _nextN;
    private byte[] _prevHash;
    private bool _faulted;
    private bool _disposed;

    private JobLedger(IJournalStore journal, IBlobStore blobs, TimeProvider clock, Func<Guid> newId, JournalCodec.DecodeResult decoded)
    {
        _journal = journal; _blobs = blobs; _clock = clock; _newId = newId;
        _goodLength = decoded.GoodLength; _prevHash = decoded.LastHash; _nextN = decoded.Entries.Count;
    }

    public Guid AgentId => _agentId;

    /// <summary>ERP origin recorded when the journal was created (null for journals created without one).</summary>
    public string? Origin => _origin;

    /// <summary>Non-null while submission is suspended; the value is the reason code.</summary>
    public string? QuarantineReason { get { lock (_gate) return _quarantine; } }

    /// <summary>
    /// Opens (or creates) the journal for one paired agent and performs crash recovery:
    /// an incomplete final record is truncated; any other damage throws <see cref="JournalCorruptException"/>;
    /// every job found in Submitting becomes Unknown; Accepted jobs stay resumable.
    /// </summary>
    public static JobLedger Open(IJournalStore journal, IBlobStore blobs, Guid agentId, TimeProvider clock, Func<Guid> newId, out RecoveryReport report)
        => Open(journal, blobs, agentId, null, clock, newId, out report);

    /// <summary>As above, additionally binding the journal to one ERP origin. A journal created for another
    /// origin (or another agent) is refused rather than reused.</summary>
    public static JobLedger Open(IJournalStore journal, IBlobStore blobs, Guid agentId, string? origin, TimeProvider clock, Func<Guid> newId, out RecoveryReport report)
    {
        if (agentId == Guid.Empty) throw new ArgumentException("agent id required", nameof(agentId));
        var decoded = JournalCodec.Decode(journal.ReadAll());
        if (decoded.TornBytes > 0)
        {
            journal.Truncate(decoded.GoodLength);
            journal.Flush();
        }

        var ledger = new JobLedger(journal, blobs, clock, newId, decoded);
        if (decoded.Entries.Count == 0)
        {
            ledger.Commit(new JournalEntry { T = JournalEntry.Header, AgentId = agentId, Origin = origin });
        }
        else
        {
            foreach (var entry in decoded.Entries) ledger.Apply(entry);
            if (ledger._agentId != agentId)
                throw new JournalMismatchException("Journal belongs to a different agent identity; refusing to reuse it.");
            if (origin is not null && !string.Equals(ledger._origin, origin, StringComparison.Ordinal))
                throw new JournalMismatchException("Journal belongs to a different ERP origin; refusing to reuse it.");
        }

        var interrupted = ledger._jobs.Values.Where(j => j.State == JobState.Submitting).ToList();
        foreach (var job in interrupted)
            ledger.Transition(job, JobState.Unknown, errorCode: "INTERRUPTED_SUBMISSION");

        // Payload bytes are only needed until the single submission attempt; drop everything else.
        int removed = 0, failures = 0;
        IReadOnlyList<Guid> stored;
        try { stored = blobs.List(); } catch (Exception ex) when (IsStorageError(ex)) { stored = []; failures++; }
        foreach (var id in stored)
        {
            if (ledger._jobs.TryGetValue(id, out var j) && j.State == JobState.Accepted) continue;
            if (ledger.TryDeleteBlob(id)) removed++; else failures++;
        }

        report = new RecoveryReport(
            decoded.Entries.Count,
            decoded.TornBytes,
            [.. interrupted.Select(j => j.Envelope.JobId)],
            [.. ledger._order.Where(id => ledger._jobs[id].State == JobState.Accepted)],
            removed,
            failures);
        return ledger;
    }

    /// <summary>
    /// Idempotent durable acceptance. Returns <see cref="AcceptOutcome.AcceptedNew"/> only after the acceptance
    /// record has been flushed. A storage failure throws and leaves no trace of the job.
    /// For an already known job the payload argument is ignored.
    /// </summary>
    public AcceptResult Accept(JobEnvelope envelope, ReadOnlyMemory<byte> payload)
    {
        var errors = EnvelopeValidator.ValidateEnvelope(envelope, _agentId);
        if (errors.Count > 0) return new AcceptResult(AcceptOutcome.Rejected, null, errors);
        var hash = ContentHash.Compute(envelope);

        lock (_gate)
        {
            EnsureUsable();
            if (_jobs.TryGetValue(envelope.JobId, out var known))
            {
                if (known.ContentHash != hash)
                    return new AcceptResult(AcceptOutcome.Conflict, known.State, [new("IDENTITY_CONTENT_CONFLICT", "jobId")]);
                // The ERP only accepts an ack that names its current delivery attempt.
                if (!known.AcceptAcked && envelope.DeliveryAttempt > known.Attempt)
                    Commit(new JournalEntry { T = JournalEntry.Redelivered, JobId = envelope.JobId, Attempt = envelope.DeliveryAttempt });
                return new AcceptResult(AcceptOutcome.AlreadyKnown, known.State, []);
            }

            errors = EnvelopeValidator.ValidatePayload(envelope.Output, payload.Span);
            if (errors.Count > 0) return new AcceptResult(AcceptOutcome.Rejected, null, errors);
            if (_clock.GetUtcNow() > envelope.AgentMustNotSubmitAfter)
                return new AcceptResult(AcceptOutcome.Rejected, null, [new("AUTHORIZATION_EXPIRED", "agentMustNotSubmitAfter")]);

            _blobs.Put(envelope.JobId, payload.Span);
            try
            {
                Commit(new JournalEntry { T = JournalEntry.Accepted, Job = envelope, ContentHash = hash });
            }
            catch
            {
                TryDeleteBlob(envelope.JobId);
                throw;
            }
            return new AcceptResult(AcceptOutcome.AcceptedNew, JobState.Accepted, []);
        }
    }

    /// <summary>
    /// Submits an Accepted job to the spooler at most once. The Submitting record is flushed before the spooler
    /// is called; if that write fails this throws and the spooler is never invoked.
    /// If recording the spooler's answer fails, this throws <see cref="JournalWriteException"/>: the job stays
    /// Submitting in memory and becomes Unknown on the next start.
    /// </summary>
    public SubmitResult Submit(Guid jobId, ISpooler spooler)
    {
        ArgumentNullException.ThrowIfNull(spooler);
        lock (_gate)
        {
            EnsureUsable();
            var job = Require(jobId);
            if (job.State != JobState.Accepted) return new SubmitResult(SubmitOutcome.NotSubmittable, job.State);
            if (_quarantine is not null) throw new LedgerQuarantinedException(_quarantine);

            var env = job.Envelope;
            if (_clock.GetUtcNow() > env.AgentMustNotSubmitAfter)
                return FailBeforeSubmission(job, "AUTHORIZATION_EXPIRED");
            var payload = _blobs.TryGet(jobId);
            if (payload is null) return FailBeforeSubmission(job, "LOCAL_PAYLOAD_MISSING");
            if (EnvelopeValidator.ValidatePayload(env.Output, payload).Count > 0)
                return FailBeforeSubmission(job, "PAYLOAD_HASH_MISMATCH");
            var profile = OutputProfile.Find(env.Output.Profile)!; // validated at acceptance

            Transition(job, JobState.Submitting); // point of no return; throws => no side effect

            SpoolResult result;
            try
            {
                result = spooler.Submit(new SpoolRequest(jobId, env.PrinterId, profile.Language, profile.Id, Transport.SpoolerRaw, payload))
                    ?? new SpoolResult(SpoolOutcome.Indeterminate, ErrorCode: "SPOOLER_NO_RESULT");
            }
            catch (Exception)
            {
                result = new SpoolResult(SpoolOutcome.Indeterminate, ErrorCode: "SPOOLER_EXCEPTION");
            }

            try
            {
                switch (result.Outcome)
                {
                    case SpoolOutcome.Accepted:
                        Transition(job, JobState.SubmittedToSpooler, spoolerJobRef: result.SpoolerJobRef);
                        break;
                    case SpoolOutcome.RejectedNothingWritten:
                        Transition(job, JobState.Failed, errorCode: result.ErrorCode ?? "SPOOLER_REJECTED");
                        break;
                    default:
                        Transition(job, JobState.Unknown, errorCode: result.ErrorCode ?? "SPOOLER_INDETERMINATE");
                        break;
                }
            }
            finally
            {
                TryDeleteBlob(jobId); // never needed again: this job is not resubmitted under any outcome
            }
            return new SubmitResult(SubmitOutcome.SpoolerInvoked, job.State);
        }
    }

    /// <summary>
    /// Records a delivered job that was refused before acceptance (invalid envelope, payload that never matched)
    /// as Failed, with one "failed" event to report. Only for an envelope whose job id and agent id are usable;
    /// returns false otherwise or when the job is already known. Nothing is ever submitted for such a job.
    /// </summary>
    public bool RecordRejected(JobEnvelope envelope, string reasonCode)
    {
        if (envelope.JobId == Guid.Empty || envelope.AgentId != _agentId || envelope.DeliveryAttempt < 1) return false;
        lock (_gate)
        {
            EnsureUsable();
            if (_jobs.ContainsKey(envelope.JobId)) return false;
            Commit(new JournalEntry
            {
                T = JournalEntry.Rejected, Job = envelope, ContentHash = ContentHash.Compute(envelope),
                EventId = _newId(), ErrorCode = Clip(reasonCode, 40),
            });
            return true;
        }
    }

    /// <summary>Durably notes that the ERP refused this job's outbox (e.g. 404). Nothing is dropped.</summary>
    public void BlockOutbox(Guid jobId, string code)
    {
        lock (_gate)
        {
            EnsureUsable();
            Require(jobId);
            Commit(new JournalEntry { T = JournalEntry.OutboxBlocked, JobId = jobId, ErrorCode = Clip(code, 40) });
        }
    }

    /// <summary>
    /// Turns jobs left in Submitting into Unknown. Submission is synchronous, so a job seen in Submitting
    /// outside <see cref="Submit"/> lost its outcome record (storage failure after the spooler call).
    /// </summary>
    public IReadOnlyList<Guid> MarkInterruptedSubmissions(string reasonCode = "OUTCOME_NOT_RECORDED")
    {
        lock (_gate)
        {
            EnsureUsable();
            var stuck = _jobs.Values.Where(j => j.State == JobState.Submitting).ToList();
            foreach (var job in stuck) Transition(job, JobState.Unknown, errorCode: reasonCode);
            return [.. stuck.Select(j => j.Envelope.JobId)];
        }
    }

    /// <summary>Suspends all submissions until <see cref="ClearQuarantine"/>. Idempotent.</summary>
    public void Quarantine(string reasonCode)
    {
        lock (_gate)
        {
            EnsureUsable();
            if (_quarantine is not null) return;
            Commit(new JournalEntry { T = JournalEntry.Quarantine, ErrorCode = Clip(reasonCode, 40) });
        }
    }

    /// <summary>Explicit operator action. Every still-Accepted job is failed first: after a suspected rollback
    /// the journal cannot prove they were never submitted, so they are not submitted.</summary>
    public IReadOnlyList<Guid> ClearQuarantine()
    {
        lock (_gate)
        {
            EnsureUsable();
            if (_quarantine is null) return [];
            var doubtful = _jobs.Values.Where(j => j.State == JobState.Accepted).ToList();
            foreach (var job in doubtful) FailBeforeSubmission(job, "QUARANTINE_RECOVERY");
            Commit(new JournalEntry { T = JournalEntry.QuarantineCleared });
            return [.. doubtful.Select(j => j.Envelope.JobId)];
        }
    }

    /// <summary>Cancels a job that has no submission intent yet. Returns false (and changes nothing) otherwise.</summary>
    public bool Cancel(Guid jobId)
    {
        lock (_gate)
        {
            EnsureUsable();
            var job = Require(jobId);
            if (job.State != JobState.Accepted) return false;
            Transition(job, JobState.Cancelled);
            TryDeleteBlob(jobId);
            return true;
        }
    }

    /// <summary>
    /// Gives up a job that was never handed to a spooler (Accepted -> Failed). Returns false, changing nothing,
    /// in any other state: a job that may have reached a spooler cannot be declared failed.
    /// </summary>
    public bool Abandon(Guid jobId, string reasonCode)
    {
        lock (_gate)
        {
            EnsureUsable();
            var job = Require(jobId);
            if (job.State != JobState.Accepted) return false;
            FailBeforeSubmission(job, reasonCode);
            return true;
        }
    }

    /// <summary>Records completion. Allowed from SubmittedToSpooler or Unknown, and only with evidence.</summary>
    public void ConfirmCompleted(Guid jobId, Evidence evidence)
    {
        lock (_gate) { EnsureUsable(); Transition(Require(jobId), JobState.ConfirmedCompleted, evidence: evidence); }
    }

    /// <summary>Unknown -> SubmittedToSpooler, when the job was positively observed in the spooler.</summary>
    public void RecordSpoolerObservation(Guid jobId, string spoolerJobRef)
    {
        lock (_gate) { EnsureUsable(); Transition(Require(jobId), JobState.SubmittedToSpooler, spoolerJobRef: spoolerJobRef); }
    }

    /// <summary>SubmittedToSpooler -> Unknown, when the spooler lost track of the job without a result.</summary>
    public void MarkUnknown(Guid jobId, string reasonCode)
    {
        lock (_gate) { EnsureUsable(); Transition(Require(jobId), JobState.Unknown, errorCode: reasonCode); }
    }

    public JobSnapshot? Find(Guid jobId)
    {
        lock (_gate) return _jobs.TryGetValue(jobId, out var j) ? Snapshot(j) : null;
    }

    public IReadOnlyList<JobSnapshot> Jobs()
    {
        lock (_gate) return [.. _order.Select(id => Snapshot(_jobs[id]))];
    }

    public IReadOnlyList<PendingAccept> PendingAccepts()
    {
        lock (_gate) return [.. _order.Select(id => _jobs[id]).Where(j => !j.AcceptAcked).Select(j => new PendingAccept(j.Envelope.JobId, j.Attempt))];
    }

    /// <summary>Events not yet acknowledged by the ERP, in per-job sequence order.</summary>
    public IReadOnlyList<StatusEvent> PendingEvents()
    {
        lock (_gate) return [.. _order.SelectMany(id => _jobs[id].Pending)];
    }

    public void AcknowledgeAccept(Guid jobId)
    {
        lock (_gate)
        {
            EnsureUsable();
            if (Require(jobId).AcceptAcked) return;
            Commit(new JournalEntry { T = JournalEntry.AcceptAcked, JobId = jobId });
        }
    }

    public void AcknowledgeEvents(Guid jobId, int upToSeq)
    {
        lock (_gate)
        {
            EnsureUsable();
            var job = Require(jobId);
            if (upToSeq > job.LastSeq) throw new ArgumentOutOfRangeException(nameof(upToSeq), "acknowledges an event that was never produced");
            if (!job.Pending.Any(e => e.Seq <= upToSeq)) return;
            Commit(new JournalEntry { T = JournalEntry.EventsAcked, JobId = jobId, Seq = upToSeq });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _journal.Dispose();
        }
    }

    // ---- internals ----

    private SubmitResult FailBeforeSubmission(Job job, string code)
    {
        Transition(job, JobState.Failed, errorCode: code);
        TryDeleteBlob(job.Envelope.JobId);
        return new SubmitResult(SubmitOutcome.FailedBeforeSubmission, job.State);
    }

    private void Transition(Job job, JobState to, string? errorCode = null, string? errorMessage = null, string? spoolerJobRef = null, Evidence? evidence = null)
    {
        JobStateMachine.Ensure(job.State, to, evidence, spoolerJobRef);
        Commit(new JournalEntry
        {
            T = JournalEntry.Transition,
            JobId = job.Envelope.JobId,
            From = job.State,
            To = to,
            Seq = job.LastSeq + 1,
            EventId = _newId(),
            ErrorCode = Clip(errorCode, 40),
            ErrorMessage = Clip(errorMessage, 200),
            SpoolerJobRef = Clip(spoolerJobRef, 80),
            EvidenceLevel = evidence?.Level,
            EvidenceDetail = Clip(evidence?.Detail, 120),
        });
    }

    /// <summary>Write, flush, then apply. Nothing is applied to memory unless the flush returned.</summary>
    private void Commit(JournalEntry entry)
    {
        entry.N = _nextN;
        entry.At = _clock.GetUtcNow();
        var frame = JournalCodec.Encode(entry, _prevHash, out var hash);
        try
        {
            _journal.Append(frame);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            // A partial frame may be on disk. Remove it so the next append starts at a clean boundary.
            try { _journal.Truncate(_goodLength); _journal.Flush(); }
            catch (Exception inner) when (IsStorageError(inner)) { _faulted = true; }
            throw new JournalWriteException("Journal append failed; the operation was not recorded.", ex);
        }
        try
        {
            _journal.Flush();
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            // After a failed flush the on-disk state is unknowable; retrying a flush proves nothing.
            _faulted = true;
            throw new JournalWriteException("Journal flush failed; durability of the operation is unknown.", ex);
        }
        _goodLength += frame.Length;
        _prevHash = hash;
        _nextN++;
        Apply(entry);
    }

    /// <summary>Single mutation path for live commits and replay. Rejects histories the state machine forbids.</summary>
    private void Apply(JournalEntry e)
    {
        static JournalCorruptException Bad(JournalEntry e, string why) => new(-1, $"record {e.N} ({e.T}): {why}");

        if (e.T == JournalEntry.Header)
        {
            if (e.N != 0 || e.AgentId is not { } agent || agent == Guid.Empty) throw Bad(e, "misplaced or incomplete header");
            _agentId = agent;
            _origin = e.Origin;
            return;
        }
        if (e.N == 0) throw Bad(e, "journal does not start with a header");

        if (e.T == JournalEntry.Quarantine) { _quarantine = e.ErrorCode ?? "UNSPECIFIED"; return; }
        if (e.T == JournalEntry.QuarantineCleared) { _quarantine = null; return; }

        if (e.T == JournalEntry.Rejected)
        {
            if (e.Job is null || e.ContentHash is null || e.EventId is not { } rejectedEvent || _jobs.ContainsKey(e.Job.JobId)) throw Bad(e, "invalid rejection");
            if (e.Job.AgentId != _agentId) throw Bad(e, "job of another agent");
            var rejected = new Job { Envelope = e.Job, ContentHash = e.ContentHash, Attempt = e.Job.DeliveryAttempt, State = JobState.Failed, AcceptAcked = true, LastSeq = 1 };
            rejected.Pending.Add(new StatusEvent(rejectedEvent, e.Job.JobId, 1, rejected.Attempt, JobState.Failed, e.At, e.ErrorCode, null, null, null));
            _jobs.Add(e.Job.JobId, rejected);
            _order.Add(e.Job.JobId);
            return;
        }

        if (e.T == JournalEntry.Accepted)
        {
            if (e.Job is null || e.ContentHash is null || _jobs.ContainsKey(e.Job.JobId)) throw Bad(e, "invalid acceptance");
            if (e.Job.AgentId != _agentId) throw Bad(e, "job of another agent");
            _jobs.Add(e.Job.JobId, new Job { Envelope = e.Job, ContentHash = e.ContentHash, Attempt = e.Job.DeliveryAttempt });
            _order.Add(e.Job.JobId);
            return;
        }

        if (e.JobId is not { } id || !_jobs.TryGetValue(id, out var job)) throw Bad(e, "unknown job");
        switch (e.T)
        {
            case JournalEntry.Redelivered:
                if (e.Attempt is not { } attempt || attempt <= job.Attempt) throw Bad(e, "attempt does not advance");
                job.Attempt = attempt;
                break;
            case JournalEntry.AcceptAcked:
                job.AcceptAcked = true;
                job.Block = null;
                break;
            case JournalEntry.OutboxBlocked:
                job.Block = new OutboxBlock(e.ErrorCode ?? "UNSPECIFIED", (job.Block?.Count ?? 0) + 1, e.At);
                break;
            case JournalEntry.EventsAcked:
                if (e.Seq is not { } upTo || upTo > job.LastSeq) throw Bad(e, "acknowledges a missing event");
                job.Pending.RemoveAll(p => p.Seq <= upTo);
                job.Block = null;
                break;
            case JournalEntry.Transition:
                if (e.From != job.State || e.To is not { } to || e.Seq != job.LastSeq + 1 || e.EventId is not { } eventId)
                    throw Bad(e, "transition does not follow the recorded state");
                var evidence = e.EvidenceLevel is null ? null : new Evidence(e.EvidenceLevel, e.EvidenceDetail);
                try { JobStateMachine.Ensure(job.State, to, evidence, e.SpoolerJobRef); }
                catch (InvalidTransitionException ex) { throw Bad(e, ex.Message); }
                job.State = to;
                job.LastSeq = e.Seq!.Value;
                job.Pending.Add(new StatusEvent(eventId, id, job.LastSeq, job.Attempt, to, e.At, e.ErrorCode, e.ErrorMessage, e.SpoolerJobRef, evidence));
                break;
            default:
                throw Bad(e, "unknown record type");
        }
    }

    private Job Require(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : throw new KeyNotFoundException($"Job {jobId} is not in the journal.");

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) throw new LedgerFaultedException();
    }

    private bool TryDeleteBlob(Guid jobId)
    {
        try { _blobs.Delete(jobId); return true; }
        catch (Exception ex) when (IsStorageError(ex)) { return false; } // retried by cleanup on next start
    }

    private JobSnapshot Snapshot(Job j) =>
        new(new JobKey(_agentId, j.Envelope.JobId), j.State, j.ContentHash, j.Attempt, j.AcceptAcked, j.LastSeq, j.Envelope, j.Block);

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException;

    /// <summary>Keeps free text short and single-line before it reaches the journal or the ERP.</summary>
    private static string? Clip(string? value, int max)
    {
        if (value is null) return null;
        var clean = new string([.. value.Select(c => char.IsControl(c) ? ' ' : c)]);
        return clean.Length <= max ? clean : clean[..max];
    }
}
