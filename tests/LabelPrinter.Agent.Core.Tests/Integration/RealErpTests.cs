using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Erp;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Runtime;
using LabelPrinter.Agent.Core.Spooling;
using LabelPrinter.Agent.Core.Tests.Fakes;
using LabelPrinter.Agent.Core.Tests.Harness;

namespace LabelPrinter.Agent.Core.Tests.Integration;

/// <summary>
/// The agent's real HTTP transport against a real, isolated ERP backend (Fastify + PostgreSQL + Redis) started
/// from the ERP repository with synthetic data. See docs/integration-testing.md. These tests are skipped unless
/// LP_TEST_ERP_ORIGIN points at a loopback ERP; they refuse any non-loopback origin. The spooler is the fake.
/// </summary>
internal static class TestErp
{
    public static string Origin
    {
        get
        {
            var origin = Environment.GetEnvironmentVariable("LP_TEST_ERP_ORIGIN");
            if (string.IsNullOrEmpty(origin)) throw new SkipException("LP_TEST_ERP_ORIGIN not set: no isolated test ERP");
            if (!new Uri(origin).IsLoopback) throw new InvalidOperationException("Integration tests only run against a loopback ERP.");
            return origin;
        }
    }

    private static readonly Lazy<HttpClient> User = new(() =>
    {
        var http = new HttpClient { BaseAddress = new Uri(Origin), Timeout = TimeSpan.FromSeconds(30) };
        var login = Send(http, HttpMethod.Post, "/api/auth/login", new JsonObject
        {
            ["identifier"] = Environment.GetEnvironmentVariable("LP_TEST_ERP_USER") ?? "09000000000",
            ["password"] = Environment.GetEnvironmentVariable("LP_TEST_ERP_PASSWORD") ?? throw new SkipException("LP_TEST_ERP_PASSWORD not set"),
        });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login["token"]!.GetValue<string>());
        return http;
    });

    /// <summary>ERP user (browser) side, used only to set up and observe.</summary>
    public static JsonNode UserCall(HttpMethod method, string path, JsonObject? body = null) => Send(User.Value, method, "/api/label-print" + path, body);

    private static JsonNode Send(HttpClient http, HttpMethod method, string path, JsonObject? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        // The ERP rejects identical user POSTs within 2 s unless each carries its own request id.
        if (method == HttpMethod.Post) request.Headers.Add("X-Client-Request-Id", Guid.NewGuid().ToString("D"));
        using var response = http.Send(request);
        var text = new StreamReader(response.Content.ReadAsStream()).ReadToEnd();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"test ERP user call {method} {path} → {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    public static string JobState(Guid id) => UserCall(HttpMethod.Get, $"/jobs/{id:D}")["state"]!.GetValue<string>();
}

/// <summary>A paired synthetic agent with one declared printer, a verified-nothing profile, and in-memory stores.</summary>
internal sealed class PairedAgent : IDisposable
{
    private sealed class PlainProtector : ISecretProtector // test only; the Windows host uses DPAPI
    {
        public byte[] Protect(ReadOnlySpan<byte> plain) => plain.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => protectedBytes.ToArray();
    }

    public readonly string Dir = Path.Combine(Path.GetTempPath(), "lp-agent-it-" + Guid.NewGuid().ToString("N"));
    public readonly IdentityStore Identities;
    public readonly AgentIdentity Identity;
    public readonly HttpErpClient Http;
    public FakeSpooler Spooler = new();
    public MemoryBlobStore Blobs = new();
    public readonly AgentSettings Settings;
    public readonly Guid ProfileId;
    public MemoryJournalStore Journal = new();
    public JobLedger Ledger = null!;
    public ErpSync Sync = null!;
    public AgentRunner Runner = null!;
    public readonly List<string> Log = [];
    public readonly LoseResponseOnce Tap = new();

    /// <summary>/pair is limited to 5 per minute per IP, so most tests share one pairing and start from an
    /// empty journal; only tests that destroy the pairing (revocation, rotation, rate limiting) make their own.</summary>
    private static readonly Lazy<PairedAgent> SharedAgent = new(() => new PairedAgent());
    public static PairedAgent Shared()
    {
        var a = SharedAgent.Value;
        a.Journal = new MemoryJournalStore();
        a.Blobs = new MemoryBlobStore();
        a.Spooler = new FakeSpooler();
        a.Tap.Suffix = null;
        a.Tap.Lost = 0;
        a.Log.Clear();
        a.Open();
        return a;
    }

    public PairedAgent()
    {
        DelegatingHandler tap = Tap;
        Identities = new IdentityStore(Dir, new PlainProtector());
        HttpMessageHandler handler = HttpErpClient.CreateHandler();
        tap.InnerHandler = handler;
        handler = tap;
        Http = new HttpErpClient(TestErp.Origin, Identities.Current, new HttpErpOptions { ClaimWaitSec = 0, AgentVersion = "0.0.0-test" }, handler);

        var code = TestErp.UserCall(HttpMethod.Post, "/pairing-codes", new JsonObject())["code"]!.GetValue<string>();
        var pair = Http.Pair(code, Identities.InstallId(), "integration-test agent", "test", null);
        var now = DateTimeOffset.UtcNow;
        Identity = new AgentIdentity(HttpErpClient.OriginKey(TestErp.Origin), pair.AgentId, Identities.InstallId(), "integration-test agent", pair.NegotiatedContract, now, now, pair.HeartbeatSec, pair.ClaimWaitSec);
        Identities.SavePairing(Identity, pair.Credential);

        var printer = new PrinterSelection(Guid.NewGuid(), "Synthetic Queue", "tspl", 203, 50, 30);
        var mapped = Http.ReportPrinters([new PrinterReport(printer.LocalPrinterId, "Synthetic printer", printer.Queue, printer.Language, printer.Dpi, printer.WidthMm, printer.HeightMm)]);
        Settings = new AgentSettings(printer with { ErpPrinterId = mapped[printer.LocalPrinterId] });
        ProfileId = Guid.Parse(TestErp.UserCall(HttpMethod.Post, "/profiles", new JsonObject
        {
            ["printerId"] = Settings.Printer!.ErpPrinterId!.Value.ToString("D"), ["name"] = "integration", ["outputLanguage"] = "tspl", ["dpi"] = 203,
            ["media"] = new JsonObject { ["widthMm"] = 50, ["heightMm"] = 30, ["gapMm"] = 3, ["sensor"] = "gap", ["mode"] = "single" },
        })["profileId"]!.GetValue<string>());
        Open();
    }

    public void Open(byte[]? image = null)
    {
        Journal = new MemoryJournalStore(image ?? Journal.CrashImage());
        Ledger = JobLedger.Open(Journal, Blobs, Identity.AgentId, Identity.Origin, TimeProvider.System, Guid.NewGuid, out _);
        Sync = new ErpSync(Ledger, Http, Spooler, TimeProvider.System) { Admit = Settings.Refuse };
        Runner = new AgentRunner(Ledger, Http, Sync, Identities, Identity, TimeProvider.System, Log.Add);
    }

    public Guid TestPrint(int copies = 1)
    {
        var job = TestErp.UserCall(HttpMethod.Post, "/test-print", new JsonObject { ["idempotencyKey"] = "it-" + Guid.NewGuid().ToString("N"), ["profileId"] = ProfileId.ToString("D"), ["copies"] = copies });
        return Guid.Parse(job["job"]!["id"]!.GetValue<string>());
    }

    public void Dispose()
    {
        Http.Dispose();
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }
}

/// <summary>Lets a request reach the ERP and then loses the answer, once, for a path suffix.</summary>
internal sealed class LoseResponseOnce : DelegatingHandler
{
    public string? Suffix;
    public int Lost;
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
    {
        var response = base.Send(request, ct);
        if (Lost == 0 && Suffix is not null && request.RequestUri!.AbsolutePath.EndsWith(Suffix, StringComparison.Ordinal))
        {
            Lost++;
            response.Dispose();
            throw new HttpRequestException("simulated: connection dropped after the request was sent");
        }
        return response;
    }
}

public sealed class RealErpTests
{
    [Test] public void PairClaimAcceptSubmitReportAndConfirmAgainstRealErp()
    {
        var a = PairedAgent.Shared();
        Check.Equal(ContractInfo.V0_1, a.Identity.NegotiatedContract, "negotiated contract");
        var id = a.TestPrint(copies: 2);
        Check.Equal("queued", TestErp.JobState(id), "created");

        Check.True(a.Runner.RunOnce() is null, "runner continues");
        Check.Equal("submitted_to_spooler", TestErp.JobState(id), "ERP state after one iteration");
        var sent = a.Spooler.Submissions.Single();
        var job = a.Ledger.Find(id)!;
        Check.Equal(job.Envelope.Output.Sha256, EnvelopeValidator.Sha256Hex(sent.Payload), "bytes handed to the fake spooler are the ERP's bytes");
        Check.Equal(2, job.Envelope.Copies, "copies are informational; one submission");
        Check.True(Encoding.ASCII.GetString(sent.Payload, 0, 4) == "SIZE", "payload is the ERP's real TSPL output");

        a.Ledger.ConfirmCompleted(id, new Evidence(Evidence.SpoolerReportedComplete, "synthetic"));
        a.Sync.RunCycle();
        var view = TestErp.UserCall(HttpMethod.Get, $"/jobs/{id:D}");
        Check.Equal("confirmed_completed", view["state"]!.GetValue<string>(), "completion with evidence");
        Check.Equal("spooler", view["physicalConfirmation"]!.GetValue<string>(), "ERP labels it as spooler-level, not physical");
        Check.Equal(0, a.Ledger.PendingEvents().Count + a.Ledger.PendingAccepts().Count, "outbox empty");
        Check.Equal(1, a.Spooler.Submissions.Count, "one fake-spooler submission");
    }

    [Test] public void LostEventAcknowledgementIsResentAndDeduplicatedByRealErp()
    {
        var a = PairedAgent.Shared();
        a.Tap.Suffix = "/events";
        var id = a.TestPrint();
        a.Sync.RunCycle();
        Check.Equal(1, a.Tap.Lost, "an acknowledgement was lost");
        Check.Equal("submitted_to_spooler", TestErp.JobState(id), "ERP applied the events anyway");
        Check.Equal(2, a.Ledger.PendingEvents().Count, "agent still owes them");

        a.Open(); // restart in between: same event ids from the journal
        a.Sync.RunCycle();
        Check.Equal(0, a.Ledger.PendingEvents().Count, "acknowledged after resend/reconcile");
        var events = TestErp.UserCall(HttpMethod.Get, $"/jobs/{id:D}")["events"]!.AsArray().Where(e => e!["source"]!.GetValue<string>() == "agent" && e["seq"] is not null).ToList();
        Check.Equal(2, events.Count, "ERP stored each agent event once");
        Check.Equal(1, a.Spooler.Submissions.Count, "no second fake-spooler submission");
    }

    [Test] public void InterruptedSubmissionIsUnknownOnRealErpAndNeverResubmitted()
    {
        var a = PairedAgent.Shared();
        var id = a.TestPrint();
        byte[]? atCrash = null;
        a.Spooler.OnSubmit = _ => { atCrash = a.Journal.CrashImage(); throw new IOException("process dies here"); };
        a.Tap.Suffix = "/events"; // whatever the dying process sent is not acknowledged to it
        a.Sync.RunCycle();

        a.Spooler.OnSubmit = null;
        a.Open(atCrash); // restart from the journal as it was inside the spooler call
        for (var i = 0; i < 3; i++) a.Sync.RunCycle();
        Check.Equal(JobState.Unknown, a.Ledger.Find(id)!.State, "agent");
        Check.Equal("unknown", TestErp.JobState(id), "real ERP");
        Check.Equal(1, a.Spooler.Submissions.Count, "one fake-spooler submission in total");
    }

    [Test] public void RealErpErrorBodiesMapToCodes()
    {
        var a = PairedAgent.Shared();
        var notFound = Check.Throws<ErpException>(() => a.Http.Accept(Guid.NewGuid(), 1), "unknown job");
        Check.Equal(404, notFound.Status, "status");
        Check.Equal("JOB_NOT_FOUND", notFound.ErrorCode, "code from the flat error body");

        var id = a.TestPrint();
        a.Http.Claim(1);
        var lease = Check.Throws<ErpException>(() => a.Http.Accept(id, 9), "wrong delivery attempt");
        Check.Equal("LEASE_LOST", lease.ErrorCode, "code");
        Check.Equal(409, lease.Status, "status");

        var invalid = Check.Throws<ErpException>(() => a.Http.PostEvents(id, [new StatusEvent(Guid.NewGuid(), id, 1, 1, JobState.ConfirmedCompleted, DateTimeOffset.UtcNow, null, null, null, null)]), "completion without evidence");
        Check.Equal(422, invalid.Status, "status");
        Check.Equal("EVIDENCE_REQUIRED", invalid.ErrorCode, "code");
    }

    [Test] public void NotFoundFromRealErpBlocksTheOutboxAndKeepsEvents()
    {
        var a = PairedAgent.Shared();
        // A job the ERP has never heard of, as after pointing the agent at another database.
        var payload = Rig.Payload(5);
        var now = DateTimeOffset.UtcNow;
        var ghost = Rig.Envelope(payload, job: 5) with
        {
            JobId = Guid.NewGuid(), AgentId = a.Identity.AgentId, PrinterId = a.Settings.Printer!.ErpPrinterId!.Value,
            CreatedAt = now.AddMinutes(-1), AuthorizationExpiresAt = now.AddMinutes(30), AgentMustNotSubmitAfter = now.AddMinutes(30),
        };
        Check.Equal(AcceptOutcome.AcceptedNew, a.Ledger.Accept(ghost, payload).Outcome, "fixture");
        a.Ledger.Cancel(ghost.JobId);
        var report = a.Sync.RunCycle(claim: false);
        Check.True(report.Has(SyncReport.Blocked, ghost.JobId), "blocked, reported");
        Check.Equal("JOB_NOT_FOUND", a.Ledger.Find(ghost.JobId)!.Block!.Code, "durable diagnostic");
        Check.Equal(1, a.Ledger.PendingEvents().Count, "event kept");
        Check.Equal(1, a.Ledger.PendingAccepts().Count, "404 acknowledged nothing");
    }

    [Test] public void CredentialRotationCommitsOnFirstUseAndRevokesTheOldOne()
    {
        using var a = new PairedAgent();
        var old = a.Identities.Current()!;
        a.Identities.SavePending(a.Http.RotateCredential());
        Check.True(a.Identities.HasPending, "pending");
        Check.True(a.Runner.RunOnce() is null, "works with the rotated credential");
        Check.True(!a.Identities.HasPending, "promoted after a successful authenticated call");
        Check.True(a.Identities.Current()!.KeyId != old.KeyId, "new key id in use");

        using var stale = new HttpErpClient(TestErp.Origin, () => old, new HttpErpOptions { ClaimWaitSec = 0 });
        var refused = Check.Throws<ErpException>(() => stale.Heartbeat(), "old credential");
        Check.Equal(401, refused.Status, "status");
        Check.Equal("AGENT_REVOKED", refused.ErrorCode, "code");
    }

    [Test] public void RevokedAgentStopsAndKeepsItsJournal()
    {
        using var a = new PairedAgent();
        var id = a.TestPrint();
        a.Runner.RunOnce();
        TestErp.UserCall(HttpMethod.Delete, $"/agents/{a.Identity.AgentId:D}");

        Check.Equal(RunnerStop.CredentialRejected, new AgentRunner(a.Ledger, a.Http, a.Sync, a.Identities, a.Identity, TimeProvider.System, a.Log.Add).RunOnce(), "runner stops");
        Check.True(a.Ledger.Find(id) is not null, "journal kept");
        var refused = Check.Throws<ErpException>(() => a.Http.Claim(1), "claim after revocation");
        Check.Equal("AGENT_REVOKED", refused.ErrorCode, "ERP names the reason");
        Check.Equal(1, a.Spooler.Submissions.Count, "nothing new submitted");
        Check.True(!a.Log.Any(l => l.Contains(AgentCredential.Scheme + "_", StringComparison.Ordinal)), "log never contains a credential");
    }

    [Test] public void CancelRequestReachesTheAgentThroughHeartbeat()
    {
        var a = PairedAgent.Shared();
        var id = a.TestPrint();
        var envelope = a.Http.Claim(1).Jobs.Single();
        var payload = a.Http.GetPayload(id);
        Check.Equal(envelope.Output.Sha256, payload.Sha256Header, "payload header matches envelope");
        a.Ledger.Accept(envelope, payload.Bytes);
        Check.Equal("accepted_by_agent", a.Http.Accept(id, 1).State, "accepted");
        a.Ledger.AcknowledgeAccept(id);
        Check.Equal("cancel_requested", TestErp.UserCall(HttpMethod.Post, $"/jobs/{id:D}/cancel")["outcome"]!.GetValue<string>(), "user cancels");

        a.Runner.RunOnce();
        Check.Equal(JobState.Cancelled, a.Ledger.Find(id)!.State, "agent");
        Check.Equal("cancelled", TestErp.JobState(id), "real ERP");
        Check.Equal(0, a.Spooler.Submissions.Count, "never submitted");
    }

    [Test] public void JobForAnUnselectedPrinterIsRefusedAndReported()
    {
        var a = PairedAgent.Shared();
        var id = a.TestPrint();
        a.Sync = new ErpSync(a.Ledger, a.Http, a.Spooler, TimeProvider.System) { Admit = (a.Settings with { Printer = a.Settings.Printer! with { Dpi = 300 } }).Refuse };
        a.Sync.RunCycle();
        Check.Equal("failed", TestErp.JobState(id), "real ERP is told");
        Check.Equal(0, a.Spooler.Submissions.Count, "not submitted");
        Check.Equal(0, a.Blobs.Blobs.Count, "payload was not even downloaded");
    }

    [Test] public void RateLimitFromRealErpCarriesRetryAfter()
    {
        using var a = new PairedAgent();
        ErpException? limited = null;
        for (var i = 0; i < 8 && limited is null; i++)
        {
            try { a.Http.Heartbeat(); }
            catch (ErpException ex) { limited = ex; }
        }
        Check.True(limited is not null, "heartbeat is limited to 6 per minute per credential");
        Check.Equal(429, limited!.Status, "status");
        // Observed, not assumed: the ERP's 429 goes through its generic fallback, so the code is not specific.
        Check.Equal("SYSTEM_INTERNAL_ERROR", limited.ErrorCode, "error_code of a rate-limit answer");
        Check.True(a.Http.LastThrottle is not null, "throttle recorded");
        Console.WriteLine("      observed Retry-After on 429: " + (a.Http.LastThrottle!.RetryAfter?.ToString() ?? "(header absent)"));
        var runner = new AgentRunner(a.Ledger, a.Http, a.Sync, a.Identities, a.Identity, TimeProvider.System, a.Log.Add);
        Check.True(runner.RunOnce() is null, "runner keeps running");
        Check.True(runner.LastDelay >= TimeSpan.FromSeconds(5), "and waits instead of retrying at once: " + runner.LastDelay);
    }
}
