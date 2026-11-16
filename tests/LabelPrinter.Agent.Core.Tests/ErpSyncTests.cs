using System.Text;
using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Erp;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Spooling;
using LabelPrinter.Agent.Core.Tests.Fakes;
using LabelPrinter.Agent.Core.Tests.Harness;

namespace LabelPrinter.Agent.Core.Tests;

/// <summary>Agent core + ErpSync against the in-memory mock ERP. No network, no real ERP, no printer.</summary>
public sealed class Bench
{
    public Rig R { get; } = new();
    public MockErp Erp { get; }
    public ErpSync Sync { get; private set; }

    public Bench()
    {
        Erp = new MockErp(R.Clock);
        Sync = new ErpSync(R.Ledger, Erp, R.Spooler, R.Clock);
    }

    public Guid Enqueue(int job = 1, int copies = 1, Guid? reprintOf = null)
    {
        var payload = Rig.Payload(job);
        Erp.Enqueue(Rig.Envelope(payload, job, copies, reprintOf: reprintOf), payload);
        return Rig.JobId(job);
    }

    /// <summary>Process death and restart: new ledger from the journal image, new ErpSync (reconciles first).</summary>
    public void Restart(byte[]? image = null)
    {
        R.Restart(image);
        Sync = new ErpSync(R.Ledger, Erp, R.Spooler, R.Clock);
    }

    public SyncReport Cycle() => Sync.RunCycle();
    public JobState Local(Guid id) => R.Ledger.Find(id)!.State;
    public string Server(Guid id) => Erp.Job(id).State;
    /// <summary>Calls received by the fake spooler. Says nothing about paper.</summary>
    public int FakeSubmissions => R.Spooler.Submissions.Count;

    public void FailNextJournalAppend()
    {
        var target = R.Journal.Appends + 1;
        R.Journal.AppendFault = n => n == target ? (0, new IOException("disk full")) : null;
    }

    public void OutboxEmpty()
    {
        Check.Equal(0, R.Ledger.PendingAccepts().Count, "pending accepts");
        Check.Equal(0, R.Ledger.PendingEvents().Count, "pending events");
    }
}

public sealed class ClaimAndAcceptanceTests
{
    private readonly Bench _b = new();

    [Test] public void ClaimAcceptSubmitReportConfirm()
    {
        var id = _b.Enqueue();
        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Accepted, id) && report.Has(SyncReport.Submitted, id), "cycle notes");
        Check.Equal("submitted_to_spooler", _b.Server(id), "ERP sees the spooler acknowledgement, not completion");
        Check.Equal(1, _b.FakeSubmissions, "submitted to fake spooler once");
        _b.OutboxEmpty();

        _b.R.Ledger.ConfirmCompleted(id, new Evidence(Evidence.SpoolerReportedComplete, "queue empty"));
        _b.Cycle();
        Check.Equal("confirmed_completed", _b.Server(id), "completion with evidence");
        Check.Equal(Evidence.SpoolerReportedComplete, _b.Erp.Job(id).AgentEvents.Last().EvidenceLevel, "evidence reached the ERP");
        Check.Equal(1, _b.FakeSubmissions, "still one fake-spooler submission");
    }

    [Test] public void ErpIsNotToldAcceptedBeforeTheJournalHoldsTheJob()
    {
        var id = _b.Enqueue();
        _b.FailNextJournalAppend(); // the acceptance record
        Check.Throws<JournalWriteException>(() => _b.Cycle(), "storage failure surfaces");
        Check.Equal(0, _b.Erp.CallCount("accept"), "no acknowledgement without durable acceptance");
        Check.Equal("delivered", _b.Server(id), "ERP still waits");
        Check.Equal(0, _b.FakeSubmissions, "nothing submitted");

        _b.R.Clock.Advance(TimeSpan.FromSeconds(61)); // lease expires, ERP redelivers the same job
        _b.Cycle();
        Check.Equal(2, _b.Erp.Job(id).DeliveryAttempts, "redelivered");
        Check.Equal("submitted_to_spooler", _b.Server(id), "second delivery completes");
        Check.Equal(1, _b.FakeSubmissions, "submitted to fake spooler once");
    }

    [Test] public void NothingIsSubmittedUntilTheErpConfirmsAcceptance()
    {
        var id = _b.Enqueue();
        _b.Erp.FailNext("accept", MockErp.Fault.RequestLost);
        var report = _b.Cycle();
        Check.True(report.ErpUnavailable, "reported");
        Check.Equal(JobState.Accepted, _b.Local(id), "durably accepted, waiting");
        Check.Equal(0, _b.FakeSubmissions, "not submitted without confirmation");

        _b.Cycle();
        Check.Equal(1, _b.FakeSubmissions, "submitted to fake spooler after confirmation");
        Check.Equal("submitted_to_spooler", _b.Server(id), "state");
    }

    [Test] public void LostAcceptResponseIsRetriedAndAnsweredAsDuplicate()
    {
        var id = _b.Enqueue();
        _b.Erp.FailNext("accept", MockErp.Fault.ResponseLost);
        _b.Cycle();
        Check.Equal("accepted_by_agent", _b.Server(id), "ERP applied it");
        Check.Equal(1, _b.R.Ledger.PendingAccepts().Count, "agent does not know yet");
        _b.Cycle();
        Check.Equal(2, _b.Erp.CallCount("accept"), "retried once");
        Check.Equal(1, _b.FakeSubmissions, "submitted to fake spooler once");
        _b.OutboxEmpty();
    }

    [Test] public void LostClaimResponseIsRecoveredByRedeliveryOfTheSameJob()
    {
        var id = _b.Enqueue();
        _b.Erp.FailNext("claim", MockErp.Fault.ResponseLost);
        _b.Cycle();
        Check.True(_b.R.Ledger.Find(id) is null, "agent never saw the job");
        _b.Cycle();
        Check.True(_b.R.Ledger.Find(id) is null, "lease still running: no second delivery yet");

        _b.R.Clock.Advance(TimeSpan.FromSeconds(61));
        _b.Cycle();
        Check.Equal(2, _b.R.Ledger.Find(id)!.DeliveryAttempt, "second delivery attempt");
        Check.Equal(1, _b.FakeSubmissions, "submitted to fake spooler once");
    }

    [Test] public void LeaseLostIsRepairedByReconcileNotByGuessing()
    {
        var id = _b.Enqueue();
        _b.Erp.FailNext("accept", MockErp.Fault.RequestLost);
        _b.Cycle();
        _b.Erp.Job(id).DeliveryAttempts = 2; // a redelivery whose claim response never arrived

        _b.Cycle();
        Check.Equal(0, _b.FakeSubmissions, "LEASE_LOST: wait");
        Check.Equal(JobState.Accepted, _b.Local(id), "kept, not failed");
        _b.Cycle(); // reconcile → implicit ack on the server → accept answered as duplicate
        Check.Equal(1, _b.FakeSubmissions, "submitted to fake spooler once");
        Check.Equal("submitted_to_spooler", _b.Server(id), "state");
    }

    [Test] public void CorruptedDownloadIsRefetchedOnce()
    {
        var id = _b.Enqueue();
        _b.Erp.CorruptPayloadDownloads = 1;
        _b.Cycle();
        Check.Equal(2, _b.Erp.CallCount("payload"), "one refetch");
        Check.True(_b.R.Spooler.Submissions.Single().Payload.AsSpan().SequenceEqual(Rig.Payload(1)), "approved bytes submitted");
    }

    [Test] public void PersistentlyCorruptPayloadIsRefusedAndReportedFailed()
    {
        var id = _b.Enqueue();
        _b.Erp.CorruptPayloadDownloads = 2;
        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Rejected, id), "reported locally");
        Check.Equal(JobState.Failed, _b.Local(id), "refusal is durable");
        Check.Equal(0, _b.Erp.CallCount("accept"), "never acknowledged as accepted");
        Check.Equal("failed", _b.Server(id), "ERP is told, instead of drifting to unknown");
        Check.Equal("PAYLOAD_HASH_MISMATCH", _b.Erp.Job(id).AgentEvents.Single().ErrorCode, "with the reason");
        Check.Equal(0, _b.FakeSubmissions, "not submitted");
        _b.Restart();
        _b.Cycle();
        Check.Equal(1, _b.Erp.Job(id).AgentEvents.Count, "reported once");
    }

    [Test] public void InvalidEnvelopeIsRefusedBeforeAnyDownloadAndReportedFailed()
    {
        var payload = Rig.Payload();
        _b.Erp.Enqueue(Rig.Envelope(payload) with { ContractVersion = "9.9" }, payload);
        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Rejected, Rig.JobId(1)), "reported locally");
        Check.Equal(0, _b.Erp.CallCount("payload"), "no download");
        Check.Equal("failed", _b.Server(Rig.JobId(1)), "ERP is told");
        Check.Equal("ENVELOPE_INVALID", _b.Erp.Job(Rig.JobId(1)).AgentEvents.Single().ErrorCode, "with the reason");
        Check.Equal(0, _b.FakeSubmissions, "not submitted");
    }

    [Test] public void EnvelopeForAnotherAgentIsNotRecordedOrReported()
    {
        var payload = Rig.Payload();
        _b.Erp.Enqueue(Rig.Envelope(payload) with { AgentId = Guid.NewGuid() }, payload);
        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Rejected, Rig.JobId(1)), "noted");
        Check.Equal(0, _b.R.Ledger.Jobs().Count, "no record under an identity that is not ours");
        Check.Equal(0, _b.Erp.CallCount("events"), "nothing reported");
    }

    [Test] public void SameJobIdWithDifferentContentIsNeverSubmitted()
    {
        var id = _b.Enqueue();
        _b.Erp.FailNext("accept", MockErp.Fault.RequestLost);
        _b.Cycle();
        var server = _b.Erp.Job(id);
        server.Envelope = server.Envelope with { Copies = 3 };
        server.DeliveryAttempts = 2;
        server.LeaseUntil = _b.R.Clock.Now.AddSeconds(-1);

        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Conflict, id), "reported");
        _b.Cycle();
        Check.Equal(JobState.Failed, _b.Local(id), "stopped");
        Check.Equal(0, _b.FakeSubmissions, "neither version submitted");
    }

    [Test] public void PausedErpDeliversNothing()
    {
        _b.Enqueue();
        _b.Erp.Paused = true;
        var report = _b.Cycle();
        Check.True(report.Paused, "reported");
        Check.Equal(0, _b.R.Ledger.Jobs().Count, "nothing claimed");
    }

    [Test] public void CopiesAreNotMultipliedEndToEnd()
    {
        _b.Enqueue(copies: 7);
        _b.Cycle();
        _b.Cycle();
        Check.Equal(1, _b.FakeSubmissions, "one submission for seven copies");
    }
}

public sealed class EventDeliveryTests
{
    private readonly Bench _b = new();

    [Test] public void LostEventResponseIsResentAndDeduplicatedByTheErp()
    {
        var id = _b.Enqueue();
        _b.Erp.FailNext("events", MockErp.Fault.ResponseLost);
        _b.Cycle();
        Check.Equal("submitted_to_spooler", _b.Server(id), "ERP applied the events");
        Check.Equal(2, _b.R.Ledger.PendingEvents().Count, "agent keeps them until answered");

        _b.Cycle();
        _b.OutboxEmpty();
        Check.Equal(2, _b.Erp.Job(id).AgentEvents.Count, "same event ids: nothing stored twice");
        Check.Sequence([1, 2], _b.Erp.Job(id).AgentEvents.Select(e => e.Seq), "sequence");
        Check.Equal(1, _b.FakeSubmissions, "submitted to fake spooler once");
    }

    [Test] public void EventsSurviveOutageAndRestartWithStableIds()
    {
        var id = _b.Enqueue();
        _b.R.Spooler.OnSubmit = _ => _b.Erp.Down = true; // ERP disappears while the job is in the spooler
        var report = _b.Cycle();
        Check.True(report.ErpUnavailable, "reported");
        var owed = _b.R.Ledger.PendingEvents().Select(e => e.EventId).ToList();
        Check.Equal(2, owed.Count, "events owed");

        _b.Restart();
        _b.Erp.Down = false;
        _b.Cycle();
        Check.Sequence(owed, _b.Erp.Job(id).AgentEvents.Select(e => e.EventId), "the same events arrive after restart");
        _b.OutboxEmpty();
        Check.Equal(1, _b.FakeSubmissions, "submitted to fake spooler once");
    }

    [Test] public void ReconcileTrimsEventsTheErpAlreadyHas()
    {
        var id = _b.Enqueue();
        _b.Erp.FailNext("events", MockErp.Fault.ResponseLost);
        _b.Cycle();
        var before = _b.Erp.CallCount("events");
        _b.Restart();
        _b.Cycle(); // report_events fromSeq = 3
        _b.OutboxEmpty();
        Check.Equal(before, _b.Erp.CallCount("events"), "nothing needed resending");
    }

    [Test] public void RejectedEventsBlockThatJobDurablyAndAreRetriedWithBackoff()
    {
        var id = _b.Enqueue();
        _b.R.Spooler.OnSubmit = _ => _b.Erp.Down = true;
        _b.Cycle();
        _b.Erp.Down = false;
        _b.Erp.Job(id).State = "queued"; // a server state from which the agent's events are not permitted

        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Blocked, id), "reported");
        Check.Equal(2, _b.R.Ledger.PendingEvents().Count, "kept, not dropped");
        Check.Equal("TRANSITION_INVALID", _b.R.Ledger.Find(id)!.Block!.Code, "diagnosable");
        var calls = _b.Erp.CallCount("events");
        _b.Cycle();
        _b.Restart();
        _b.Cycle();
        Check.Equal(calls, _b.Erp.CallCount("events"), "no busy retry, not even across a restart");

        _b.Erp.Job(id).State = "accepted_by_agent"; // the server-side cause is fixed
        _b.R.Clock.Advance(TimeSpan.FromSeconds(61));
        _b.Cycle();
        _b.OutboxEmpty();
        Check.True(_b.R.Ledger.Find(id)!.Block is null, "block cleared by a real acknowledgement");
        Check.Equal(JobState.SubmittedToSpooler, _b.Local(id), "local truth unchanged throughout");
        Check.Equal(1, _b.FakeSubmissions, "one fake-spooler submission");
    }

    [Test] public void OnlyEventsCoveredByTheAnswerAreAcknowledged()
    {
        var id = _b.Enqueue();
        _b.Erp.OmitLastEventResultOnce = true;
        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Blocked, id), "incomplete answer reported");
        Check.Sequence([2], _b.R.Ledger.PendingEvents().Select(e => e.Seq), "the uncovered event stays owed");
        _b.Restart();
        _b.Cycle();
        _b.OutboxEmpty();
        Check.Equal(1, _b.FakeSubmissions, "submitted to fake spooler once");
    }

    [Test] public void NotFoundIsNeverAnAcknowledgementAndEventsSurviveUntilTheErpKnowsTheJobAgain()
    {
        var id = _b.Enqueue();
        _b.R.Spooler.OnSubmit = _ => _b.Erp.Down = true;
        _b.Cycle();
        _b.Erp.Down = false;
        var owed = _b.R.Ledger.PendingEvents().Select(e => (e.EventId, e.Seq)).ToList();
        var gone = _b.Erp.Remove(id); // e.g. the agent is pointed at the wrong ERP database

        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Blocked, id), "reported");
        Check.Equal("JOB_NOT_FOUND", _b.R.Ledger.Find(id)!.Block!.Code, "diagnosable");
        Check.Sequence(owed, _b.R.Ledger.PendingEvents().Select(e => (e.EventId, e.Seq)), "nothing dropped");

        var calls = _b.Erp.CallCount("events");
        for (var i = 0; i < 3; i++) { _b.Cycle(); _b.Restart(); }
        Check.Equal(calls, _b.Erp.CallCount("events"), "no busy retry inside the backoff window");
        Check.Equal(1, _b.R.Ledger.Find(id)!.Block!.Count, "block survives restarts");

        _b.R.Clock.Advance(TimeSpan.FromSeconds(61));
        _b.Cycle();
        Check.Equal(2, _b.R.Ledger.Find(id)!.Block!.Count, "still 404: blocked again, longer backoff");
        _b.R.Clock.Advance(TimeSpan.FromSeconds(61));
        _b.Cycle();
        Check.Equal(calls + 1, _b.Erp.CallCount("events"), "second retry waits two minutes");

        _b.Erp.Restore(gone);
        _b.R.Clock.Advance(TimeSpan.FromMinutes(2));
        _b.Cycle();
        Check.Sequence(owed, _b.Erp.Job(id).AgentEvents.Select(e => (e.EventId, e.Seq)), "same event ids and sequence numbers arrive");
        _b.OutboxEmpty();
        Check.Equal(1, _b.FakeSubmissions, "never resubmitted to the fake spooler");
    }

    [Test] public void ErpOutageNeverThrowsAndNeverBlocksLocalTruth()
    {
        _b.Erp.Down = true;
        var report = _b.Cycle();
        Check.True(report.ErpUnavailable, "reported");
        _b.Erp.Down = false;
        var id = _b.Enqueue();
        _b.Cycle();
        Check.Equal("submitted_to_spooler", _b.Server(id), "recovers");
    }
}

public sealed class ReconciliationAndRestartTests
{
    private readonly Bench _b = new();

    private Guid AcceptedAndConfirmedButNotSubmitted()
    {
        var id = _b.Enqueue();
        var failing = _b.R.Journal.Appends + 3; // accepted, accept_acked, then the Submitting record
        _b.R.Journal.AppendFault = n => n == failing ? (0, new IOException("disk full")) : null;
        Check.Throws<JournalWriteException>(() => _b.Cycle(), "intent could not be persisted");
        Check.Equal(JobState.Accepted, _b.Local(id), "fixture: accepted");
        Check.Equal("accepted_by_agent", _b.Server(id), "fixture: confirmed by ERP");
        Check.Equal(0, _b.FakeSubmissions, "fixture: not submitted");
        return id;
    }

    [Test] public void CrashDuringSubmissionEndsAsUnknownOnBothSidesAndIsNeverResubmitted()
    {
        var id = _b.Enqueue();
        byte[]? atCrash = null;
        _b.R.Spooler.OnSubmit = _ => { atCrash = _b.R.Journal.CrashImage(); _b.Erp.Down = true; };
        _b.Cycle();

        _b.Restart(atCrash);
        _b.R.Spooler.OnSubmit = null;
        _b.Erp.Down = false;
        for (var i = 0; i < 3; i++) _b.Cycle();

        Check.Equal(JobState.Unknown, _b.Local(id), "agent");
        Check.Equal("unknown", _b.Server(id), "ERP");
        Check.Equal("INTERRUPTED_SUBMISSION", _b.Erp.Job(id).AgentEvents.Last().ErrorCode, "reason reported");
        Check.Equal(1, _b.FakeSubmissions, "one submission attempt in total");
        Check.Equal(1, _b.Erp.Job(id).DeliveryAttempts, "ERP did not redeliver");
    }

    [Test] public void ErpSaysResumeForAnUnknownJobAndTheAgentDoesNotResubmit()
    {
        var id = _b.Enqueue();
        _b.R.Spooler.Then(new SpoolResult(SpoolOutcome.Indeterminate, ErrorCode: "WRITE_TIMEOUT"));
        _b.R.Spooler.OnSubmit = _ => _b.Erp.Down = true;
        _b.Cycle();
        _b.Erp.Down = false;
        Check.Equal(JobState.Unknown, _b.Local(id), "fixture");

        var directive = _b.Erp.Reconcile([new LocalJobReport(id, "unknown", 2)]).Single();
        Check.Equal(ReconcileAction.Resume, directive.Action, "verified ERP behaviour: resume even for unknown");

        _b.Restart();
        for (var i = 0; i < 3; i++) _b.Cycle();
        Check.Equal(1, _b.FakeSubmissions, "no automatic resubmission");
        Check.Equal(JobState.Unknown, _b.Local(id), "agent");
        Check.Equal("unknown", _b.Server(id), "ERP learns it is unknown");
    }

    [Test] public void AcceptedNeverSubmittedJobContinuesAfterRestart()
    {
        var id = AcceptedAndConfirmedButNotSubmitted();
        _b.Restart();
        _b.Cycle();
        Check.Equal(1, _b.FakeSubmissions, "submitted to fake spooler once");
        Check.Equal("submitted_to_spooler", _b.Server(id), "state");
        _b.OutboxEmpty();
    }

    [Test] public void ExplicitReprintIsASeparateJob()
    {
        var original = _b.Enqueue(1);
        _b.R.Spooler.Then(new SpoolResult(SpoolOutcome.Indeterminate));
        _b.Cycle();
        Check.Equal("unknown", _b.Server(original), "fixture");

        var reprint = _b.Enqueue(2, reprintOf: original);
        _b.Cycle();
        Check.Equal("submitted_to_spooler", _b.Server(reprint), "reprint submitted");
        Check.Equal("unknown", _b.Server(original), "original untouched");
        Check.Sequence([original, reprint], _b.R.Spooler.Submissions.Select(s => s.JobId), "each id submitted once");
    }

    [Test] public void JobTheErpAlreadyMarkedUnknownIsReportedFailedNotSubmitted()
    {
        var id = _b.Enqueue();
        _b.Erp.FailNext("accept", MockErp.Fault.RequestLost);
        _b.Cycle();
        _b.Erp.Job(id).State = "unknown"; // server gave up on delivery (expiry without ack / attempts exhausted)

        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Abandoned, id), "reported");
        Check.Equal(0, _b.FakeSubmissions, "not submitted against the server's state");
        Check.Equal(JobState.Failed, _b.Local(id), "agent knows it never submitted");
        Check.Equal("failed", _b.Server(id), "ERP's unknown is resolved by a truthful 'failed'");
    }

    [Test] public void JobMissingOnTheErpIsNotSubmittedAndItsAcceptStaysOwed()
    {
        var id = _b.Enqueue();
        _b.Erp.FailNext("accept", MockErp.Fault.RequestLost);
        _b.Cycle();
        _b.Erp.Remove(id);
        _b.Restart();
        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Abandoned, id) && report.Has(SyncReport.Blocked, id), "reported");
        Check.Equal(JobState.Failed, _b.Local(id), "stopped");
        Check.Equal(0, _b.FakeSubmissions, "not submitted");
        Check.Equal(1, _b.R.Ledger.PendingAccepts().Count, "404 did not acknowledge anything");
        Check.Equal(1, _b.R.Ledger.PendingEvents().Count, "the failure report is kept");
    }

    [Test] public void CancelLearnedThroughReconcileIsReportedAsCancelledNotFailed()
    {
        var id = AcceptedAndConfirmedButNotSubmitted();
        _b.Erp.UserCancel(id);
        _b.Restart(); // reconcile answers "abandon"; /accept then says why
        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Cancelled, id), "reported as cancelled");
        Check.Equal(JobState.Cancelled, _b.Local(id), "agent");
        Check.Equal("cancelled", _b.Server(id), "ERP");
        Check.Equal(0, _b.FakeSubmissions, "not submitted");
    }

    [Test] public void AbandonedJobIsHeldEvenWhenTheFollowUpCallFails()
    {
        var id = AcceptedAndConfirmedButNotSubmitted();
        _b.Erp.UserCancel(id);
        _b.Restart();
        _b.Erp.FailNext("accept", MockErp.Fault.RequestLost);
        _b.Cycle();
        Check.Equal(0, _b.FakeSubmissions, "held: not submitted while the reason is unknown");
        _b.Cycle();
        Check.Equal(JobState.Cancelled, _b.Local(id), "resolved on the next cycle");
        Check.Equal(0, _b.FakeSubmissions, "not submitted");
    }

    [Test] public void CancelRequestedBeforeSubmissionIsHonoured()
    {
        var id = AcceptedAndConfirmedButNotSubmitted();
        Check.Equal("cancel_requested", _b.Erp.UserCancel(id), "ERP outcome");
        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Cancelled, id), "reported");
        Check.Equal("cancelled", _b.Server(id), "ERP");
        Check.Equal(0, _b.FakeSubmissions, "not submitted");
    }

    [Test] public void CancelAfterSubmissionIsTooLate()
    {
        var id = _b.Enqueue();
        _b.Cycle();
        Check.Equal("too_late", _b.Erp.UserCancel(id), "ERP outcome");
        _b.Cycle();
        Check.Equal(JobState.SubmittedToSpooler, _b.Local(id), "unchanged");
    }

    [Test] public void AuthorizationRunsOutWhileErpIsUnreachable()
    {
        var id = _b.Enqueue();
        _b.Erp.FailNext("accept", MockErp.Fault.RequestLost);
        _b.Cycle();
        _b.Erp.Down = true;
        _b.R.Clock.Advance(TimeSpan.FromHours(1));
        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Abandoned, id), "reported");
        Check.Equal(0, _b.FakeSubmissions, "never submitted");

        _b.Erp.Down = false;
        _b.Cycle();
        Check.Equal("failed", _b.Server(id), "ERP is told it was not submitted");
        Check.Equal("AUTHORIZATION_EXPIRED", _b.Erp.Job(id).AgentEvents.Last().ErrorCode, "with the reason");
        _b.OutboxEmpty();
    }

    [Test] public void ManyJobsAcrossFaultsAndRestartsReachTheFakeSpoolerOnceEach()
    {
        var ids = Enumerable.Range(1, 8).Select(n => _b.Enqueue(n)).ToList();
        var faults = new[] { "claim", "accept", "events", "payload", "reconcile" };
        for (var round = 0; round < 40; round++)
        {
            _b.Erp.FailNext(faults[round % faults.Length], round % 2 == 0 ? MockErp.Fault.ResponseLost : MockErp.Fault.RequestLost);
            _b.Sync.RunCycle(claimMax: 3);
            if (round % 3 == 2) _b.Restart();
            _b.R.Clock.Advance(TimeSpan.FromSeconds(20));
        }
        for (var i = 0; i < 5; i++) { _b.Cycle(); _b.R.Clock.Advance(TimeSpan.FromSeconds(61)); }

        Check.Sequence(ids.Select(_ => 1), ids.Select(id => _b.R.Spooler.Submissions.Count(s => s.JobId == id)), "submissions per job");
        Check.True(ids.All(id => _b.Server(id) == "submitted_to_spooler"), "ERP and agent agree: " + string.Join(",", ids.Select(_b.Server)));
        _b.OutboxEmpty();
    }
}

public sealed class WireFormatTests
{
    private static ErpErrorBody Parse(string json) => ErpErrorBody.Parse(Encoding.UTF8.GetBytes(json));

    [Test] public void FlatErrorBodyFromTheGlobalHandlerIsRead()
    {
        var e = Parse("""{"error":"Error","message":"x","trace_id":"t-1","error_code":"LEASE_LOST","user_friendly_message_fa":"y"}""");
        Check.Equal("LEASE_LOST", e.ErrorCode, "stable code comes from error_code");
        Check.Equal("Error", e.Error, "error is only the JS error name");
        Check.Equal("t-1", e.TraceId, "trace id");
    }

    [Test] public void ValidationAndHandlerBypassShapesAreTolerated()
    {
        var validation = Parse("""{"error":"VALIDATION_ERROR","message":"m","details":[{"path":["events"]}],"trace_id":"t","error_code":"SYSTEM_VALIDATION_ERROR"}""");
        Check.Equal("SYSTEM_VALIDATION_ERROR", validation.ErrorCode, "validation");
        var bypass = Parse("""{"error":"VALIDATION_ERROR","message":"m"}""");
        Check.True(bypass.ErrorCode is null, "events jobId mismatch carries no error_code");
    }

    [Test] public void NestedDraftShapeAndGarbageDoNotYieldACode()
    {
        Check.True(Parse("""{"error":{"code":"LEASE_LOST","retryable":false}}""").ErrorCode is null, "the contract draft's shape is not what the ERP sends");
        Check.True(Parse("<html>502</html>").ErrorCode is null, "non-JSON");
        Check.True(Parse("[1]").ErrorCode is null, "non-object");
        Check.True(Parse("""{"error_code":7}""").ErrorCode is null, "wrong type");
    }

    // Built at run time from fixed bytes so that no credential-shaped literal is committed.
    private static string Synthetic(int keyBytes = 16, int secretBytes = 32) =>
        AgentCredential.Scheme + "_" + B64(keyBytes, 0x11) + "." + B64(secretBytes, 0x22);
    private static string B64(int n, byte fill) =>
        Convert.ToBase64String(Enumerable.Repeat(fill, n).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Test] public void CredentialShapeMatchesErpGeneratorAndParser()
    {
        var text = Synthetic();
        Check.True(AgentCredential.TryParse(text, out var c), "16-byte key id + 32-byte secret, base64url");
        Check.Equal(22, c!.KeyId.Length, "key id length");
        Check.Equal("Bearer " + text, c.ToAuthorizationHeader(), "header");
        Check.True(!c.ToString().Contains(B64(32, 0x22), StringComparison.Ordinal), "ToString never reveals the secret");
    }

    [Test] public void MalformedCredentialsAreRejected()
    {
        var good = Synthetic();
        string[] bad =
        [
            "", good.Replace("lpa1_", "lpa2_"), good.Replace(".", ""), good + "=", "Bearer " + good, good.Replace("lpa1_", "LPA1_"),
            AgentCredential.Scheme + "_short.x", good + new string('A', 40), " " + good,
        ];
        foreach (var b in bad) Check.True(!AgentCredential.TryParse(b, out _), "must reject: " + b.Length + " chars");
        Check.True(!AgentCredential.TryParse(null, out _), "null");
    }
}

public sealed class SafetyRecoveryTests
{
    private readonly Bench _b = new();

    [Test] public void LostSpoolerOutcomeBecomesUnknownInTheRunningProcess()
    {
        var id = _b.Enqueue();
        var failing = _b.R.Journal.Appends + 4; // accepted, accept_acked, submitting, then the spooler's answer
        _b.R.Journal.AppendFault = n => n == failing ? (0, new IOException("disk full")) : null;
        Check.Throws<JournalWriteException>(() => _b.Cycle(), "outcome could not be recorded");
        Check.Equal(1, _b.FakeSubmissions, "fake spooler was called");
        Check.Equal(JobState.Submitting, _b.Local(id), "outcome unknown to the journal");

        var report = _b.Cycle(); // same process, storage works again
        Check.True(report.Has(SyncReport.Interrupted, id), "reported");
        Check.Equal(JobState.Unknown, _b.Local(id), "resolved conservatively without a restart");
        Check.Equal("unknown", _b.Server(id), "ERP is told");
        Check.Equal(1, _b.FakeSubmissions, "no blind retry");
    }

    [Test] public void ErpAheadOfJournalQuarantinesAndStopsAllSubmissions()
    {
        var a = _b.Enqueue(1);
        _b.Cycle();
        var beforeSubmit = _b.R.Journal.CrashImage();
        var b = _b.Enqueue(2);
        _b.Erp.FailNext("accept", MockErp.Fault.RequestLost);
        _b.Cycle(); // job 2 accepted locally, waiting for confirmation
        _b.R.Ledger.ConfirmCompleted(a, new Evidence(Evidence.SpoolerReportedComplete, null));
        _b.Cycle();
        Check.Equal(3, _b.Erp.Job(a).LastSeq, "fixture: ERP has three events for job 1");

        // An older copy of the journal is put back: it knows job 1 only up to seq 2 and nothing of job 2's confirmation.
        var old = _b.R.Journal.CrashImage();
        _b.R.Ledger.ConfirmCompleted(b, new Evidence(Evidence.SpoolerReportedComplete, null)); // job 2 completes in "reality"
        _b.Cycle();
        _b.Restart(Truncate(old, dropRecords: 4));
        var before = _b.FakeSubmissions;
        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Quarantined), "rollback evidence reported");
        Check.True(_b.R.Ledger.QuarantineReason is not null, "quarantined");
        var c = _b.Enqueue(3);
        for (var i = 0; i < 3; i++) _b.Cycle();
        Check.Equal(before, _b.FakeSubmissions, "nothing is submitted while quarantined");
        Check.True(_b.R.Ledger.Find(c) is null, "nothing new is claimed either");

        _b.Restart();
        Check.True(_b.R.Ledger.QuarantineReason is not null, "quarantine survives restart");
        _ = beforeSubmit;
    }

    [Test] public void ClearingQuarantineFailsDoubtfulJobsInsteadOfSubmittingThem()
    {
        var id = _b.Enqueue();
        _b.Erp.FailNext("accept", MockErp.Fault.RequestLost);
        _b.Cycle();
        _b.Erp.Job(id).State = "submitted_to_spooler"; // the ERP recorded a submission this journal has no trace of
        var report = _b.Cycle();
        Check.True(report.Has(SyncReport.Quarantined, id), "reported");
        Check.Equal(JobState.Accepted, _b.Local(id), "untouched while quarantined");
        Check.Throws<LedgerQuarantinedException>(() => _b.R.Ledger.Submit(id, _b.R.Spooler), "direct submission refused too");

        Check.Sequence([id], _b.R.Ledger.ClearQuarantine(), "operator action lists the jobs it gave up");
        Check.Equal(JobState.Failed, _b.Local(id), "not submitted after recovery");
        _b.Cycle();
        Check.Equal(0, _b.FakeSubmissions, "never reached the fake spooler");
    }

    [Test] public void ReconcileRunsPeriodicallyNotOnEveryCycle()
    {
        _b.Enqueue();
        _b.Cycle();
        var calls = _b.Erp.CallCount("reconcile");
        _b.Cycle();
        _b.Cycle();
        Check.Equal(calls, _b.Erp.CallCount("reconcile"), "not every cycle");
        _b.R.Clock.Advance(TimeSpan.FromMinutes(10));
        _b.Cycle();
        Check.Equal(calls + 1, _b.Erp.CallCount("reconcile"), "after the interval");
    }

    [Test] public void JournalIsBoundToItsErpOrigin()
    {
        var journal = new MemoryJournalStore();
        JobLedger.Open(journal, new MemoryBlobStore(), Rig.Agent, "https://erp.test", _b.R.Clock, _b.R.Ids.Next, out _).Dispose();
        using var same = JobLedger.Open(new MemoryJournalStore(journal.CrashImage()), new MemoryBlobStore(), Rig.Agent, "https://erp.test", _b.R.Clock, _b.R.Ids.Next, out _);
        Check.Equal("https://erp.test", same.Origin, "recorded");
        Check.Throws<JournalMismatchException>(
            () => JobLedger.Open(new MemoryJournalStore(journal.CrashImage()), new MemoryBlobStore(), Rig.Agent, "https://other.test", _b.R.Clock, _b.R.Ids.Next, out _),
            "another origin must not reuse the journal");
    }

    /// <summary>Cuts whole records off the end of a journal image (what restoring an old copy looks like).</summary>
    private static byte[] Truncate(byte[] image, int dropRecords)
    {
        var ends = new List<int>();
        for (var o = 0; o < image.Length;)
        {
            o += Journal.JournalCodec.HeaderSize + (int)BitConverter.ToUInt32(image, o + 4) + Journal.JournalCodec.HashSize;
            ends.Add(o);
        }
        return image[..ends[^(dropRecords + 1)]];
    }
}
