using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Spooling;
using LabelPrinter.Agent.Core.Tests.Harness;

namespace LabelPrinter.Agent.Core.Tests;

public sealed class AcceptanceTests
{
    private readonly Rig _r = new();

    [Test] public void NewJobIsAcceptedAndOwedToErp()
    {
        var id = _r.AcceptNew();
        var job = _r.Ledger.Find(id)!;
        Check.Equal(JobState.Accepted, job.State, "state");
        Check.Equal(new JobKey(Rig.Agent, id), job.Key, "identity scoped to agent");
        Check.Sequence([new PendingAccept(id, 1)], _r.Ledger.PendingAccepts(), "pending accept");
        Check.Equal(0, _r.Ledger.PendingEvents().Count, "acceptance is acknowledged via accept, not an event");
    }

    [Test] public void InvalidJobLeavesNoTrace()
    {
        var p = Rig.Payload();
        var bad = _r.Ledger.Accept(Rig.Envelope(p) with { Copies = 0 }, p);
        Check.Equal(AcceptOutcome.Rejected, bad.Outcome, "invalid envelope");
        var wrongBytes = _r.Ledger.Accept(Rig.Envelope(p), Rig.Payload(seed: 2));
        Check.Equal("PAYLOAD_HASH_MISMATCH", wrongBytes.Errors.Single().Code, "payload mismatch");
        Check.Equal(0, _r.Ledger.Jobs().Count, "nothing stored");
        Check.Equal(0, _r.Blobs.Blobs.Count, "no payload stored");
        Check.Equal(1, _r.Journal.Appends, "only the journal header was written");
    }

    [Test] public void ExpiredAuthorizationIsNotAccepted()
    {
        _r.Clock.Advance(TimeSpan.FromHours(1));
        var p = Rig.Payload();
        var result = _r.Ledger.Accept(Rig.Envelope(p), p);
        Check.Equal("AUTHORIZATION_EXPIRED", result.Errors.Single().Code, "expired");
    }

    [Test] public void DuplicateWithIdenticalContentReturnsExistingState()
    {
        var id = _r.AcceptNew();
        var appends = _r.Journal.Appends;
        var p = Rig.Payload();
        var again = _r.Ledger.Accept(Rig.Envelope(p), p);
        Check.Equal(AcceptOutcome.AlreadyKnown, again.Outcome, "outcome");
        Check.Equal(JobState.Accepted, again.State, "state");
        Check.Equal(appends, _r.Journal.Appends, "no new journal record");

        _r.Ledger.Submit(id, _r.Spooler);
        var afterSubmit = _r.Ledger.Accept(Rig.Envelope(p), ReadOnlyMemory<byte>.Empty);
        Check.Equal(JobState.SubmittedToSpooler, afterSubmit.State, "reports current state, payload not needed");
        Check.Equal(1, _r.Spooler.Submissions.Count, "duplicate acceptance never prints");
    }

    [Test] public void RedeliveryWithHigherAttemptUpdatesTheOwedAcknowledgement()
    {
        var id = _r.AcceptNew();
        var p = Rig.Payload();
        var again = _r.Ledger.Accept(Rig.Envelope(p, attempt: 2) with { LeaseUntil = Rig.T0.AddMinutes(9) }, p);
        Check.Equal(AcceptOutcome.AlreadyKnown, again.Outcome, "same job");
        Check.Sequence([new PendingAccept(id, 2)], _r.Ledger.PendingAccepts(), "ack must name the ERP's current attempt");
        _r.Restart();
        Check.Sequence([new PendingAccept(id, 2)], _r.Ledger.PendingAccepts(), "survives restart");
    }

    [Test] public void SameIdentityWithDifferentContentIsAConflict()
    {
        var id = _r.AcceptNew();
        var other = Rig.Payload(seed: 7);
        var conflict = _r.Ledger.Accept(Rig.Envelope(other), other);
        Check.Equal(AcceptOutcome.Conflict, conflict.Outcome, "different payload hash");
        var copies = _r.Ledger.Accept(Rig.Envelope(Rig.Payload(), copies: 3), Rig.Payload());
        Check.Equal(AcceptOutcome.Conflict, copies.Outcome, "different copies");
        Check.Equal(ContentHash.Compute(Rig.Envelope(Rig.Payload())), _r.Ledger.Find(id)!.ContentHash, "original content kept");
        Check.Equal(1, _r.Ledger.Jobs().Count, "still one job");
    }

    [Test] public void ConcurrentDuplicatesProduceOneAcceptanceAndOneSubmission()
    {
        var p = Rig.Payload();
        var env = Rig.Envelope(p);
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<AcceptOutcome>();
        var submits = new System.Collections.Concurrent.ConcurrentBag<SubmitOutcome>();
        using var start = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, 16).Select(_ => new Thread(() =>
        {
            start.Wait();
            outcomes.Add(_r.Ledger.Accept(env, p).Outcome);
            submits.Add(_r.Ledger.Submit(env.JobId, _r.Spooler).Outcome);
        })).ToList();
        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => t.Join());

        Check.Equal(1, outcomes.Count(o => o == AcceptOutcome.AcceptedNew), "exactly one new acceptance");
        Check.Equal(15, outcomes.Count(o => o == AcceptOutcome.AlreadyKnown), "the rest see the existing job");
        Check.Equal(1, submits.Count(o => o == SubmitOutcome.SpoolerInvoked), "exactly one submission");
        Check.Equal(1, _r.Spooler.Submissions.Count, "spooler called once");
    }
}

public sealed class SubmissionTests
{
    private readonly Rig _r = new();

    [Test] public void HappyPathRecordsEachStageAndStopsAtSpoolerAcknowledgement()
    {
        var id = _r.AcceptNew();
        var result = _r.Ledger.Submit(id, _r.Spooler);
        Check.Equal(SubmitOutcome.SpoolerInvoked, result.Outcome, "outcome");
        Check.Equal(JobState.SubmittedToSpooler, result.State, "a spooler acknowledgement is not completion");
        var events = _r.Ledger.PendingEvents();
        Check.Sequence([JobState.Submitting, JobState.SubmittedToSpooler], events.Select(e => e.State), "events");
        Check.Sequence([1, 2], events.Select(e => e.Seq), "sequence numbers");
        Check.Equal("fake-1", events[1].SpoolerJobRef, "spooler ref recorded");
        Check.Equal(0, _r.Blobs.Blobs.Count, "payload removed after the single attempt");
    }

    [Test] public void SubmissionIntentIsDurableBeforeTheSpoolerRuns()
    {
        var id = _r.AcceptNew();
        JobState? seenByRestartedProcess = null;
        _r.Spooler.OnSubmit = _ =>
        {
            var probe = new Rig();
            probe.Restart(_r.Journal.CrashImage());
            seenByRestartedProcess = probe.Ledger.Find(id)!.State;
        };
        _r.Ledger.Submit(id, _r.Spooler);
        Check.Equal(JobState.Unknown, seenByRestartedProcess, "a crash at this instant must already see the intent");
    }

    [Test] public void CopyCountIsNeverMultiplied()
    {
        var id = _r.AcceptNew(job: 1, copies: 7);
        _r.Ledger.Submit(id, _r.Spooler);
        _r.Ledger.Submit(id, _r.Spooler);
        var sent = _r.Spooler.Submissions.Single();
        Check.True(sent.Payload.AsSpan().SequenceEqual(Rig.Payload(1)), "approved bytes submitted unchanged");
        Check.Equal("tspl-bitmap-v1", sent.OutputProfile, "declared profile passed through");
        Check.True(typeof(SpoolRequest).GetProperties().All(p => !p.Name.Contains("Cop", StringComparison.OrdinalIgnoreCase)),
            "the spool request has no copies field to multiply");
    }

    [Test] public void SpoolerRejectionWithNothingWrittenIsAKnownFailure()
    {
        var id = _r.AcceptNew();
        _r.Spooler.Then(new SpoolResult(SpoolOutcome.RejectedNothingWritten, ErrorCode: "PRINTER_NOT_FOUND"));
        Check.Equal(JobState.Failed, _r.Ledger.Submit(id, _r.Spooler).State, "state");
        Check.Equal("PRINTER_NOT_FOUND", _r.Ledger.PendingEvents().Last().ErrorCode, "error code reported");
    }

    [Test] public void IndeterminateSpoolerOutcomesBecomeUnknown()
    {
        var a = _r.AcceptNew(1);
        var b = _r.AcceptNew(2);
        _r.Spooler.Then(new SpoolResult(SpoolOutcome.Indeterminate, ErrorCode: "WRITE_TIMEOUT")).ThenThrow(new InvalidOperationException("driver crashed"));
        Check.Equal(JobState.Unknown, _r.Ledger.Submit(a, _r.Spooler).State, "indeterminate");
        Check.Equal(JobState.Unknown, _r.Ledger.Submit(b, _r.Spooler).State, "exception");
        Check.Equal("SPOOLER_EXCEPTION", _r.Ledger.PendingEvents().Last().ErrorCode, "no exception text leaks into events");
    }

    [Test] public void ExpiredAuthorizationFailsWithoutCallingTheSpooler()
    {
        var id = _r.AcceptNew();
        _r.Clock.Advance(TimeSpan.FromHours(1));
        var result = _r.Ledger.Submit(id, _r.Spooler);
        Check.Equal(SubmitOutcome.FailedBeforeSubmission, result.Outcome, "outcome");
        Check.Equal(JobState.Failed, result.State, "state");
        Check.Equal(0, _r.Spooler.Submissions.Count, "spooler untouched");
    }

    [Test] public void MissingOrAlteredLocalPayloadFailsWithoutCallingTheSpooler()
    {
        var a = _r.AcceptNew(1);
        var b = _r.AcceptNew(2);
        _r.Blobs.Delete(a);
        _r.Blobs.Tamper(b);
        Check.Equal(JobState.Failed, _r.Ledger.Submit(a, _r.Spooler).State, "missing");
        Check.Equal(JobState.Failed, _r.Ledger.Submit(b, _r.Spooler).State, "altered");
        Check.Sequence(["LOCAL_PAYLOAD_MISSING", "PAYLOAD_HASH_MISMATCH"], _r.Ledger.PendingEvents().Select(e => e.ErrorCode!), "codes");
        Check.Equal(0, _r.Spooler.Submissions.Count, "spooler untouched");
    }

    [Test] public void CancellationOnlyBeforeSubmissionIntent()
    {
        var a = _r.AcceptNew(1);
        var b = _r.AcceptNew(2);
        Check.True(_r.Ledger.Cancel(a), "accepted job cancels");
        Check.Equal(SubmitOutcome.NotSubmittable, _r.Ledger.Submit(a, _r.Spooler).Outcome, "cancelled job is not submitted");
        _r.Ledger.Submit(b, _r.Spooler);
        Check.True(!_r.Ledger.Cancel(b), "too late after submission");
        Check.Equal(JobState.SubmittedToSpooler, _r.Ledger.Find(b)!.State, "state unchanged");
    }

    [Test] public void CompletionNeedsEvidenceAndIsFinal()
    {
        var id = _r.AcceptNew();
        Check.Throws<InvalidTransitionException>(() => _r.Ledger.ConfirmCompleted(id, new Evidence(Evidence.DeviceReportedComplete, null)), "not yet submitted");
        _r.Ledger.Submit(id, _r.Spooler);
        Check.Throws<InvalidTransitionException>(() => _r.Ledger.ConfirmCompleted(id, new Evidence("looks_fine", null)), "unrecognised evidence");
        Check.Equal(JobState.SubmittedToSpooler, _r.Ledger.Find(id)!.State, "rejected confirmation changes nothing");
        _r.Ledger.ConfirmCompleted(id, new Evidence(Evidence.SpoolerReportedComplete, "spooler reports job 12 done"));
        var last = _r.Ledger.PendingEvents().Last();
        Check.Equal(JobState.ConfirmedCompleted, last.State, "state");
        Check.Equal(Evidence.SpoolerReportedComplete, last.Evidence!.Level, "evidence travels with the event");
        Check.Throws<InvalidTransitionException>(() => _r.Ledger.MarkUnknown(id, "X"), "terminal");
    }
}

public sealed class UnknownOutcomeTests
{
    private readonly Rig _r = new();

    private Guid MakeUnknown(int job = 1)
    {
        var id = _r.AcceptNew(job);
        _r.Spooler.Then(new SpoolResult(SpoolOutcome.Indeterminate));
        _r.Ledger.Submit(id, _r.Spooler);
        Check.Equal(JobState.Unknown, _r.Ledger.Find(id)!.State, "fixture");
        return id;
    }

    [Test] public void UnknownJobIsNeverResubmitted()
    {
        var id = MakeUnknown();
        for (var i = 0; i < 3; i++)
        {
            Check.Equal(SubmitOutcome.NotSubmittable, _r.Ledger.Submit(id, _r.Spooler).Outcome, "submit refused");
            _r.Restart();
            Check.Equal(0, _r.Report.Resumable.Count, "not offered for resumption");
        }
        Check.Equal(JobState.Unknown, _r.Ledger.Find(id)!.State, "still unknown");
        Check.Equal(1, _r.Spooler.Submissions.Count, "one attempt in total");
    }

    [Test] public void LeaseExpiryAndRedeliveryDoNotAuthorizeANewAttempt()
    {
        var id = MakeUnknown();
        _r.Clock.Advance(TimeSpan.FromMinutes(10)); // well past leaseUntil
        var p = Rig.Payload();
        var redelivered = _r.Ledger.Accept(Rig.Envelope(p, attempt: 2) with { LeaseUntil = _r.Clock.Now.AddMinutes(1) }, p);
        Check.Equal(AcceptOutcome.AlreadyKnown, redelivered.Outcome, "same job");
        Check.Equal(JobState.Unknown, redelivered.State, "reported as unknown");
        Check.Equal(SubmitOutcome.NotSubmittable, _r.Ledger.Submit(id, _r.Spooler).Outcome, "submit refused");
        Check.Equal(1, _r.Spooler.Submissions.Count, "no second print");
    }

    [Test] public void ExplicitReprintIsANewJobThatReferencesTheOriginal()
    {
        var original = MakeUnknown();
        var p = Rig.Payload();
        var reprint = Rig.Envelope(p, job: 2, reprintOf: original);
        Check.Equal(AcceptOutcome.AcceptedNew, _r.Ledger.Accept(reprint, p).Outcome, "new authorized job id");
        Check.Equal(JobState.SubmittedToSpooler, _r.Ledger.Submit(reprint.JobId, _r.Spooler).State, "reprint submitted");
        Check.Equal(JobState.Unknown, _r.Ledger.Find(original)!.State, "original untouched");
        Check.Equal(original, _r.Ledger.Find(reprint.JobId)!.Envelope.ReprintOf, "lineage kept");
        Check.Sequence([original, reprint.JobId], _r.Spooler.Submissions.Select(s => s.JobId), "each job submitted once");
    }

    [Test] public void UnknownResolvesOnlyOnPositiveEvidence()
    {
        var a = MakeUnknown(1);
        var b = MakeUnknown(2);
        Check.Throws<InvalidTransitionException>(() => _r.Ledger.RecordSpoolerObservation(a, ""), "no reference");
        _r.Ledger.RecordSpoolerObservation(a, "spool-41");
        Check.Equal(JobState.SubmittedToSpooler, _r.Ledger.Find(a)!.State, "observed in spooler");
        _r.Ledger.ConfirmCompleted(b, new Evidence(Evidence.DeviceReportedComplete, null));
        Check.Equal(JobState.ConfirmedCompleted, _r.Ledger.Find(b)!.State, "device evidence");
    }
}
