using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Journal;
using LabelPrinter.Agent.Core.Spooling;

namespace LabelPrinter.Agent.Core.Tests.Fakes;

/// <summary>
/// In-memory journal with deterministic fault injection. "Durable" bytes are those covered by a successful
/// Flush; <see cref="CrashImage"/> is what a process would find after a power cut.
/// </summary>
public sealed class MemoryJournalStore(byte[]? initial = null) : IJournalStore
{
    private readonly List<byte> _data = [.. initial ?? []];
    private int _durable = initial?.Length ?? 0;

    public int Appends { get; private set; }
    public int Flushes { get; private set; }

    /// <summary>Called before each append with its 1-based index. Return bytes to write before throwing, or null to succeed.</summary>
    public Func<int, (int PartialBytes, Exception Error)?>? AppendFault { get; set; }
    public Func<int, Exception?>? FlushFault { get; set; }
    public Exception? TruncateFault { get; set; }

    public byte[] ReadAll() => [.. _data];

    public void Append(ReadOnlySpan<byte> data)
    {
        Appends++;
        if (AppendFault?.Invoke(Appends) is { } fault)
        {
            _data.AddRange(data[..Math.Min(fault.PartialBytes, data.Length)]);
            throw fault.Error;
        }
        _data.AddRange(data);
    }

    public void Flush()
    {
        Flushes++;
        if (FlushFault?.Invoke(Flushes) is { } error) throw error;
        _durable = _data.Count;
    }

    public void Truncate(long length)
    {
        if (TruncateFault is not null) throw TruncateFault;
        _data.RemoveRange((int)length, _data.Count - (int)length);
        _durable = Math.Min(_durable, (int)length);
    }

    /// <summary>Durable bytes, optionally followed by some of the unflushed ones (a torn write).</summary>
    public byte[] CrashImage(int unflushedBytesKept = 0) =>
        [.. _data.Take(Math.Min(_data.Count, _durable + unflushedBytesKept))];

    public void Dispose() { }
}

public sealed class MemoryBlobStore : IBlobStore
{
    private readonly Dictionary<Guid, byte[]> _blobs = [];
    public Exception? PutFault { get; set; }
    public IReadOnlyDictionary<Guid, byte[]> Blobs => _blobs;

    public void Put(Guid jobId, ReadOnlySpan<byte> data)
    {
        if (PutFault is not null) throw PutFault;
        _blobs[jobId] = data.ToArray();
    }

    public byte[]? TryGet(Guid jobId) => _blobs.TryGetValue(jobId, out var b) ? b : null;
    public void Delete(Guid jobId) => _blobs.Remove(jobId);
    public IReadOnlyList<Guid> List() => [.. _blobs.Keys];
    public void Tamper(Guid jobId) => _blobs[jobId][0] ^= 0xFF;
}

/// <summary>
/// Deterministic spooler: answers come from a script (default: accept with ref "fake-N"), every call is recorded.
/// </summary>
public sealed class FakeSpooler : ISpooler
{
    public sealed record Submission(Guid JobId, Guid PrinterId, PrinterLanguage Language, string OutputProfile, byte[] Payload);

    private readonly Queue<Func<SpoolRequest, SpoolResult>> _script = new();
    private readonly List<Submission> _submissions = [];

    public IReadOnlyList<Submission> Submissions { get { lock (_submissions) return [.. _submissions]; } }
    public Action<SpoolRequest>? OnSubmit { get; set; }

    public FakeSpooler Then(SpoolResult result) { _script.Enqueue(_ => result); return this; }
    public FakeSpooler ThenThrow(Exception ex) { _script.Enqueue(_ => throw ex); return this; }

    public SpoolResult Submit(SpoolRequest request)
    {
        int n;
        lock (_submissions)
        {
            _submissions.Add(new Submission(request.JobId, request.PrinterId, request.Language, request.OutputProfile, request.Payload.ToArray()));
            n = _submissions.Count;
        }
        OnSubmit?.Invoke(request);
        return _script.Count > 0 ? _script.Dequeue()(request) : new SpoolResult(SpoolOutcome.Accepted, $"fake-{n}");
    }
}

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class SequentialIds
{
    private int _next;
    public Guid Next()
    {
        var n = Interlocked.Increment(ref _next);
        return new Guid(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xEE);
    }
}
