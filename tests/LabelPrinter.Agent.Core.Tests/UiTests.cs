using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Runtime;
using LabelPrinter.Agent.Core.Spooling;
using LabelPrinter.Agent.Core.Tests.Fakes;
using LabelPrinter.Agent.Core.Tests.Harness;

namespace LabelPrinter.Agent.Core.Tests;

internal sealed class TestPlatform : IPlatform
{
    public FakeSpooler Spooler { get; } = new();
    public List<string> Deleted { get; } = [];
    public bool DeleteResult { get; set; } = true;
    public bool Autostart { get; private set; }
    public bool RequiresExistingQueue => true;
    public IReadOnlyList<string> Queues() => ["Test Queue", "Other Queue"];
    public ISpooler CreateSpooler(PrinterSelection printer, Guid erpPrinterId, Action<string> log) => Spooler;
    public void WatchSpooler(JobLedger ledger, ISpooler spooler, string queue, Action<string> log) { }
    public bool DeleteSpoolerJob(string queue, string spoolerRef) { Deleted.Add(queue + "#" + spoolerRef); return DeleteResult; }
    public bool AutostartEnabled => Autostart;
    public void SetAutostart(bool enabled) => Autostart = enabled;
    public void OpenWindow(string url, string profileDir) { }
    public List<(string Path, string Args)> Installers { get; } = [];
    public bool LaunchResult { get; set; } = true;
    public bool LaunchInstaller(string path, string arguments) { Installers.Add((path, arguments)); return LaunchResult; }
}

/// <summary>The control panel end to end: real HTTP to the panel, real worker, mock ERP over sockets, fake spooler.</summary>
internal sealed class PanelBench : IDisposable
{
    private sealed class Plain : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plain) => plain.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => protectedBytes.ToArray();
    }

    public readonly string Dir = Path.Combine(Path.GetTempPath(), "lp-ui-" + Guid.NewGuid().ToString("N"));
    public readonly MockErpHttpServer Erp = new(new MockErp(TimeProvider.System));
    public readonly TestPlatform Platform = new();
    public readonly AgentHost Host;
    public readonly AgentService Service;
    public readonly UiServer Ui;
    public readonly HttpClient Http;
    public readonly CookieContainer Cookies = new();

    public PanelBench(bool login = true)
    {
        Host = new AgentHost(Dir, new Plain());
        Service = new AgentService(Host, Platform, new FileLog(Dir, echo: false), TimeProvider.System);
        Service.Start();
        Ui = new UiServer(Service);
        Ui.Start();
        Http = new HttpClient(new HttpClientHandler { CookieContainer = Cookies, AllowAutoRedirect = false }) { BaseAddress = new Uri(Ui.BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        if (login) Check.Equal(HttpStatusCode.Redirect, Http.GetAsync("/?t=" + Uri.EscapeDataString(Ui.Token)).Result.StatusCode, "token exchange");
    }

    public JsonNode State() => JsonNode.Parse(Http.GetStringAsync("/api/state").Result)!;

    public (int Status, JsonNode Body) Post(string path, object? body = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json") };
        req.Headers.Add("X-LP", "1");
        using var res = Http.Send(req);
        return ((int)res.StatusCode, JsonNode.Parse(new StreamReader(res.Content.ReadAsStream()).ReadToEnd()) ?? new JsonObject());
    }

    public void PairAndSelect()
    {
        var pair = Post("/api/pair", new { origin = Erp.Origin, code = "ABCD-1234", name = "test pc" });
        Check.True(pair.Status == 200, "pair: " + pair.Body);
        var printer = Post("/api/printer", new { queue = "Test Queue", language = "tspl", dpi = 203, widthMm = 50, heightMm = 30 });
        Check.True(printer.Status == 200, "printer: " + printer.Body);
        WaitFor(() => State()["state"]!.GetValue<string>() == "Running", "worker running");
    }

    public Guid Enqueue(int job)
    {
        var payload = Rig.Payload(job, 3000);
        var now = DateTimeOffset.UtcNow;
        var env = Rig.Envelope(payload, job) with
        {
            CreatedAt = now.AddMinutes(-1), AuthorizationExpiresAt = now.AddMinutes(30), AgentMustNotSubmitAfter = now.AddMinutes(30),
            PrinterId = Rig.Printer,
        };
        Erp.Erp.Enqueue(env, payload);
        return env.JobId;
    }

    public JsonNode? Job(Guid id) => State()["jobs"]!.AsArray().FirstOrDefault(j => j!["id"]!.GetValue<string>() == id.ToString("D"));
    public string? JobState(Guid id) => Job(id)?["state"]?.GetValue<string>();

    public static void WaitFor(Func<bool> condition, string what, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { if (condition()) return; Thread.Sleep(100); }
        throw new CheckFailedException("timed out waiting for: " + what);
    }

    public void Dispose()
    {
        Http.Dispose();
        Ui.Dispose();
        Service.Dispose();
        Erp.Dispose();
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }
}

public sealed class ControlPanelSecurityTests : IDisposable
{
    private readonly PanelBench _b = new(login: false);
    public void Dispose() => _b.Dispose();

    [Test] public void NothingIsServedWithoutTheSessionCookie()
    {
        Check.Equal(HttpStatusCode.Unauthorized, _b.Http.GetAsync("/api/state").Result.StatusCode, "state");
        Check.Equal(HttpStatusCode.Forbidden, _b.Http.GetAsync("/").Result.StatusCode, "index");
        Check.Equal(HttpStatusCode.Forbidden, _b.Http.GetAsync("/?t=wrong").Result.StatusCode, "wrong token");
        Check.Equal(HttpStatusCode.Unauthorized, _b.Post("/api/quit").Status == 401 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, "actions need the cookie");
        Check.True(!_b.Ui.QuitRequested.WaitOne(0), "quit was not triggered");
    }

    [Test] public void TokenIsExchangedForAnHttpOnlyStrictCookieAndRemovedFromTheUrl()
    {
        var res = _b.Http.GetAsync("/?t=" + Uri.EscapeDataString(_b.Ui.Token)).Result;
        Check.Equal(HttpStatusCode.Redirect, res.StatusCode, "redirect");
        Check.Equal("/", res.Headers.Location!.OriginalString, "token not kept in the URL");
        var cookie = res.Headers.GetValues("Set-Cookie").Single();
        Check.True(cookie.Contains("HttpOnly") && cookie.Contains("SameSite=Strict"), "cookie flags: " + cookie);
        Check.Equal(HttpStatusCode.OK, _b.Http.GetAsync("/").Result.StatusCode, "index with cookie");
    }

    [Test] public void StateChangingRequestsNeedCustomHeaderJsonAndSameOrigin()
    {
        _b.Http.GetAsync("/?t=" + Uri.EscapeDataString(_b.Ui.Token)).Wait();
        HttpStatusCode Send(Action<HttpRequestMessage> shape)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/pause") { Content = new StringContent("{\"paused\":true}", Encoding.UTF8, "application/json") };
            shape(req);
            return _b.Http.Send(req).StatusCode;
        }
        Check.Equal(HttpStatusCode.Forbidden, Send(_ => { }), "no X-LP header");
        Check.Equal(HttpStatusCode.Forbidden, Send(r => { r.Headers.Add("X-LP", "1"); r.Headers.Add("Origin", "http://evil.example"); }), "foreign Origin");
        Check.Equal(HttpStatusCode.Forbidden, Send(r => { r.Headers.Add("X-LP", "1"); r.Content = new StringContent("{}", Encoding.UTF8, "text/plain"); }), "form-like content type");
        Check.True(!_b.Service.Paused, "nothing changed");
        Check.Equal(HttpStatusCode.OK, Send(r => { r.Headers.Add("X-LP", "1"); r.Headers.Add("Origin", _b.Ui.BaseUrl); }), "legitimate request");
        Check.True(_b.Service.Paused, "now paused");
    }

    [Test] public void ResponsesCarryStrictHeaders()
    {
        var res = _b.Http.GetAsync("/app.js").Result;
        Check.True(res.Headers.GetValues("Content-Security-Policy").Single().Contains("default-src 'none'"), "CSP");
        Check.Equal("no-store", res.Headers.CacheControl!.ToString(), "no caching");
        var index = _b.Http.GetAsync("/?t=" + Uri.EscapeDataString(_b.Ui.Token)).Result; _ = index;
        var html = _b.Http.GetStringAsync("/").Result;
        Check.True(!html.Contains("<script>") && !html.Contains("onclick"), "no inline script");
        Check.True(html.Contains("dir=\"rtl\"") && html.Contains("عامل چاپ لیبل"), "Persian RTL page");
        foreach (var id in new[] { "panel-connect", "panel-printer", "panel-control", "panel-guide" }) Check.True(html.Contains("id=\"" + id + "\""), "tab panel " + id);
        Check.True(html.Contains("data-goto=\"guide\""), "connection form links to the guide");
    }

    [Test] public void PanelIsReachableOnLoopbackOnly()
    {
        Check.True(_b.Ui.BaseUrl.StartsWith("http://127.0.0.1:", StringComparison.Ordinal), "bound to 127.0.0.1");
        var bytes = Encoding.UTF8.GetBytes("x");
        _ = bytes;
    }
}

public sealed class ControlPanelFlowTests : IDisposable
{
    private readonly PanelBench _b = new();
    public void Dispose() => _b.Dispose();

    [Test] public void FirstRunWalksThroughPairingAndPrinterSelectionThenHandlesAJob()
    {
        var s = _b.State();
        Check.Equal("NotPaired", s["state"]!.GetValue<string>(), "starts unconfigured");
        Check.True(s["paired"] is null && s["printer"] is null, "nothing configured");

        Check.True(_b.Post("/api/pair", new { origin = "http://erp.example", code = "ABCDEFGH" }).Status == 400, "plain http to a non-loopback host is refused");
        _b.Post("/api/pair", new { origin = _b.Erp.Origin, code = "ABCD-1234", name = "صندوق ۱" });
        Check.Equal("NoPrinter", _b.State()["state"]!.GetValue<string>(), "paired, waiting for a printer");
        Check.True(_b.Post("/api/printer", new { queue = "No Such", language = "tspl", dpi = 203, widthMm = 50, heightMm = 30 }).Status == 400, "unknown queue refused");
        Check.True(_b.Post("/api/printer", new { queue = "Test Queue", language = "escpos", dpi = 203, widthMm = 50, heightMm = 30 }).Status == 400, "unsupported language refused");
        Check.True(_b.Post("/api/printer", new { queue = "Test Queue", language = "tspl", dpi = 203, widthMm = 5, heightMm = 30 }).Status == 400, "absurd size refused");
        Check.Equal(0, _b.Platform.Spooler.Submissions.Count, "nothing is printed before a printer is selected");
        Check.Equal(0, _b.Erp.Requests.Count(r => r.Contains("/jobs/claim")), "nothing is claimed before a printer is selected");

        Check.True(_b.Post("/api/printer", new { queue = "Test Queue", language = "tspl", dpi = 203, widthMm = 50, heightMm = 30 }).Status == 200, "printer saved");
        PanelBench.WaitFor(() => _b.State()["state"]!.GetValue<string>() == "Running", "worker running");

        var id = _b.Enqueue(1);
        PanelBench.WaitFor(() => _b.JobState(id) == "submitted_to_spooler", "job reaches the fake spooler");
        Check.Equal(1, _b.Platform.Spooler.Submissions.Count, "one fake-spooler submission");
        var job = _b.Job(id)!;
        Check.Equal("fake-1", job["spoolerJobRef"]!.GetValue<string>(), "spooler reference shown");
    }

    [Test] public void PauseStopsClaimingAndSubmittingAndResumeContinues()
    {
        _b.PairAndSelect();
        Check.True(_b.Post("/api/pause", new { paused = true }).Status == 200, "pause");
        Check.True(_b.State()["paused"]!.GetValue<bool>(), "state shows paused");
        var id = _b.Enqueue(2);
        Thread.Sleep(3000);
        Check.Equal(0, _b.Platform.Spooler.Submissions.Count, "nothing submitted while paused");
        Check.Equal("queued", _b.Erp.Erp.Job(id).State, "the ERP still holds it: nothing was claimed");

        _b.Post("/api/pause", new { paused = false });
        PanelBench.WaitFor(() => _b.JobState(id) == "submitted_to_spooler", "continues after resume");
        Check.Equal(1, _b.Platform.Spooler.Submissions.Count, "exactly one submission");
    }

    [Test] public void PauseSurvivesARestartOfTheWorker()
    {
        _b.PairAndSelect();
        _b.Post("/api/pause", new { paused = true });
        _b.Service.StopWorker();
        _b.Service.Start();
        Check.True(_b.Service.Paused, "still paused after restart");
        _b.Enqueue(3);
        Thread.Sleep(2500);
        Check.Equal(0, _b.Platform.Spooler.Submissions.Count, "still nothing submitted");
    }

    [Test] public void AcceptedJobCanBeCancelledAndIsNeverSubmitted()
    {
        _b.PairAndSelect();
        _b.Erp.Erp.FailNext("accept", MockErp.Fault.RequestLost); // the agent holds the job but the ERP has not confirmed it
        var id = _b.Enqueue(4);
        PanelBench.WaitFor(() => _b.JobState(id) == "accepted_by_agent", "job accepted locally");
        Check.Equal(0, _b.Platform.Spooler.Submissions.Count, "not submitted yet");

        var cancel = _b.Post($"/api/jobs/{id:D}/cancel");
        Check.True(cancel.Status == 200, "cancel: " + cancel.Body);
        PanelBench.WaitFor(() => _b.JobState(id) == "cancelled", "cancelled");
        PanelBench.WaitFor(() => _b.Erp.Erp.Job(id).State == "cancelled", "the ERP is told");
        Thread.Sleep(1500);
        Check.Equal(0, _b.Platform.Spooler.Submissions.Count, "never submitted");
    }

    [Test] public void SubmittedJobCanBeDeletedFromTheQueueAndBecomesUnknownNotRetried()
    {
        _b.PairAndSelect();
        var id = _b.Enqueue(5);
        PanelBench.WaitFor(() => _b.JobState(id) == "submitted_to_spooler", "submitted");
        Check.True(_b.Post($"/api/jobs/{id:D}/spooler-delete").Status == 400, "deletion needs confirmation");
        var del = _b.Post($"/api/jobs/{id:D}/spooler-delete", new { confirm = true });
        Check.True(del.Status == 200, "delete: " + del.Body);
        Check.Sequence(["Test Queue#fake-1"], _b.Platform.Deleted, "asked the OS queue to delete exactly that job");
        PanelBench.WaitFor(() => _b.JobState(id) == "unknown", "recorded as unknown");
        Thread.Sleep(2500);
        Check.Equal(1, _b.Platform.Spooler.Submissions.Count, "not submitted again");
    }

    [Test] public void NoActionLeadsToAResubmissionOfAFinishedOrUnknownJob()
    {
        _b.PairAndSelect();
        var id = _b.Enqueue(6);
        PanelBench.WaitFor(() => _b.JobState(id) == "submitted_to_spooler", "submitted");
        _b.Platform.DeleteResult = true;
        _b.Post($"/api/jobs/{id:D}/spooler-delete", new { confirm = true });
        PanelBench.WaitFor(() => _b.JobState(id) == "unknown", "unknown");
        Check.True(_b.Post($"/api/jobs/{id:D}/cancel").Status == 400, "an unknown job cannot be cancelled");
        Check.True(_b.Post($"/api/jobs/{id:D}/spooler-delete", new { confirm = true }).Status == 400, "nor deleted again");
        Check.True(_b.Post($"/api/jobs/{id:D}/resubmit").Status == 404, "there is no resubmit action at all");
        Check.True(_b.Post($"/api/jobs/{id:D}/retry").Status == 404, "nor a retry action");
        Check.Equal(1, _b.Platform.Spooler.Submissions.Count, "one submission in total");
    }

    [Test] public void FailedQueueDeletionChangesNothing()
    {
        _b.PairAndSelect();
        var id = _b.Enqueue(7);
        PanelBench.WaitFor(() => _b.JobState(id) == "submitted_to_spooler", "submitted");
        _b.Platform.DeleteResult = false;
        Check.True(_b.Post($"/api/jobs/{id:D}/spooler-delete", new { confirm = true }).Status == 400, "refused by the OS");
        Check.Equal("submitted_to_spooler", _b.JobState(id), "state unchanged");
    }

    [Test] public void AutostartAndPrinterChangeAreAppliedAndPersisted()
    {
        _b.PairAndSelect();
        Check.True(_b.Post("/api/autostart", new { enabled = true }).Status == 200 && _b.Platform.Autostart, "autostart on");
        Check.True(_b.State()["autostart"]!.GetValue<bool>(), "state reflects it");
        _b.Post("/api/autostart", new { enabled = false });
        Check.True(!_b.Platform.Autostart, "autostart off");

        var before = _b.Service.Settings().Printer!.LocalPrinterId;
        _b.Post("/api/printer", new { queue = "Other Queue", language = "zpl", dpi = 300, widthMm = 60, heightMm = 40 });
        var after = _b.Service.Settings().Printer!;
        Check.True(after.LocalPrinterId != before && after.Queue == "Other Queue" && after.Language == "zpl", "a different queue is a different printer for the ERP");
        PanelBench.WaitFor(() => _b.State()["state"]!.GetValue<string>() == "Running", "worker restarted with the new printer");
    }

    [Test] public void UnpairKeepsTheJournalAndStopsTheWorker()
    {
        _b.PairAndSelect();
        var id = _b.Enqueue(8);
        PanelBench.WaitFor(() => _b.JobState(id) == "submitted_to_spooler", "submitted");
        Check.True(_b.Post("/api/unpair").Status == 400, "needs confirmation");
        Check.True(_b.Post("/api/unpair", new { confirm = true }).Status == 200, "unpaired");
        Check.Equal("NotPaired", _b.State()["state"]!.GetValue<string>(), "state");
        Check.True(File.Exists(_b.Host.JournalPath), "journal kept");
        Check.True(!File.Exists(Path.Combine(_b.Dir, "credential.bin")), "credential removed");
        Check.True(_b.State()["jobs"]!.AsArray().Count == 0, "no worker, no job list (the journal is not open)");
    }

    [Test] public void QuarantineIsShownAndClearingNeedsConfirmation()
    {
        _b.PairAndSelect();
        _b.Service.Ledger!.Quarantine("TEST");
        PanelBench.WaitFor(() => _b.State()["quarantine"]?.GetValue<string>() == "TEST", "quarantine visible");
        Check.True(_b.Post("/api/quarantine").Status == 400, "needs confirmation");
        var id = _b.Enqueue(9);
        Thread.Sleep(2000);
        Check.Equal(0, _b.Platform.Spooler.Submissions.Count, "nothing is submitted while quarantined");
        Check.True(_b.Post("/api/quarantine", new { confirm = true }).Status == 200, "cleared");
        PanelBench.WaitFor(() => _b.State()["quarantine"] is null, "quarantine gone");
        _ = id;
    }

    [Test] public void QuitSignalsTheHost()
    {
        Check.True(!_b.Ui.QuitRequested.WaitOne(0), "not yet");
        _b.Post("/api/quit");
        Check.True(_b.Ui.QuitRequested.WaitOne(2000), "quit requested");
    }

    [Test] public void LogAndStateNeverContainTheCredential()
    {
        _b.PairAndSelect();
        _b.Enqueue(10);
        Thread.Sleep(1500);
        var state = _b.Http.GetStringAsync("/api/state").Result;
        Check.True(!state.Contains(_b.Erp.Credential, StringComparison.Ordinal) && !state.Contains(AgentCredential.Scheme + "_", StringComparison.Ordinal), "state has no credential");
        Check.True(!File.ReadAllText(Path.Combine(_b.Dir, "agent.log")).Contains(AgentCredential.Scheme + "_", StringComparison.Ordinal), "log has no credential");
    }
}

public sealed class PauseHoldTests
{
    [Test] public void HoldStopsClaimAndSubmissionButNotReportsOrCancellation()
    {
        var b = new Bench();
        b.Enqueue(1);
        var held = true;
        var sync = new Erp.ErpSync(b.R.Ledger, b.Erp, b.R.Spooler, b.R.Clock) { Hold = () => held };
        sync.RunCycle();
        Check.Equal(0, b.R.Ledger.Jobs().Count, "nothing claimed while held");
        Check.Equal(0, b.FakeSubmissions, "nothing submitted while held");
        held = false;
        sync.RunCycle();
        Check.Equal(1, b.FakeSubmissions, "continues when released");

        // A job accepted before the hold is not submitted while held, can be cancelled, and reports still flow.
        held = true;
        b.Erp.Enqueue(Rig.Envelope(Rig.Payload(2), 2), Rig.Payload(2));
        held = false;
        b.Erp.FailNext("accept", MockErp.Fault.RequestLost);
        sync.RunCycle();
        var second = Rig.JobId(2);
        Check.Equal(JobState.Accepted, b.Local(second), "accepted, unconfirmed");
        held = true;
        b.Erp.Job(second).CancelRequested = false;
        sync.RunCycle();
        Check.Equal(1, b.FakeSubmissions, "still one submission while held");
        Check.True(b.R.Ledger.Cancel(second), "operator cancel works while held");
        sync.RunCycle();
        Check.Equal("cancelled", b.Server(second), "the cancellation is reported while held");
        Check.Equal(0, b.R.Ledger.PendingEvents().Count, "outbox drained");
    }
}

/// <summary>The window never shows (or even receives) the server address or any port, and the embedded font and
/// logo are served under the strict policy.</summary>
public sealed class ControlPanelPrivacyTests : IDisposable
{
    private readonly PanelBench _b = new();
    public void Dispose() => _b.Dispose();

    [Test] public void ServerAddressAndPortsAreNeverSentToThePage()
    {
        _b.PairAndSelect();
        var raw = _b.Http.GetStringAsync("/api/state").Result;
        var erpUri = new Uri(_b.Erp.Origin);
        Check.True(!raw.Contains("\"origin\"", StringComparison.OrdinalIgnoreCase), "no origin field in the state");
        Check.True(!raw.Contains(erpUri.Authority, StringComparison.Ordinal), "server host:port is not in the state");
        Check.True(!raw.Contains(_b.Ui.BaseUrl, StringComparison.Ordinal), "panel address is not in the state");
        Check.True(!raw.Contains("poshakerp", StringComparison.OrdinalIgnoreCase), "built-in host is not in the state");
        Check.True(_b.State()["paired"]!["name"]!.GetValue<string>() == "test pc", "the computer name is still shown");
    }

    [Test] public void PageSourcesContainNoServerAddress()
    {
        foreach (var path in new[] { "/", "/app.js", "/app.css", "/manifest.webmanifest" })
        {
            var body = _b.Http.GetStringAsync(path).Result;
            Check.True(!body.Contains("poshakerp", StringComparison.OrdinalIgnoreCase), path + " has no server host");
            Check.True(!body.Contains("https://", StringComparison.OrdinalIgnoreCase), path + " has no https address");
        }
        Check.True(!_b.Http.GetStringAsync("/").Result.Contains("in-origin"), "no address input in the page");
    }

    [Test] public void PairingWithoutAnAddressUsesTheBuiltInServerAndFailsClosedWhenUnreachable()
    {
        var res = _b.Post("/api/pair", new { code = "ABCD-1234", name = "x" });
        Check.True(res.Status == 400, "built-in server is not reachable from the test, so it must fail, not connect elsewhere");
        var msg = res.Body["message"]!.GetValue<string>();
        Check.True(!msg.Contains("poshakerp", StringComparison.OrdinalIgnoreCase) && !msg.Contains("://"), "error does not name the server: " + msg);
        Check.True(_b.State()["paired"] is null, "nothing was paired");
    }

    [Test] public void RedactorRemovesUrlsHostsAndPorts()
    {
        Check.Equal("failed [سرور] now", Redactor.Apply("failed https://poshakerp.top/api/x?y=1 now"), "url");
        Check.Equal("no such host ([سرور])", Redactor.Apply("no such host (erp.example.com:443)"), "host:port");
        Check.Equal("connect [سرور] refused", Redactor.Apply("connect 127.0.0.1:5123 refused"), "ipv4:port");
        Check.Equal("[سرور]", Redactor.Apply("poshakerp.top"), "built-in host");
        Check.Equal("[سرور] down", Redactor.Apply("shop.example.org down", "https://shop.example.org"), "origin host");
        Check.Equal("agent.log is full", Redactor.Apply("agent.log is full"), "file names are left alone");
        Check.Equal("", Redactor.Apply(null), "null");
    }

    [Test] public void FontAndLogosAreServedWithPolicyThatAllowsThem()
    {
        var f = _b.Http.GetAsync("/Vazirmatn-Regular.woff2").Result;
        Check.Equal(HttpStatusCode.OK, f.StatusCode, "font");
        Check.Equal("font/woff2", f.Content.Headers.ContentType!.MediaType!, "font type");
        Check.True(f.Content.ReadAsByteArrayAsync().Result.Length > 10_000, "font bytes");
        foreach (var p in new[] { "/icon.png", "/logo-light.png", "/logo-dark.png", "/Vazirmatn-Bold.woff2" })
            Check.Equal(HttpStatusCode.OK, _b.Http.GetAsync(p).Result.StatusCode, p);
        var csp = f.Headers.GetValues("Content-Security-Policy").Single();
        Check.True(csp.Contains("font-src 'self'") && csp.Contains("default-src 'none'") && !csp.Contains("unsafe"), "csp: " + csp);
        Check.True(f.Headers.Contains("Permissions-Policy") && f.Headers.Contains("Cross-Origin-Opener-Policy") && f.Headers.Contains("Cross-Origin-Resource-Policy"), "extra isolation headers");
    }
}
