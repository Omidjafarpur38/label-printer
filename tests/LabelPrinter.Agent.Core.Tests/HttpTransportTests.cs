using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Erp;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Runtime;
using LabelPrinter.Agent.Core.Spooling;
using LabelPrinter.Agent.Core.Tests.Fakes;
using LabelPrinter.Agent.Core.Tests.Harness;

namespace LabelPrinter.Agent.Core.Tests;

/// <summary>HttpErpClient over real loopback sockets against the mock ERP HTTP server. Fake spooler.</summary>
public sealed class HttpBench : IDisposable
{
    public Rig R { get; } = new();
    public MockErpHttpServer Server { get; }
    public HttpErpClient Http { get; private set; }
    public ErpSync Sync { get; private set; }
    public List<string> Log { get; } = [];
    public AgentCredential? Credential;

    public HttpBench(TimeSpan? timeout = null)
    {
        Server = new MockErpHttpServer(new MockErp(R.Clock));
        AgentCredential.TryParse(Server.Credential, out Credential);
        Http = new HttpErpClient(Server.Origin, () => Credential, new HttpErpOptions { ClaimWaitSec = 0, RequestTimeout = timeout ?? TimeSpan.FromSeconds(10) });
        Sync = new ErpSync(R.Ledger, Http, R.Spooler, R.Clock);
    }

    public Guid Enqueue(int job = 1, int copies = 1)
    {
        var payload = Rig.Payload(job, 5000);
        Server.Erp.Enqueue(Rig.Envelope(payload, job, copies), payload);
        return Rig.JobId(job);
    }

    public void Restart(byte[]? image = null)
    {
        R.Restart(image);
        Sync = new ErpSync(R.Ledger, Http, R.Spooler, R.Clock);
    }

    public int FakeSubmissions => R.Spooler.Submissions.Count;
    public string Server_(Guid id) => Server.Erp.Job(id).State;

    public void Dispose() { Http.Dispose(); Server.Dispose(); }
}

public sealed class HttpTransportTests : IDisposable
{
    private readonly HttpBench _b = new();
    public void Dispose() => _b.Dispose();

    [Test] public void FullCycleOverHttp()
    {
        var id = _b.Enqueue(copies: 3);
        var report = _b.Sync.RunCycle();
        Check.True(report.Has(SyncReport.Accepted, id) && report.Has(SyncReport.Submitted, id), "cycle notes");
        Check.Equal("submitted_to_spooler", _b.Server_(id), "server state");
        Check.True(_b.R.Spooler.Submissions.Single().Payload.AsSpan().SequenceEqual(Rig.Payload(1, 5000)), "bytes arrive unchanged over HTTP");
        Check.Equal("fake-1", _b.Server.Erp.Job(id).AgentEvents.Count == 2 ? "fake-1" : "?", "both events delivered");

        _b.R.Ledger.ConfirmCompleted(id, new Evidence(Evidence.DeviceReportedComplete, "synthetic"));
        _b.Sync.RunCycle();
        Check.Equal("confirmed_completed", _b.Server_(id), "evidence travels as JSON");
        Check.Equal(Evidence.DeviceReportedComplete, _b.Server.Erp.Job(id).AgentEvents.Last().EvidenceLevel, "level");
        Check.Equal(1, _b.FakeSubmissions, "one fake-spooler submission");
    }

    [Test] public void CredentialOnlyTravelsInTheAuthorizationHeader()
    {
        _b.Enqueue();
        _b.Sync.RunCycle();
        Check.True(_b.Server.Requests.Count >= 4, "requests were made");
        Check.True(_b.Server.Requests.All(r => !r.Contains(AgentCredential.Scheme + "_", StringComparison.Ordinal)), "never in a URL");
        Check.True(_b.Server.AuthorizationSeen.All(a => a == "Bearer " + _b.Server.Credential), "sent as Bearer on every agent call");
    }

    [Test] public void DroppedConnectionsNeverCauseASecondSubmission()
    {
        var id = _b.Enqueue();
        foreach (var (op, fault) in new[] { ("claim", MockErp.Fault.ResponseLost), ("accept", MockErp.Fault.ResponseLost), ("events", MockErp.Fault.RequestLost), ("events", MockErp.Fault.ResponseLost) })
            _b.Server.Erp.FailNext(op, fault);
        for (var i = 0; i < 8; i++)
        {
            var report = _b.Sync.RunCycle();
            if (i % 3 == 2) _b.Restart();
            _b.R.Clock.Advance(TimeSpan.FromSeconds(61));
            _ = report;
        }
        Check.Equal("submitted_to_spooler", _b.Server_(id), "converged");
        Check.Equal(1, _b.FakeSubmissions, "one fake-spooler submission");
        Check.Equal(0, _b.R.Ledger.PendingEvents().Count + _b.R.Ledger.PendingAccepts().Count, "outbox empty");
        Check.Equal(2, _b.Server.Erp.Job(id).AgentEvents.Count, "events stored once");
    }

    [Test] public void RedirectIsNotFollowedAndCredentialIsNotForwarded()
    {
        using var other = new MockErpHttpServer(new MockErp(_b.R.Clock));
        _b.Server.RedirectTo = other.Origin;
        var report = _b.Sync.RunCycle();
        Check.True(report.ErpUnavailable, "treated as no usable answer");
        Check.Equal(0, other.Requests.Count, "the other origin was never contacted");
    }

    [Test] public void ServerErrorsAreNotRetriedImmediately()
    {
        _b.Enqueue();
        _b.Server.Force = (100, 503, null);
        var report = _b.Sync.RunCycle();
        Check.True(report.ErpUnavailable, "unavailable");
        Check.Equal(1, _b.Server.Requests.Count, "one request, then the cycle stops talking");
        Check.Equal(0, _b.FakeSubmissions, "nothing submitted");
    }

    [Test] public void RateLimitDelayFollowsRetryAfterWithinBounds()
    {
        var identity = new AgentIdentity(_b.Server.Origin, Rig.Agent, Guid.NewGuid(), "t", "0.1", Rig.T0, Rig.T0, 30, 0);
        var dir = Path.Combine(Path.GetTempPath(), "lp-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new AgentRunner(_b.R.Ledger, _b.Http, _b.Sync, new IdentityStore(dir, new Plain()), identity, _b.R.Clock, _b.Log.Add);
            _b.Server.Force = (1, 429, 7);
            Check.True(runner.RunOnce() is null, "keeps running");
            Check.Equal(TimeSpan.FromSeconds(7), runner.LastDelay, "Retry-After honoured");
            _b.Server.Force = (1, 429, 1);
            runner.RunOnce();
            Check.Equal(TimeSpan.FromSeconds(5), runner.LastDelay, "never below the floor");
            _b.Server.Force = (1, 429, 86400);
            runner.RunOnce();
            Check.Equal(TimeSpan.FromMinutes(15), runner.LastDelay, "never above the cap");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test] public void BackoffGrowsWithJitterAndResetsAfterSuccess()
    {
        var identity = new AgentIdentity(_b.Server.Origin, Rig.Agent, Guid.NewGuid(), "t", "0.1", Rig.T0, Rig.T0, 30, 0);
        var dir = Path.Combine(Path.GetTempPath(), "lp-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new AgentRunner(_b.R.Ledger, _b.Http, _b.Sync, new IdentityStore(dir, new Plain()), identity, _b.R.Clock, _b.Log.Add, random: new Random(1));
            _b.Server.Force = (6, 500, null);
            var delays = new List<TimeSpan>();
            for (var i = 0; i < 6; i++) { runner.RunOnce(); delays.Add(runner.LastDelay); }
            Check.True(delays.All(d => d >= TimeSpan.FromSeconds(0.5)), "never an immediate retry");
            for (var i = 0; i < 6; i++) Check.True(delays[i] <= TimeSpan.FromSeconds(2 * Math.Pow(2, i)), $"delay {i} within its ceiling");
            Check.True(delays.Distinct().Count() > 3, "jittered");
            runner.RunOnce();
            Check.Equal(TimeSpan.FromSeconds(1), runner.LastDelay, "idle delay after recovery");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test] public void RevokedCredentialStopsTheRunner()
    {
        var identity = new AgentIdentity(_b.Server.Origin, Rig.Agent, Guid.NewGuid(), "t", "0.1", Rig.T0, Rig.T0, 30, 0);
        var dir = Path.Combine(Path.GetTempPath(), "lp-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            _b.Server.Revoked = true;
            var runner = new AgentRunner(_b.R.Ledger, _b.Http, _b.Sync, new IdentityStore(dir, new Plain()), identity, _b.R.Clock, _b.Log.Add);
            Check.Equal(RunnerStop.CredentialRejected, runner.RunOnce(), "stops");
            Check.True(_b.Log.All(l => !l.Contains(AgentCredential.Scheme + "_", StringComparison.Ordinal)), "log has no credential");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test] public void UnusableAnswersAreNotGuessedAt()
    {
        var id = _b.Enqueue();
        _b.Server.RawBodyOnce = "<html>gateway</html>";
        Check.True(_b.Sync.RunCycle().ErpUnavailable, "non-JSON answer");
        _b.Server.RawBodyOnce = """{"jobs":[{"jobId":"not-a-uuid"}],"cancelJobIds":[],"paused":false}""";
        _b.R.Clock.Advance(TimeSpan.FromMinutes(11));
        Check.True(_b.Sync.RunCycle().ErpUnavailable, "envelope without a usable identity");
        Check.Equal(0, _b.R.Ledger.Jobs().Count, "nothing accepted or invented");
        _ = id;
    }

    [Test] public void PayloadLongerThanDeclaredIsRefusedAndReported()
    {
        var id = _b.Enqueue();
        _b.Server.ExtraPayloadBytes = 10;
        _b.Sync.RunCycle();
        Check.Equal(JobState.Failed, _b.R.Ledger.Find(id)!.State, "refused");
        Check.Equal("failed", _b.Server_(id), "reported");
        Check.Equal(0, _b.FakeSubmissions, "not submitted");
    }

    [Test] public void StalledServerTimesOutInsteadOfHanging()
    {
        using var slow = new HttpBench(timeout: TimeSpan.FromMilliseconds(400));
        slow.Enqueue();
        slow.Server.Stall = TimeSpan.FromSeconds(2);
        var started = DateTime.UtcNow;
        Check.True(slow.Sync.RunCycle().ErpUnavailable, "timeout is 'no usable answer'");
        Check.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5), "gave up after the timeout");
    }

    [Test] public void OriginRules()
    {
        Check.Equal("https://erp.example", HttpErpClient.OriginKey("HTTPS://ERP.example/"), "normalised");
        Check.Equal("http://127.0.0.1:4100", HttpErpClient.OriginKey("http://127.0.0.1:4100"), "loopback http for tests");
        foreach (var bad in new[] { "http://erp.example", "https://user:pw@erp.example", "https://erp.example/api", "https://erp.example/?x=1", "ftp://erp.example", "erp.example" })
            Check.Throws<ArgumentException>(() => HttpErpClient.OriginKey(bad), "must refuse " + bad);
    }

    [Test] public void EnvelopeParsingIgnoresUnknownFieldsAndToleratesAbsentMedia()
    {
        var id = _b.Enqueue();
        var payload = Rig.Payload(1, 5000);
        _b.Server.Erp.Job(id).Envelope = Rig.Envelope(payload) with { Output = Rig.Envelope(payload).Output with { MediaWidthMm = null, MediaHeightMm = null, GapMm = null } };
        var envelope = _b.Http.Claim(1).Jobs.Single();
        Check.True(envelope.Output.MediaWidthMm is null && envelope.Output.GapMm is null, "absent media stays absent");
        Check.Equal(Rig.T0.AddMinutes(59), envelope.AuthorizationExpiresAt, "timestamps parsed as UTC");
        Check.Equal(0, EnvelopeValidator.ValidateEnvelope(envelope, Rig.Agent).Count, "valid after the round trip");
    }

    private sealed class Plain : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plain) => plain.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => protectedBytes.ToArray();
    }
}

public sealed class IdentityStoreTests : IDisposable
{
    private sealed class Xor : ISecretProtector // stands in for DPAPI: reversible, and not the plain text
    {
        public byte[] Protect(ReadOnlySpan<byte> plain) => [.. plain.ToArray().Select(b => (byte)(b ^ 0x5A))];
        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => [.. protectedBytes.ToArray().Select(b => (byte)(b ^ 0x5A))];
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lp-id-" + Guid.NewGuid().ToString("N"));
    private static string Synthetic(char k, char s) => AgentCredential.Scheme + "_" + new string(k, 22) + "." + new string(s, 43);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Test] public void CredentialNeverReachesDiskUnprotectedAndRotationKeepsTheOldOneUntilConfirmed()
    {
        var restricted = new List<string>();
        var store = new IdentityStore(_dir, new Xor(), restricted.Add);
        var install = store.InstallId();
        var identity = new AgentIdentity("https://erp.test", Rig.Agent, install, "pc", "0.1", Rig.T0, Rig.T0, 30, 20);
        store.SavePairing(identity, Synthetic('a', 'b'));
        Check.True(restricted.Count >= 2, "access is restricted before content is written");
        foreach (var file in Directory.GetFiles(_dir))
            Check.True(!File.ReadAllText(file).Contains(new string('b', 43), StringComparison.Ordinal), "secret not readable in " + Path.GetFileName(file));
        Check.Equal(new string('a', 22), store.Current()!.KeyId, "active credential");

        store.SavePending(Synthetic('c', 'd'));
        Check.Equal(new string('c', 22), store.Current()!.KeyId, "rotated credential is presented first");
        store.DiscardPending();
        Check.Equal(new string('a', 22), store.Current()!.KeyId, "falls back to the previous one");

        store.SavePending(Synthetic('c', 'd'));
        store.PromotePending(Rig.T0.AddDays(1));
        Check.Equal(new string('c', 22), new IdentityStore(_dir, new Xor()).Current()!.KeyId, "promoted and durable");
        Check.Equal(Rig.T0.AddDays(1), store.Load()!.CredentialSetAt, "rotation time recorded");

        store.Unpair();
        Check.True(store.Current() is null && store.Load() is null, "unpaired");
        Check.Equal(install, store.InstallId(), "install id survives unpairing");
    }

    [Test] public void SettingsRefuseJobsForAnythingButTheSelectedPrinter()
    {
        var payload = Rig.Payload();
        var job = Rig.Envelope(payload);
        Check.Equal("PRINTER_NOT_CONFIGURED", new AgentSettings().Refuse(job), "no selection");
        var selected = new AgentSettings(new PrinterSelection(Guid.NewGuid(), "Q", "tspl", 203, 50, 30, Rig.Printer));
        Check.True(selected.Refuse(job) is null, "matching job");
        Check.Equal("PRINTER_NOT_SELECTED", selected.Refuse(job with { PrinterId = Guid.NewGuid() }), "another printer");
        Check.Equal("PRINTER_DPI_MISMATCH", selected.Refuse(job with { Output = job.Output with { Dpi = 300 } }), "dpi");
        Check.Equal("PRINTER_LANGUAGE_MISMATCH", selected.Refuse(job with { Output = job.Output with { Language = "zpl", Profile = "zpl-bitmap-v1" } }), "language");
        selected.Save(_dir);
        Check.Equal(selected, AgentSettings.Load(_dir), "round trip");
    }
}
