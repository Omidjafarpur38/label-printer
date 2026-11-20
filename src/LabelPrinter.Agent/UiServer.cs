using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabelPrinter.Agent.Core.Jobs;

namespace LabelPrinter.Agent;

/// <summary>
/// The local control panel: a small web app served on 127.0.0.1 only. Protection:
/// loopback listener and loopback peer; Host header must be a loopback name (DNS rebinding); a random per-run
/// token is exchanged once for an HttpOnly SameSite=Strict cookie; state-changing requests also need a custom
/// header (blocks cross-site form posts) and a same-origin Origin; strict CSP, no framing, no caching.
/// It exposes no way to submit or resubmit a job.
/// </summary>
internal sealed class UiServer : IDisposable
{
    private const int FirstPort = 17631, PortCount = 20;
    private const int MaxBody = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AgentService _service;
    private readonly HttpListener _listener = new();
    private readonly byte[] _tokenBytes = RandomNumberGenerator.GetBytes(32);
    private Thread? _thread;
    private volatile bool _stopping;

    public int Port { get; private set; }
    public string Token { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string OpenUrl => $"{BaseUrl}/?t={Uri.EscapeDataString(Token)}";
    public ManualResetEvent QuitRequested { get; } = new(false);

    public UiServer(AgentService service)
    {
        _service = service;
        Token = Convert.ToBase64String(_tokenBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public void Start()
    {
        for (var port = FirstPort; port < FirstPort + PortCount; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); }
            catch (HttpListenerException) { listener.Close(); continue; }
            listener.Close();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { _listener.Start(); }
            catch (HttpListenerException) { _listener.Prefixes.Clear(); continue; }
            Port = port;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ui-server" };
            _thread.Start();
            return;
        }
        throw new IOException("no free local port for the control panel");
    }

    public void Dispose()
    {
        _stopping = true;
        try { _listener.Stop(); _listener.Close(); } catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException) { }
        _thread?.Join(2000);
        QuitRequested.Dispose();
    }

    private void Loop()
    {
        while (!_stopping)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            try { Handle(ctx); }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { /* browser went away */ }
            catch (Exception ex)
            {
                _service.Log.Write("ui error: " + ex.GetType().Name);
                try { Send(ctx, 500, "text/plain; charset=utf-8", "خطای داخلی"u8.ToArray()); } catch (Exception) { }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = req.Url!.AbsolutePath;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; manifest-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), usb=(), serial=(), bluetooth=(), payment=(), clipboard-read=()";
        ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        ctx.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";

        if (!IsLoopbackHost(req.Url.Host) || req.RemoteEndPoint is null || !IPAddress.IsLoopback(req.RemoteEndPoint.Address)) { Send(ctx, 403, "text/plain; charset=utf-8", "forbidden"u8.ToArray()); return; }

        if (path == "/" && req.QueryString["t"] is { } t)
        {
            if (!TokenEquals(t)) { Send(ctx, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("پیوند نامعتبر است. پنجره را از آیکن برنامه باز کنید.")); return; }
            ctx.Response.AppendHeader("Set-Cookie", $"lp={Token}; Path=/; HttpOnly; SameSite=Strict");
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = "/";
            ctx.Response.Close();
            return;
        }

        if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            if (!Authenticated(req)) { SendJson(ctx, 401, new { error = "نشست معتبر نیست. پنجره را از آیکن برنامه دوباره باز کنید." }); return; }
            if (req.HttpMethod == "POST" && !SameOriginPost(req)) { SendJson(ctx, 403, new { error = "درخواست نامعتبر" }); return; }
            Api(ctx, req.HttpMethod, path);
            return;
        }

        if (req.HttpMethod is not ("GET" or "HEAD")) { Send(ctx, 405, "text/plain", "method not allowed"u8.ToArray()); return; }
        var (name, type) = path switch
        {
            "/" => ("index.html", "text/html; charset=utf-8"),
            "/app.js" => ("app.js", "text/javascript; charset=utf-8"),
            "/app.css" => ("app.css", "text/css; charset=utf-8"),
            "/manifest.webmanifest" => ("manifest.webmanifest", "application/manifest+json"),
            "/icon.png" => ("icon.png", "image/png"),
            "/logo-light.png" => ("logo-light.png", "image/png"),
            "/logo-dark.png" => ("logo-dark.png", "image/png"),
            "/Vazirmatn-Regular.woff2" => ("Vazirmatn-Regular.woff2", "font/woff2"),
            "/Vazirmatn-Bold.woff2" => ("Vazirmatn-Bold.woff2", "font/woff2"),
            _ => (null, null),
        };
        if (name is null) { Send(ctx, 404, "text/plain; charset=utf-8", "not found"u8.ToArray()); return; }
        if (path == "/" && !Authenticated(req))
        {
            Send(ctx, 403, "text/html; charset=utf-8", Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>عامل چاپ لیبل</title><body dir=rtl style=\"font-family:sans-serif;padding:2em\">این صفحه را باید از آیکن «Label Printer Agent» در منوی Start باز کنید."));
            return;
        }
        var asset = Asset(name);
        if (asset is null) { Send(ctx, 404, "text/plain", "missing asset"u8.ToArray()); return; }
        Send(ctx, 200, type!, asset, head: req.HttpMethod == "HEAD");
    }

    // ---- security helpers ----

    private static bool IsLoopbackHost(string host) => host is "127.0.0.1" or "localhost" or "[::1]";

    private bool TokenEquals(string candidate)
    {
        var a = Encoding.UTF8.GetBytes(candidate);
        var b = Encoding.UTF8.GetBytes(Token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private bool Authenticated(HttpListenerRequest req) => req.Cookies["lp"] is { } c && TokenEquals(c.Value);

    private bool SameOriginPost(HttpListenerRequest req)
    {
        if (req.Headers["X-LP"] != "1") return false;
        if (req.ContentType is null || !req.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) return false;
        var origin = req.Headers["Origin"];
        return origin is null || origin == $"http://127.0.0.1:{Port}" || origin == $"http://localhost:{Port}";
    }

    // ---- API ----

    private void Api(HttpListenerContext ctx, string method, string path)
    {
        if (method == "GET" && path == "/api/state") { SendJson(ctx, 200, BuildState()); return; }
        if (method != "POST") { SendJson(ctx, 405, new { error = "method not allowed" }); return; }

        JsonNode? body = null;
        if (ctx.Request.ContentLength64 > MaxBody) { SendJson(ctx, 413, new { error = "درخواست بزرگ است" }); return; }
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        {
            var text = reader.ReadToEnd();
            if (text.Length > MaxBody) { SendJson(ctx, 413, new { error = "درخواست بزرگ است" }); return; }
            if (text.Length > 0)
            {
                try { body = JsonNode.Parse(text); } catch (JsonException) { SendJson(ctx, 400, new { error = "داده‌ی نامعتبر" }); return; }
            }
        }

        string Str(string key) => body?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        double Num(string key) => body?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN;
        bool Bool(string key) => body?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

        ActionResult result;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // api, x[, id[, action]]
        switch (parts.Length >= 2 ? parts[1] : "")
        {
            case "pair": result = _service.Pair(Str("origin"), Str("code"), Str("name")); break;
            case "unpair": result = Bool("confirm") ? _service.Unpair() : new(false, "تأیید لازم است."); break;
            case "printer":
                var dpi = Num("dpi");
                result = double.IsNaN(dpi) ? new(false, "DPI نامعتبر است.") : _service.SelectPrinter(Str("queue"), Str("language"), (int)dpi, Num("widthMm"), Num("heightMm"));
                break;
            case "pause": result = _service.SetPaused(Bool("paused")); break;
            case "autostart": result = _service.SetAutostart(Bool("enabled")); break;
            case "quarantine": result = Bool("confirm") ? _service.ClearQuarantine() : new(false, "تأیید لازم است."); break;
            case "quit": QuitRequested.Set(); result = new(true, "برنامه بسته می‌شود."); break;
            case "jobs" when parts.Length == 4 && Guid.TryParseExact(parts[2], "D", out var id):
                // Exactly two job actions exist. There is deliberately no resubmit/retry route.
                if (parts[3] == "cancel") result = _service.CancelJob(id);
                else if (parts[3] == "spooler-delete") result = Bool("confirm") ? _service.DeleteFromSpooler(id) : new(false, "تأیید لازم است.");
                else { SendJson(ctx, 404, new { error = "not found" }); return; }
                break;
            default: SendJson(ctx, 404, new { error = "not found" }); return;
        }
        SendJson(ctx, result.Ok ? 200 : 400, new { ok = result.Ok, message = result.Message });
    }

    private object BuildState()
    {
        var identity = _service.Identity();
        var settings = _service.Settings();
        var ledger = _service.Ledger;
        IReadOnlyList<JobSnapshot> jobs = [];
        int pendingAccepts = 0, pendingEvents = 0;
        string? quarantine = null;
        try
        {
            if (ledger is not null)
            {
                jobs = ledger.Jobs();
                pendingAccepts = ledger.PendingAccepts().Count;
                pendingEvents = ledger.PendingEvents().Count;
                quarantine = ledger.QuarantineReason;
            }
        }
        catch (ObjectDisposedException) { /* worker is stopping */ }

        IReadOnlyList<string> queues;
        try { queues = _service.Platform.Queues(); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { queues = []; }

        return new
        {
            version = AgentHost.Version,
            state = _service.State.ToString(),
            message = Redactor.Apply(_service.Message, identity?.Origin),
            paused = _service.Paused,
            erpReachable = _service.ErpReachable,
            lastCleanAt = _service.LastCleanAt,
            paired = identity is null ? null : new { agentId = identity.AgentId, keyId = _service.CredentialKeyId(), pairedAt = identity.PairedAt, name = identity.DisplayName },
            printer = settings.Printer is { } p ? new { queue = p.Queue, language = p.Language, dpi = p.Dpi, widthMm = p.WidthMm, heightMm = p.HeightMm } : null,
            queues,
            autostart = _service.Platform.AutostartEnabled,
            quarantine,
            outbox = new { accepts = pendingAccepts, events = pendingEvents },
            jobs = jobs.OrderByDescending(j => j.UpdatedAt).Take(100).Select(j => new
            {
                id = j.Key.JobId,
                state = JobStateMachine.WireName(j.State),
                kind = j.Envelope.Kind,
                copies = j.Envelope.Copies,
                reprint = j.Envelope.ReprintOf is not null,
                createdAt = j.Envelope.CreatedAt,
                updatedAt = j.UpdatedAt,
                error = j.LastErrorCode,
                spoolerJobRef = j.SpoolerJobRef,
                blocked = j.Block is null ? null : new { code = j.Block.Code, count = j.Block.Count },
            }),
            log = _service.Log.Recent(40).Select(l => Redactor.Apply(l, identity?.Origin)),
        };
    }

    // ---- plumbing ----

    private static byte[]? Asset(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream("ui/" + name);
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void SendJson(HttpListenerContext ctx, int status, object value) =>
        Send(ctx, status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private static void Send(HttpListenerContext ctx, int status, string contentType, byte[] bytes, bool head = false)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        if (!head) ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }
}
