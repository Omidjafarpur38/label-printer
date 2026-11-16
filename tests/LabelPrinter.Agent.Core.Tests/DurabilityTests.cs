using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Journal;
using LabelPrinter.Agent.Core.Spooling;
using LabelPrinter.Agent.Core.Tests.Fakes;
using LabelPrinter.Agent.Core.Tests.Harness;

namespace LabelPrinter.Agent.Core.Tests;

public sealed class StorageFailureTests
{
    private readonly Rig _r = new();
    private static readonly IOException DiskFull = new("There is not enough space on the disk.");
    private static readonly UnauthorizedAccessException Denied = new("Access to the path is denied.");

    private void FailNextAppend(Exception error, int partialBytes = 0)
    {
        var target = _r.Journal.Appends + 1;
        _r.Journal.AppendFault = n => n == target ? (partialBytes, error) : null;
    }

    [Test] public void DiskFullBeforeDurableAcceptanceMeansNoAcceptance()
    {
        var p = Rig.Payload();
        FailNextAppend(DiskFull, partialBytes: 10);
        var ex = Check.Throws<JournalWriteException>(() => _r.Ledger.Accept(Rig.Envelope(p), p), "accept");
        Check.True(ReferenceEquals(DiskFull, ex.InnerException), "cause preserved");
        Check.True(_r.Ledger.Find(Rig.JobId(1)) is null, "job unknown to the ledger");
        Check.Equal(0, _r.Ledger.PendingAccepts().Count, "nothing may be acknowledged to the ERP");
        Check.Equal(0, _r.Blobs.Blobs.Count, "payload not kept");

        // The partial frame was removed, so the journal is usable again once space is available.
        Check.Equal(AcceptOutcome.AcceptedNew, _r.Ledger.Accept(Rig.Envelope(p), p).Outcome, "retry succeeds");
        _r.Restart();
        Check.Equal(JobState.Accepted, _r.Ledger.Find(Rig.JobId(1))!.State, "retry was durable");
        Check.Equal(0L, _r.Report.TornBytesDiscarded, "no residue of the failed write");
    }

    [Test] public void AccessDeniedOnPayloadStoreMeansNoAcceptance()
    {
        var p = Rig.Payload();
        _r.Blobs.PutFault = Denied;
        Check.Throws<UnauthorizedAccessException>(() => _r.Ledger.Accept(Rig.Envelope(p), p), "accept");
        Check.Equal(0, _r.Ledger.Jobs().Count, "no job");
        Check.Equal(1, _r.Journal.Appends, "journal untouched beyond its header");
    }

    [Test] public void AccessDeniedOnJournalMeansNoAcceptance()
    {
        var p = Rig.Payload();
        FailNextAppend(Denied);
        var ex = Check.Throws<JournalWriteException>(() => _r.Ledger.Accept(Rig.Envelope(p), p), "accept");
        Check.True(ex.InnerException is UnauthorizedAccessException, "cause preserved");
        Check.Equal(0, _r.Ledger.Jobs().Count, "no job");
    }

    [Test] public void FlushFailureFaultsTheLedgerUntilReopened()
    {
        var p = Rig.Payload();
        var target = _r.Journal.Flushes + 1;
        _r.Journal.FlushFault = n => n == target ? DiskFull : null;
        Check.Throws<JournalWriteException>(() => _r.Ledger.Accept(Rig.Envelope(p), p), "accept");
        Check.True(_r.Ledger.Find(Rig.JobId(1)) is null, "unflushed acceptance is not applied");
        Check.Throws<LedgerFaultedException>(() => _r.Ledger.Accept(Rig.Envelope(p), p), "no further writes on unknown file state");
        Check.Throws<LedgerFaultedException>(() => _r.Ledger.Submit(Rig.JobId(1), _r.Spooler), "no submissions either");

        _r.Restart();
        Check.Equal(0, _r.Ledger.Jobs().Count, "the unflushed record did not survive");
    }

    [Test] public void FailedCleanupAfterFailedAppendFaultsTheLedgerAndRestartDiscardsTheTornTail()
    {
        _r.AcceptNew(1);
        var p = Rig.Payload(2);
        FailNextAppend(DiskFull, partialBytes: 30);
        _r.Journal.TruncateFault = DiskFull;
        Check.Throws<JournalWriteException>(() => _r.Ledger.Accept(Rig.Envelope(p, job: 2), p), "accept");
        Check.Throws<LedgerFaultedException>(() => _r.Ledger.Cancel(Rig.JobId(1)), "faulted");

        _r.Restart(_r.Journal.CrashImage(unflushedBytesKept: 30));
        Check.Equal(30L, _r.Report.TornBytesDiscarded, "torn tail reported, not silent");
        Check.Sequence([Rig.JobId(1)], _r.Ledger.Jobs().Select(j => j.Key.JobId), "earlier job intact, failed one absent");
    }

    [Test] public void FailureWhilePersistingSubmissionIntentPreventsPrinting()
    {
        var id = _r.AcceptNew();
        FailNextAppend(DiskFull, partialBytes: 5);
        Check.Throws<JournalWriteException>(() => _r.Ledger.Submit(id, _r.Spooler), "submit");
        Check.Equal(0, _r.Spooler.Submissions.Count, "no printing side effect without durable intent");
        Check.Equal(JobState.Accepted, _r.Ledger.Find(id)!.State, "still accepted");
        Check.True(_r.Blobs.Blobs.ContainsKey(id), "payload kept for a later attempt");

        Check.Equal(JobState.SubmittedToSpooler, _r.Ledger.Submit(id, _r.Spooler).State, "later attempt proceeds");
        Check.Equal(1, _r.Spooler.Submissions.Count, "exactly once");
    }

    [Test] public void LostAcknowledgementAfterSuccessfulSubmissionBecomesUnknown()
    {
        var id = _r.AcceptNew();
        var failing = _r.Journal.Appends + 2; // +1 is the Submitting record, +2 records the spooler's answer
        _r.Journal.AppendFault = n => n == failing ? (0, DiskFull) : null;
        Check.Throws<JournalWriteException>(() => _r.Ledger.Submit(id, _r.Spooler), "outcome could not be recorded");
        Check.Equal(1, _r.Spooler.Submissions.Count, "the spooler did take the job");
        Check.Equal(SubmitOutcome.NotSubmittable, _r.Ledger.Submit(id, _r.Spooler).Outcome, "same process will not retry");

        _r.Restart();
        Check.Sequence([id], _r.Report.MarkedUnknown, "recovery reports it");
        Check.Equal(JobState.Unknown, _r.Ledger.Find(id)!.State, "physical outcome unknown");
        Check.Equal(SubmitOutcome.NotSubmittable, _r.Ledger.Submit(id, _r.Spooler).Outcome, "restarted process will not retry");
        Check.Equal(1, _r.Spooler.Submissions.Count, "still exactly one submission");
        Check.Sequence([JobState.Submitting, JobState.Unknown], _r.Ledger.PendingEvents().Select(e => e.State), "ERP will be told");
    }
}

public sealed class RecoveryTests
{
    private readonly Rig _r = new();

    [Test] public void CrashAfterSubmissionIntentBecomesUnknown()
    {
        var id = _r.AcceptNew();
        byte[]? atCrash = null;
        _r.Spooler.OnSubmit = _ => atCrash = _r.Journal.CrashImage();
        _r.Ledger.Submit(id, _r.Spooler);

        _r.Restart(atCrash); // the process died inside the spooler call
        Check.Sequence([id], _r.Report.MarkedUnknown, "interrupted submission");
        Check.Equal(JobState.Unknown, _r.Ledger.Find(id)!.State, "state");
        Check.Equal("INTERRUPTED_SUBMISSION", _r.Ledger.PendingEvents().Last().ErrorCode, "reason");
        Check.Equal(SubmitOutcome.NotSubmittable, _r.Ledger.Submit(id, _r.Spooler).Outcome, "no automatic resubmission");
        Check.Equal(1, _r.Spooler.Submissions.Count, "one attempt");

        _r.Restart();
        Check.Equal(0, _r.Report.MarkedUnknown.Count, "recovery is idempotent");
        Check.Equal(2, _r.Ledger.Find(id)!.LastSeq, "no duplicate recovery event");
    }

    [Test] public void AcceptedNeverSubmittedJobResumesAfterRestart()
    {
        var id = _r.AcceptNew();
        _r.Restart();
        _r.Restart();
        Check.Sequence([id], _r.Report.Resumable, "offered for continuation");
        Check.True(_r.Blobs.Blobs.ContainsKey(id), "payload kept");
        Check.Equal(JobState.SubmittedToSpooler, _r.Ledger.Submit(id, _r.Spooler).State, "continues");
        Check.Equal(1, _r.Spooler.Submissions.Count, "submitted to fake spooler once");
    }

    [Test] public void RestartPreservesEveryState()
    {
        var ids = Enumerable.Range(1, 6).Select(n => _r.AcceptNew(n)).ToList();
        _r.Spooler
            .Then(new SpoolResult(SpoolOutcome.Accepted, "s-2"))
            .Then(new SpoolResult(SpoolOutcome.Accepted, "s-3"))
            .Then(new SpoolResult(SpoolOutcome.RejectedNothingWritten, ErrorCode: "QUEUE_PAUSED"))
            .Then(new SpoolResult(SpoolOutcome.Indeterminate));
        _r.Ledger.Submit(ids[1], _r.Spooler);
        _r.Ledger.Submit(ids[2], _r.Spooler);
        _r.Ledger.ConfirmCompleted(ids[2], new Evidence(Evidence.DeviceReportedComplete, null));
        _r.Ledger.Submit(ids[3], _r.Spooler);
        _r.Ledger.Cancel(ids[4]);
        _r.Ledger.Submit(ids[5], _r.Spooler);
        var before = _r.Ledger.Jobs();

        _r.Restart();
        Check.Sequence(
            [JobState.Accepted, JobState.SubmittedToSpooler, JobState.ConfirmedCompleted, JobState.Failed, JobState.Cancelled, JobState.Unknown],
            _r.Ledger.Jobs().Select(j => j.State), "states");
        Check.Sequence(before, _r.Ledger.Jobs(), "identity, hash, attempts and sequence numbers");
        Check.Sequence([ids[0]], _r.Report.Resumable, "only the never-submitted job resumes");
        Check.Equal(0, _r.Report.MarkedUnknown.Count, "nothing was mid-submission");
        Check.Sequence([ids[0]], _r.Blobs.Blobs.Keys, "payload kept only where still needed");

        foreach (var id in ids) _r.Ledger.Submit(id, _r.Spooler);
        Check.Equal(5, _r.Spooler.Submissions.Count, "4 before restart + the resumed job; nothing submitted twice");
    }

    [Test] public void PayloadWithoutAcceptanceRecordIsRemoved()
    {
        _r.AcceptNew(1);
        _r.Blobs.Put(Rig.JobId(77), Rig.Payload(77)); // crash between payload write and journal commit
        _r.Restart();
        Check.Equal(1, _r.Report.BlobsRemoved, "orphan removed");
        Check.True(_r.Ledger.Find(Rig.JobId(77)) is null, "orphan is not a job");
    }

    [Test] public void PendingAcknowledgementsAndEventsSurviveRestart()
    {
        var a = _r.AcceptNew(1);
        var b = _r.AcceptNew(2);
        _r.Ledger.Submit(a, _r.Spooler);
        _r.Ledger.ConfirmCompleted(a, new Evidence(Evidence.SpoolerReportedComplete, "done"));
        var accepts = _r.Ledger.PendingAccepts();
        var events = _r.Ledger.PendingEvents();
        Check.Equal(3, events.Count, "three events owed");

        _r.Restart();
        Check.Sequence(accepts, _r.Ledger.PendingAccepts(), "accept acks");
        Check.Sequence(events.Select(e => e with { Evidence = null }), _r.Ledger.PendingEvents().Select(e => e with { Evidence = null }), "event ids, seq, state, time");
        Check.Equal(events[2].Evidence, _r.Ledger.PendingEvents()[2].Evidence, "evidence");

        _r.Ledger.AcknowledgeAccept(a);
        _r.Ledger.AcknowledgeEvents(a, 2);
        _r.Ledger.AcknowledgeEvents(a, 2); // idempotent
        _r.Restart();
        Check.Sequence([new PendingAccept(b, 1)], _r.Ledger.PendingAccepts(), "acknowledged accept stays acknowledged");
        Check.Sequence([3], _r.Ledger.PendingEvents().Select(e => e.Seq), "only the unacknowledged event remains");
        Check.Throws<ArgumentOutOfRangeException>(() => _r.Ledger.AcknowledgeEvents(a, 9), "cannot acknowledge events that do not exist");
    }

    [Test] public void JournalOfAnotherAgentIsRefused()
    {
        _r.AcceptNew();
        var other = Guid.Parse("dddddddd-0000-4000-8000-000000000009");
        Check.Throws<JournalMismatchException>(
            () => JobLedger.Open(new MemoryJournalStore(_r.Journal.CrashImage()), new MemoryBlobStore(), other, _r.Clock, _r.Ids.Next, out _), "open");
    }
}

public sealed class JournalIntegrityTests
{
    private readonly Rig _r = new();

    private byte[] BuildJournal()
    {
        var a = _r.AcceptNew(1);
        _r.AcceptNew(2);
        _r.Ledger.Submit(a, _r.Spooler);
        return _r.Journal.CrashImage();
    }

    [Test] public void IncompleteFinalRecordIsDiscardedAtEveryCutPoint()
    {
        var id = _r.AcceptNew(1);
        var before = _r.Journal.CrashImage();
        _r.AcceptNew(2);
        var full = _r.Journal.CrashImage();

        for (var length = before.Length + 1; length < full.Length; length++)
        {
            _r.Restart(full[..length]);
            Check.Equal((long)(length - before.Length), _r.Report.TornBytesDiscarded, $"cut at {length}: reported");
            Check.Sequence([id], _r.Ledger.Jobs().Select(j => j.Key.JobId), $"cut at {length}: earlier record intact, partial one absent");
            Check.True(_r.Journal.ReadAll().AsSpan().SequenceEqual(before), $"cut at {length}: file truncated to the last good record");
        }
    }

    [Test] public void RecoveredJournalAcceptsNewRecords()
    {
        _r.AcceptNew(1);
        var full = _r.Journal.CrashImage();
        _r.Restart([.. full, .. "LPJ1"u8, 9, 0]);
        Check.Equal(6L, _r.Report.TornBytesDiscarded, "torn header");
        _r.AcceptNew(2);
        _r.Restart();
        Check.Equal(2, _r.Ledger.Jobs().Count, "append after recovery is readable");
    }

    [Test] public void ZeroFilledTailIsTreatedAsNeverWritten()
    {
        _r.AcceptNew(1);
        _r.Restart([.. _r.Journal.CrashImage(), .. new byte[100]]);
        Check.Equal(100L, _r.Report.TornBytesDiscarded, "reported");
        Check.Equal(1, _r.Ledger.Jobs().Count, "data intact");
    }

    [Test] public void AnySingleByteCorruptionInsideTheJournalIsDetected()
    {
        var image = BuildJournal();
        for (var i = 0; i < image.Length; i++)
        {
            var damaged = (byte[])image.Clone();
            damaged[i] ^= 0x01;
            Check.Throws<JournalCorruptException>(() => JournalCodec.Decode(damaged), $"bit flip at byte {i}");
        }
    }

    [Test] public void CorruptJournalIsRefusedNotSkipped()
    {
        var image = BuildJournal();
        image[image.Length / 2] ^= 0x40;
        var ex = Check.Throws<JournalCorruptException>(
            () => JobLedger.Open(new MemoryJournalStore(image), new MemoryBlobStore(), Rig.Agent, _r.Clock, _r.Ids.Next, out _), "open");
        Check.True(ex.Offset > 0, "reports where");
    }

    [Test] public void RemovedOrReorderedRecordsAreDetected()
    {
        var image = BuildJournal();
        var frames = SplitFrames(image);
        Check.True(frames.Count >= 5, "fixture has several records");
        byte[] Join(IEnumerable<byte[]> parts) => [.. parts.SelectMany(p => p)];

        Check.Throws<JournalCorruptException>(() => JournalCodec.Decode(Join(frames.Where((_, i) => i != 2))), "middle record removed");
        Check.Throws<JournalCorruptException>(() => JournalCodec.Decode(Join([frames[0], frames[2], frames[1], .. frames.Skip(3)])), "records swapped");
        Check.Throws<JournalCorruptException>(() => JournalCodec.Decode(Join(frames.Skip(1))), "first record removed");
    }

    [Test] public void WellFormedButImpossibleHistoryIsRefused()
    {
        var p = Rig.Payload();
        var env = Rig.Envelope(p);
        JournalEntry[] history =
        [
            new() { N = 0, T = JournalEntry.Header, AgentId = Rig.Agent },
            new() { N = 1, T = JournalEntry.Accepted, Job = env, ContentHash = ContentHash.Compute(env) },
            // Claims completion straight from Accepted, with no submission and no evidence.
            new() { N = 2, T = JournalEntry.Transition, JobId = env.JobId, From = JobState.Accepted, To = JobState.ConfirmedCompleted, Seq = 1, EventId = Guid.NewGuid() },
        ];
        Check.Throws<JournalCorruptException>(
            () => JobLedger.Open(new MemoryJournalStore(Encode(history)), new MemoryBlobStore(), Rig.Agent, _r.Clock, _r.Ids.Next, out _), "open");
    }

    [Test] public void UnsupportedFormatVersionIsRefused()
    {
        var image = Encode([new JournalEntry { V = 2, N = 0, T = JournalEntry.Header, AgentId = Rig.Agent }]);
        Check.Throws<JournalCorruptException>(() => JournalCodec.Decode(image), "decode");
    }

    private static byte[] Encode(IEnumerable<JournalEntry> entries)
    {
        var prev = new byte[JournalCodec.HashSize];
        var bytes = new List<byte>();
        foreach (var e in entries)
        {
            bytes.AddRange(JournalCodec.Encode(e, prev, out var hash));
            prev = hash;
        }
        return [.. bytes];
    }

    private static List<byte[]> SplitFrames(byte[] image)
    {
        var frames = new List<byte[]>();
        for (var o = 0; o < image.Length;)
        {
            var total = JournalCodec.HeaderSize + (int)BitConverter.ToUInt32(image, o + 4) + JournalCodec.HashSize;
            frames.Add(image[o..(o + total)]);
            o += total;
        }
        return frames;
    }
}

/// <summary>The same behaviour on the real file-backed stores, in a throw-away temp directory.</summary>
public sealed class FileBackedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lp-agent-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(Rig.T0);
    private readonly SequentialIds _ids = new();
    private string JournalPath => Path.Combine(_dir, "journal.lpj");

    private JobLedger Open(out RecoveryReport report) =>
        JobLedger.Open(new FileJournalStore(JournalPath), new FileBlobStore(Path.Combine(_dir, "payloads")), Rig.Agent, _clock, _ids.Next, out report);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Test] public void StateSurvivesReopenAndPayloadIsRemovedAfterSubmission()
    {
        var spooler = new FakeSpooler();
        var p1 = Rig.Payload(1);
        var p2 = Rig.Payload(2);
        using (var ledger = Open(out _))
        {
            ledger.Accept(Rig.Envelope(p1, 1), p1);
            ledger.Accept(Rig.Envelope(p2, 2), p2);
            ledger.Submit(Rig.JobId(1), spooler);
        }
        Check.Sequence([Rig.JobId(2).ToString("N") + ".bin"], Directory.GetFiles(Path.Combine(_dir, "payloads")).Select(Path.GetFileName)!, "payload files");

        using (var ledger = Open(out var report))
        {
            Check.Equal(JobState.SubmittedToSpooler, ledger.Find(Rig.JobId(1))!.State, "job 1");
            Check.Sequence([Rig.JobId(2)], report.Resumable, "job 2 resumable");
            Check.Equal(JobState.SubmittedToSpooler, ledger.Submit(Rig.JobId(2), spooler).State, "job 2 continues from the stored payload");
        }
        Check.True(spooler.Submissions[1].Payload.AsSpan().SequenceEqual(p2), "bytes read back intact");
        Check.Equal(2, spooler.Submissions.Count, "each once");
    }

    [Test] public void SecondWriterIsRefused()
    {
        using var first = Open(out _);
        Check.Throws<JournalLockedException>(() => Open(out _), "second open while the first is alive");
    }

    [Test] public void TornTailOnDiskIsTruncatedInPlace()
    {
        var p = Rig.Payload();
        using (var ledger = Open(out _)) ledger.Accept(Rig.Envelope(p), p);
        var good = new FileInfo(JournalPath).Length;
        using (var fs = new FileStream(JournalPath, FileMode.Append)) fs.Write("LPJ1"u8);

        using (var ledger = Open(out var report))
        {
            Check.Equal(4L, report.TornBytesDiscarded, "reported");
            Check.Equal(JobState.Accepted, ledger.Find(Rig.JobId(1))!.State, "data intact");
        }
        Check.Equal(good, new FileInfo(JournalPath).Length, "file truncated");
    }

    [Test] public void DamagedFileIsRefused()
    {
        var p = Rig.Payload();
        using (var ledger = Open(out _)) ledger.Accept(Rig.Envelope(p), p);
        var bytes = File.ReadAllBytes(JournalPath);
        bytes[^40] ^= 0xFF;
        File.WriteAllBytes(JournalPath, bytes);
        Check.Throws<JournalCorruptException>(() => Open(out _), "open");
        using (new FileStream(JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } // refused journal is not left locked
    }
}
