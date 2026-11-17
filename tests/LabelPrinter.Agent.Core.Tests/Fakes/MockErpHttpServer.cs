using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Erp;
using LabelPrinter.Agent.Core.Jobs;

namespace LabelPrinter.Agent.Core.Tests.Fakes;

/// <summary>
/// Serves <see cref="MockErp"/> over real loopback HTTP with the ERP's routes, JSON shapes, headers and flat
/// error body, so that <see cref="HttpErpClient"/> is exercised over sockets. Still a mock: plain http, one
/// accepted credential, no rate limiting unless a test asks for it.
/// </summary>
public sealed class MockErpHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Thread _thread;
    private volatile bool _stopping;

    public MockErp Erp { get; }
    public string Origin { get; }
    public string Credential { get; } = AgentCredential.Scheme + "_" + new string('k', 22) + "." + new string('s', 43); // synthetic filler
    public bool Revoked { get; set; }
    /// <summary>Answers the next N requests with this status (and Retry-After for 429).</summary>
    public (int Count, int Status, int? RetryAfterSec) Force { get; set; }
    public string? RedirectTo { get; set; }
    public TimeSpan Stall { get; set; }
    public string? RawBodyOnce { get; set; }
    public int ExtraPayloadBytes { get; set; }
    public List<string> Requests { get; } = [];
    public List<string> AuthorizationSeen { get; } = [];

    public MockErpHttpServer(MockErp erp)
    {
        Erp = erp;
        var port = FreePort();
        Origin = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Origin + "/");
        _listener.Start();
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stopping = true;
        try { _listener.Stop(); _listener.Close(); } catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException) { }
        _thread.Join(2000);
    }

    private void Loop()
    {
        while (!_stopping)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            try { Handle(ctx); }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { /* client went away */ }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var request = ctx.Request;
        var path = request.Url!.AbsolutePath;
        lock (Requests) { Requests.Add(request.HttpMethod + " " + request.Url.PathAndQuery); AuthorizationSeen.Add(request.Headers["Authorization"] ?? ""); }
        var body = new StreamReader(request.InputStream, Encoding.UTF8).ReadToEnd();

        if (Stall > TimeSpan.Zero) Thread.Sleep(Stall);
        if (RedirectTo is { } target) { ctx.Response.StatusCode = 307; ctx.Response.RedirectLocation = target + request.Url.PathAndQuery; ctx.Response.Close(); return; }
        if (Force.Count > 0)
        {
            Force = (Force.Count - 1, Force.Status, Force.RetryAfterSec);
            if (Force.RetryAfterSec is { } s) ctx.Response.Headers["Retry-After"] = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Error(ctx, Force.Status, Force.Status == 429 ? "SYSTEM_INTERNAL_ERROR" : "SYSTEM_HTTP_ERROR");
            return;
        }
        if (RawBodyOnce is { } raw) { RawBodyOnce = null; Write(ctx, 200, "application/json", Encoding.UTF8.GetBytes(raw)); return; }

        const string prefix = HttpErpClient.ApiPath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) { Error(ctx, 404, "SYSTEM_HTTP_ERROR"); return; }
        if (request.Headers["Authorization"] != "Bearer " + Credential) { Error(ctx, 401, "AGENT_UNAUTHENTICATED"); return; }
        if (Revoked) { Error(ctx, 401, "AGENT_REVOKED"); return; }

        var route = path[prefix.Length..];
        var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            JsonNode json = body.Length > 0 ? JsonNode.Parse(body)! : new JsonObject();
            switch (request.HttpMethod, parts)
            {
                case ("POST", ["heartbeat"]):
                    Json(ctx, new JsonObject { ["negotiated"] = "0.1", ["serverTime"] = DateTimeOffset.UtcNow.ToString("O"), ["cancelJobIds"] = new JsonArray() });
                    break;
                case ("PUT", ["printers"]):
                    Json(ctx, new JsonObject { ["printers"] = new JsonArray([.. json["printers"]!.AsArray().Select(p => (JsonNode)new JsonObject { ["localPrinterId"] = p!["localPrinterId"]!.GetValue<string>(), ["printerId"] = Rig.Printer.ToString("D") })]) });
                    break;
                case ("POST", ["jobs", "claim"]):
                    var claim = Erp.Claim(json["max"]?.GetValue<int>() ?? 1);
                    Json(ctx, new JsonObject { ["jobs"] = new JsonArray([.. claim.Jobs.Select(Envelope)]), ["cancelJobIds"] = Ids(claim.CancelJobIds), ["paused"] = claim.Paused });
                    break;
                case ("POST", ["jobs", "reconcile"]):
                    var directives = Erp.Reconcile([.. json["jobs"]!.AsArray().Select(j => new LocalJobReport(Guid.Parse(j!["jobId"]!.GetValue<string>()), j["state"]!.GetValue<string>(), j["lastSeq"]!.GetValue<int>()))]);
                    Json(ctx, new JsonObject { ["directives"] = new JsonArray([.. directives.Select(d =>
                    {
                        var o = new JsonObject { ["jobId"] = d.JobId.ToString("D"), ["action"] = d.Action switch { ReconcileAction.Resume => "resume", ReconcileAction.Abandon => "abandon", _ => "report_events" } };
                        if (d.FromSeq is { } f) o["fromSeq"] = f;
                        return (JsonNode)o;
                    })]) });
                    break;
                case ("GET", ["jobs", var id, "payload"]):
                    var payload = Erp.GetPayload(Guid.Parse(id));
                    ctx.Response.Headers["X-Payload-Sha256"] = payload.Sha256Header;
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    Write(ctx, 200, "application/octet-stream", [.. payload.Bytes, .. new byte[ExtraPayloadBytes]]);
                    break;
                case ("POST", ["jobs", var id, "accept"]):
                    var accept = Erp.Accept(Guid.Parse(id), json["attempt"]!.GetValue<int>());
                    Json(ctx, new JsonObject { ["state"] = accept.State, ["cancelRequested"] = accept.CancelRequested, ["duplicate"] = accept.Duplicate });
                    break;
                case ("POST", ["jobs", var id, "events"]):
                    var jobId = Guid.Parse(id);
                    var events = json["events"]!.AsArray().Select(e => new StatusEvent(
                        Guid.Parse(e!["eventId"]!.GetValue<string>()), jobId, e["seq"]!.GetValue<int>(), e["attempt"]?.GetValue<int>() ?? 1,
                        Enum.GetValues<JobState>().Single(s => JobStateMachine.WireName(s) == e["state"]!.GetValue<string>()),
                        DateTimeOffset.Parse(e["at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
                        e["error"]?["code"]?.GetValue<string>(), e["error"]?["message"]?.GetValue<string>(), e["spoolerJobRef"]?.GetValue<string>(),
                        e["evidence"] is { } ev ? new Evidence(ev["level"]!.GetValue<string>(), ev["detail"]?.GetValue<string>()) : null)).ToList();
                    var result = Erp.PostEvents(jobId, events);
                    Json(ctx, new JsonObject
                    {
                        ["results"] = new JsonArray([.. result.Results.Select(r => (JsonNode)new JsonObject { ["eventId"] = r.EventId.ToString("D"), ["applied"] = r.Applied, ["duplicate"] = r.Duplicate, ["stale"] = r.Stale })]),
                        ["state"] = result.State, ["lastSeq"] = result.LastSeq,
                    });
                    break;
                default:
                    Error(ctx, 404, "SYSTEM_HTTP_ERROR");
                    break;
            }
        }
        catch (ErpException ex) { Error(ctx, ex.Status, ex.ErrorCode); }
        catch (ErpUnavailableException) { ctx.Response.Abort(); } // request or answer lost: the client sees a dropped connection
    }

    private static JsonNode Envelope(JobEnvelope e)
    {
        var output = new JsonObject
        {
            ["language"] = e.Output.Language, ["profile"] = e.Output.Profile, ["contentType"] = e.Output.ContentType, ["sizeBytes"] = e.Output.SizeBytes,
            ["sha256"] = e.Output.Sha256, ["pages"] = e.Output.Pages, ["dpi"] = e.Output.Dpi,
            ["mediaMm"] = new JsonObject { ["w"] = e.Output.MediaWidthMm, ["h"] = e.Output.MediaHeightMm }, ["gapMm"] = e.Output.GapMm,
        };
        static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var o = new JsonObject
        {
            ["jobId"] = e.JobId.ToString("D"), ["idempotencyKey"] = e.IdempotencyKey, ["contractVersion"] = e.ContractVersion, ["agentId"] = e.AgentId.ToString("D"),
            ["printerId"] = e.PrinterId.ToString("D"), ["profileId"] = e.ProfileId.ToString("D"), ["profileVersion"] = e.ProfileVersion, ["kind"] = e.Kind, ["output"] = output,
            ["copies"] = e.Copies, ["createdAt"] = Iso(e.CreatedAt), ["authorizationExpiresAt"] = Iso(e.AuthorizationExpiresAt), ["agentMustNotSubmitAfter"] = Iso(e.AgentMustNotSubmitAfter),
            ["correlationId"] = e.CorrelationId, ["deliveryAttempt"] = e.DeliveryAttempt, ["leaseUntil"] = Iso(e.LeaseUntil), ["futureField"] = "ignored by old agents",
        };
        if (e.ReprintOf is { } r) o["reprintOf"] = r.ToString("D");
        return o;
    }

    private static JsonArray Ids(IEnumerable<Guid> ids) => new([.. ids.Select(i => (JsonNode)JsonValue.Create(i.ToString("D"))!)]);

    private static void Json(HttpListenerContext ctx, JsonNode node) => Write(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(node.ToJsonString()));

    private static void Error(HttpListenerContext ctx, int status, string? code)
    {
        var body = new JsonObject { ["error"] = "Error", ["message"] = "پیام", ["trace_id"] = Guid.NewGuid().ToString("D"), ["user_friendly_message_fa"] = "پیام" };
        if (code is not null) body["error_code"] = code;
        Write(ctx, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(body.ToJsonString()));
    }

    private static void Write(HttpListenerContext ctx, int status, string contentType, byte[] bytes)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }
}
