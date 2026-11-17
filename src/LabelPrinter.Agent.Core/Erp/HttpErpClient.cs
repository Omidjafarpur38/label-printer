using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Jobs;

namespace LabelPrinter.Agent.Core.Erp;

/// <summary>The ERP answered 429. <see cref="RetryAfter"/> is the Retry-After header when present.</summary>
public sealed record ErpThrottle(TimeSpan? RetryAfter);

[ContractModel(ContractStatus.VerifiedAgainstErpSource, "agent.service.ts pair()")]
public sealed record PairResult(Guid AgentId, string Credential, string NegotiatedContract, int HeartbeatSec, int ClaimWaitSec);

[ContractModel(ContractStatus.VerifiedAgainstErpSource, "agent.service.ts heartbeat()")]
public sealed record HeartbeatResult(string Negotiated, IReadOnlyList<Guid> CancelJobIds);

/// <summary>One printer as reported to PUT /printers (controller printerSchema, strict).</summary>
[ContractModel(ContractStatus.VerifiedAgainstErpSource, "label-agent.controller.ts printerSchema")]
public sealed record PrinterReport(Guid LocalPrinterId, string DisplayName, string? WindowsQueue, string Language, int Dpi, double WidthMm, double HeightMm);

public sealed record HttpErpOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Long-poll seconds sent with claim (server caps at 25). 0 = answer immediately.</summary>
    public int ClaimWaitSec { get; init; } = 20;
    public string AgentVersion { get; init; } = "0.0.0";
    public long MaxJsonBytes { get; init; } = 1024 * 1024;
}

/// <summary>
/// HTTPS transport for the ERP agent API (/api/label-agent/v1), following label-agent.controller.ts and
/// label-agent.routes.ts at the baseline commit.
/// - The origin must be https. Plain http is accepted only for a loopback host (local test ERP).
/// - Certificate validation is the platform default and cannot be turned off here.
/// - Redirects are not followed, so the credential is never sent to another origin; cookies are not used.
/// - Nothing is retried here. Every failure without a usable answer becomes <see cref="ErpUnavailableException"/>
///   and the caller's cycle logic (which is idempotent) decides when to try again.
/// - The credential is only ever placed in the Authorization header; it is not logged or put in a URL.
/// </summary>
public sealed class HttpErpClient : IErpClient, IDisposable
{
    public const string ApiPath = "/api/label-agent/v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly HttpErpOptions _options;
    private readonly Func<AgentCredential?> _credential;

    public Uri Origin { get; }
    /// <summary>Set when the last answer was 429.</summary>
    public ErpThrottle? LastThrottle { get; private set; }

    public HttpErpClient(string origin, Func<AgentCredential?> credential, HttpErpOptions? options = null, HttpMessageHandler? handler = null)
    {
        Origin = NormalizeOrigin(origin);
        _credential = credential;
        _options = options ?? new HttpErpOptions();
        _http = new HttpClient(handler ?? CreateHandler(), disposeHandler: true)
        {
            BaseAddress = Origin,
            Timeout = Timeout.InfiniteTimeSpan, // per-request timeouts below
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("label-printer-agent", Sanitize(_options.AgentVersion)));
    }

    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>Scheme, host and port only, lower-case, no trailing slash. Throws for anything unsafe.</summary>
    public static Uri NormalizeOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) throw new ArgumentException("ERP origin is not an absolute URL.", nameof(origin));
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("ERP origin must not contain credentials.", nameof(origin));
        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("ERP origin must be scheme://host[:port] without path or query.", nameof(origin));
        var secure = uri.Scheme == Uri.UriSchemeHttps;
        if (!secure && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new ArgumentException("ERP origin must use https (http is allowed for loopback only).", nameof(origin));
        return new Uri(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant());
    }

    public static string OriginKey(string origin) => NormalizeOrigin(origin).GetLeftPart(UriPartial.Authority);

    // ---- unauthenticated ----

    /// <summary>Exchanges a pairing code for a credential. Not idempotent (the code is consumed): never retried.</summary>
    public PairResult Pair(string pairingCode, Guid installId, string displayName, string osName, string? osBuild)
    {
        var body = new JsonObject
        {
            ["pairingCode"] = pairingCode,
            ["installId"] = installId.ToString("D"),
            ["displayName"] = displayName,
            ["agentVersion"] = _options.AgentVersion,
            ["supportedContracts"] = Contracts(),
        };
        var os = new JsonObject { ["name"] = osName };
        if (osBuild is not null) os["build"] = osBuild; // optional in the ERP schema, but not nullable
        body["os"] = os;
        var r = SendJson(HttpMethod.Post, "/pair", body, authenticated: false, _options.RequestTimeout);
        var credential = Str(r, "credential");
        if (!AgentCredential.TryParse(credential, out _)) throw new ErpUnavailableException("pair: credential has an unexpected shape");
        return new PairResult(Id(r, "agentId"), credential, Str(r, "negotiatedContract"), Int(r, "heartbeatSec", 30), Int(r, "claimWaitSec", 20));
    }

    // ---- authenticated, beyond IErpClient ----

    public HeartbeatResult Heartbeat()
    {
        var r = SendJson(HttpMethod.Post, "/heartbeat", new JsonObject { ["agentVersion"] = _options.AgentVersion, ["supportedContracts"] = Contracts() }, true, _options.RequestTimeout);
        return new HeartbeatResult(Str(r, "negotiated"), Ids(r, "cancelJobIds"));
    }

    /// <summary>Full replacement report. Returns localPrinterId → ERP printerId.</summary>
    public IReadOnlyDictionary<Guid, Guid> ReportPrinters(IReadOnlyList<PrinterReport> printers)
    {
        var list = new JsonArray();
        foreach (var p in printers)
        {
            var item = new JsonObject
            {
                ["localPrinterId"] = p.LocalPrinterId.ToString("D"),
                ["displayName"] = p.DisplayName,
                // Capabilities are declared by the operator, never claimed as verified by the agent.
                ["languages"] = new JsonArray(new JsonObject { ["language"] = p.Language, ["verification"] = new JsonObject { ["status"] = "unverified" } }),
                ["transports"] = new JsonArray("spooler_raw"),
                ["renderModes"] = new JsonArray("bitmap_1bit"),
                ["dpi"] = new JsonObject { ["native"] = p.Dpi },
                ["media"] = new JsonObject { ["widthMm"] = p.WidthMm, ["heightMm"] = p.HeightMm },
                ["options"] = new JsonObject(),
            };
            if (p.WindowsQueue is not null) item["windowsQueue"] = p.WindowsQueue;
            list.Add(item);
        }
        var r = SendJson(HttpMethod.Put, "/printers", new JsonObject { ["printers"] = list }, true, _options.RequestTimeout);
        var map = new Dictionary<Guid, Guid>();
        foreach (var e in Arr(r, "printers")) map[Id(e, "localPrinterId")] = Id(e, "printerId");
        return map;
    }

    /// <summary>Asks for a new credential. The old one stays valid until the new one is first used.</summary>
    public string RotateCredential()
    {
        var r = SendJson(HttpMethod.Post, "/credential/rotate", new JsonObject(), true, _options.RequestTimeout);
        var credential = Str(r, "credential");
        if (!AgentCredential.TryParse(credential, out _)) throw new ErpUnavailableException("rotate: credential has an unexpected shape");
        return credential;
    }

    // ---- IErpClient ----

    public ClaimResponse Claim(int max)
    {
        var wait = Math.Clamp(_options.ClaimWaitSec, 0, 25);
        var r = SendJson(HttpMethod.Post, "/jobs/claim", new JsonObject { ["max"] = max, ["waitSec"] = wait }, true, TimeSpan.FromSeconds(wait) + _options.RequestTimeout);
        return new ClaimResponse([.. Arr(r, "jobs").Select(ParseEnvelope)], Ids(r, "cancelJobIds"), r.TryGetProperty("paused", out var p) && p.ValueKind == JsonValueKind.True);
    }

    public PayloadResponse GetPayload(Guid jobId)
    {
        using var response = Send(HttpMethod.Get, $"/jobs/{jobId:D}/payload", null, true, _options.RequestTimeout + TimeSpan.FromSeconds(60), out var cts);
        using (cts)
        {
            if (response.Content.Headers.ContentType?.MediaType != "application/octet-stream")
                throw new ErpUnavailableException("payload: unexpected content type");
            var bytes = ReadBounded(response, ContractInfo.MaxPayloadBytes, cts.Token);
            var sha = response.Headers.TryGetValues("X-Payload-Sha256", out var v) ? v.FirstOrDefault() : null;
            return new PayloadResponse(bytes, sha);
        }
    }

    public AcceptResponse Accept(Guid jobId, int attempt)
    {
        var r = SendJson(HttpMethod.Post, $"/jobs/{jobId:D}/accept", new JsonObject { ["attempt"] = attempt }, true, _options.RequestTimeout);
        return new AcceptResponse(Str(r, "state"), Bool(r, "cancelRequested"), Bool(r, "duplicate"));
    }

    public EventsResponse PostEvents(Guid jobId, IReadOnlyList<StatusEvent> events)
    {
        var list = new JsonArray();
        foreach (var e in events)
        {
            var item = new JsonObject
            {
                ["eventId"] = e.EventId.ToString("D"),
                ["seq"] = e.Seq,
                ["attempt"] = e.Attempt,
                ["state"] = JobStateMachine.WireName(e.State),
                ["at"] = e.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            };
            if (e.ErrorCode is not null || e.ErrorMessage is not null)
            {
                var error = new JsonObject();
                if (e.ErrorCode is not null) error["code"] = e.ErrorCode;
                if (e.ErrorMessage is not null) error["message"] = e.ErrorMessage;
                item["error"] = error;
            }
            if (e.SpoolerJobRef is not null) item["spoolerJobRef"] = e.SpoolerJobRef;
            if (e.Evidence is not null)
            {
                var evidence = new JsonObject { ["level"] = e.Evidence.Level };
                if (e.Evidence.Detail is not null) evidence["detail"] = e.Evidence.Detail;
                item["evidence"] = evidence;
            }
            list.Add(item);
        }
        var r = SendJson(HttpMethod.Post, $"/jobs/{jobId:D}/events", new JsonObject { ["events"] = list }, true, _options.RequestTimeout);
        var results = Arr(r, "results").Select(x => new EventResult(Id(x, "eventId"), Bool(x, "applied"), Bool(x, "duplicate"), Bool(x, "stale"))).ToList();
        return new EventsResponse(results, r.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null, Int(r, "lastSeq", 0));
    }

    public IReadOnlyList<ReconcileDirective> Reconcile(IReadOnlyList<LocalJobReport> jobs)
    {
        var list = new JsonArray();
        foreach (var j in jobs) list.Add(new JsonObject { ["jobId"] = j.JobId.ToString("D"), ["state"] = j.State, ["lastSeq"] = j.LastSeq });
        var r = SendJson(HttpMethod.Post, "/jobs/reconcile", new JsonObject { ["jobs"] = list }, true, _options.RequestTimeout);
        return [.. Arr(r, "directives").Select(d => new ReconcileDirective(
            Id(d, "jobId"),
            Str(d, "action") switch
            {
                "resume" => ReconcileAction.Resume,
                "abandon" => ReconcileAction.Abandon,
                "report_events" => ReconcileAction.ReportEvents,
                var other => throw new ErpUnavailableException("reconcile: unknown action " + Sanitize(other)),
            },
            d.TryGetProperty("fromSeq", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : null))];
    }

    public void Dispose() => _http.Dispose();

    // ---- wire helpers ----

    /// <summary>
    /// Maps a claim envelope. A structurally unusable envelope (missing or mistyped field) cannot be given a
    /// truthful identity, so the whole answer is treated as unusable instead of guessing.
    /// </summary>
    public static JobEnvelope ParseEnvelope(JsonElement e)
    {
        var o = Obj(e, "output");
        double? Num(JsonElement parent, string name) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var media = o.TryGetProperty("mediaMm", out var m) ? m : default;
        return new JobEnvelope(
            JobId: Id(e, "jobId"),
            IdempotencyKey: Str(e, "idempotencyKey"),
            ContractVersion: Str(e, "contractVersion"),
            AgentId: Id(e, "agentId"),
            PrinterId: Id(e, "printerId"),
            ProfileId: Id(e, "profileId"),
            ProfileVersion: Int(e, "profileVersion"),
            Kind: Str(e, "kind"),
            Output: new JobOutput(Str(o, "language"), Str(o, "profile"), Str(o, "contentType"), Long(o, "sizeBytes"), Str(o, "sha256"),
                Int(o, "pages"), Int(o, "dpi"), Num(media, "w"), Num(media, "h"), Num(o, "gapMm")),
            Copies: Int(e, "copies"),
            CreatedAt: Time(e, "createdAt"),
            AuthorizationExpiresAt: Time(e, "authorizationExpiresAt"),
            AgentMustNotSubmitAfter: Time(e, "agentMustNotSubmitAfter"),
            CorrelationId: Str(e, "correlationId"),
            ReprintOf: e.TryGetProperty("reprintOf", out var rp) && rp.ValueKind == JsonValueKind.String ? Id(e, "reprintOf") : null,
            DeliveryAttempt: Int(e, "deliveryAttempt"),
            LeaseUntil: Time(e, "leaseUntil"));
    }

    private JsonArray Contracts() => new([.. ContractInfo.Supported.Select(v => (JsonNode)JsonValue.Create(v)!)]);

    private JsonElement SendJson(HttpMethod method, string path, JsonObject body, bool authenticated, TimeSpan timeout)
    {
        using var response = Send(method, path, body, authenticated, timeout, out var cts);
        using (cts)
        {
            var bytes = ReadBounded(response, _options.MaxJsonBytes, cts.Token);
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ErpUnavailableException(path + ": answer is not a JSON object");
                return doc.RootElement.Clone();
            }
            catch (JsonException) { throw new ErpUnavailableException(path + ": answer is not JSON"); }
        }
    }

    /// <summary>Returns only for 2xx. 4xx → ErpException (with error_code when present); everything else → unavailable.</summary>
    private HttpResponseMessage Send(HttpMethod method, string path, JsonObject? body, bool authenticated, TimeSpan timeout, out CancellationTokenSource cts)
    {
        cts = new CancellationTokenSource(timeout);
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(method, ApiPath + path);
            if (body is not null)
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json)) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
            if (authenticated)
            {
                var credential = _credential() ?? throw new ErpException(401, "AGENT_UNAUTHENTICATED");
                request.Headers.TryAddWithoutValidation("Authorization", credential.ToAuthorizationHeader());
            }
            response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300) { LastThrottle = null; return response; }

            if (status == 429)
            {
                TimeSpan? retryAfter = response.Headers.RetryAfter switch
                {
                    { Delta: { } d } => d,
                    { Date: { } at } => at - DateTimeOffset.UtcNow,
                    _ => null,
                };
                LastThrottle = new ErpThrottle(retryAfter);
            }
            if (status is >= 400 and < 500)
            {
                var error = ErpErrorBody.Parse(ReadBounded(response, 64 * 1024, cts.Token));
                throw new ErpException(status, error.ErrorCode);
            }
            throw new ErpUnavailableException($"{path}: HTTP {status}"); // 3xx (not followed) and 5xx
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            response?.Dispose();
            cts.Dispose();
            throw new ErpUnavailableException($"{path}: {ex.GetType().Name}");
        }
        catch
        {
            response?.Dispose();
            cts.Dispose();
            throw;
        }
    }

    private static byte[] ReadBounded(HttpResponseMessage response, long max, CancellationToken ct)
    {
        try
        {
            if (response.Content.Headers.ContentLength is { } declared && declared > max) throw new ErpUnavailableException("answer larger than allowed");
            using var stream = response.Content.ReadAsStream(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > max) throw new ErpUnavailableException("answer larger than allowed");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            throw new ErpUnavailableException("answer body: " + ex.GetType().Name);
        }
    }

    private static JsonElement Req(JsonElement e, string name, JsonValueKind kind)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == kind) return v;
        throw new ErpUnavailableException("answer lacks a usable '" + name + "'");
    }

    private static string Str(JsonElement e, string name) => Req(e, name, JsonValueKind.String).GetString()!;
    private static JsonElement Obj(JsonElement e, string name) => Req(e, name, JsonValueKind.Object);
    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) => Req(e, name, JsonValueKind.Array).EnumerateArray();
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name, int? fallback = null)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        return fallback ?? throw new ErpUnavailableException("answer lacks a usable '" + name + "'");
    }

    private static long Long(JsonElement e, string name) =>
        Req(e, name, JsonValueKind.Number).TryGetInt64(out var l) ? l : throw new ErpUnavailableException("answer lacks a usable '" + name + "'");

    private static Guid Id(JsonElement e, string name) =>
        Guid.TryParseExact(Str(e, name), "D", out var g) ? g : throw new ErpUnavailableException("'" + name + "' is not a UUID");

    private static IReadOnlyList<Guid> Ids(JsonElement e, string name) =>
        [.. Arr(e, name).Select(x => x.ValueKind == JsonValueKind.String && Guid.TryParseExact(x.GetString(), "D", out var g) ? g : throw new ErpUnavailableException("'" + name + "' holds a non-UUID"))];

    private static DateTimeOffset Time(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t : throw new ErpUnavailableException("'" + name + "' is not a timestamp");

    private static string Sanitize(string? s) => new([.. (s ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').Take(40)]);
}
