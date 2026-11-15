using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Tests.Fakes;

namespace LabelPrinter.Agent.Core.Tests;

/// <summary>Synthetic test bench: in-memory storage, manual clock, fake spooler. No network, no printer.</summary>
public sealed class Rig
{
    public static readonly Guid Agent = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    public static readonly Guid Printer = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    public static readonly Guid Profile = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public ManualClock Clock { get; } = new(T0);
    public SequentialIds Ids { get; } = new();
    public MemoryBlobStore Blobs { get; } = new();
    public FakeSpooler Spooler { get; } = new();
    public MemoryJournalStore Journal { get; private set; } = new();
    public JobLedger Ledger { get; private set; } = null!;
    public RecoveryReport Report { get; private set; } = null!;

    public Rig() => Open();

    private void Open()
    {
        Ledger = JobLedger.Open(Journal, Blobs, Agent, Clock, Ids.Next, out var report);
        Report = report;
    }

    /// <summary>Simulates process death and restart from the given on-disk image (default: durable bytes only).</summary>
    public void Restart(byte[]? image = null)
    {
        Journal = new MemoryJournalStore(image ?? Journal.CrashImage());
        Open();
    }

    public static byte[] Payload(int seed = 1, int length = 64)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static Guid JobId(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x0A);

    public static JobEnvelope Envelope(byte[] payload, int job = 1, int copies = 1, int attempt = 1, Guid? reprintOf = null) => new(
        JobId: JobId(job),
        IdempotencyKey: $"idem-{job}",
        ContractVersion: ContractInfo.V0_1,
        AgentId: Agent,
        PrinterId: Printer,
        ProfileId: Profile,
        ProfileVersion: 1,
        Kind: "test",
        Output: new JobOutput("tspl", "tspl-bitmap-v1", "application/octet-stream", payload.Length,
            EnvelopeValidator.Sha256Hex(payload), Pages: copies, Dpi: 203, MediaWidthMm: 50, MediaHeightMm: 30, GapMm: 2),
        Copies: copies,
        CreatedAt: T0.AddMinutes(-1),
        AuthorizationExpiresAt: T0.AddMinutes(59),
        AgentMustNotSubmitAfter: T0.AddMinutes(59),
        CorrelationId: "corr-" + job,
        ReprintOf: reprintOf,
        DeliveryAttempt: attempt,
        LeaseUntil: T0.AddMinutes(1));

    /// <summary>Accepts a fresh synthetic job and returns its id.</summary>
    public Guid AcceptNew(int job = 1, int copies = 1)
    {
        var payload = Payload(job);
        var result = Ledger.Accept(Envelope(payload, job, copies), payload);
        Harness.Check.Equal(AcceptOutcome.AcceptedNew, result.Outcome, "fixture acceptance");
        return JobId(job);
    }
}
