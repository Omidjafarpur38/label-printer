using System.Net;
using System.Security.Cryptography;
using LabelPrinter.Agent.Core.Updates;

namespace LabelPrinter.Agent;

internal sealed record UpdateOptions(Uri BaseUri, IReadOnlyList<byte[]> Keys, string CurrentVersion, TimeSpan FirstCheckDelay, TimeSpan CheckInterval)
{
    public static UpdateOptions Production() => new(new Uri(Redactor.DefaultOrigin), TrustedKeys.Parsed(), AgentHost.Version, TimeSpan.FromSeconds(45), TimeSpan.FromHours(6));
}

internal enum UpdateStatus { Disabled, Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

/// <summary>
/// Looks for a newer version on its own, tells the user, and installs only after the user presses the button.
/// Safety rules (docs/auto-update.md): the manifest is accepted only with a valid signature from a built-in key; the
/// installer only if its size and SHA-256 equal the signed values; only strictly newer versions; HTTPS only (loopback
/// HTTP for tests), no redirects, no cookies, size caps, nothing is downloaded until the user asks, and the installer
/// is started by this program only after verification. The installer itself stops the agent through the same
/// graceful path as any upgrade, so no submission is cut.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private const string Dir = "/downloads/";
    private readonly AgentHost _host;
    private readonly IPlatform _platform;
    private readonly FileLog _log;
    private readonly UpdateOptions _options;
    private readonly HttpClient _http;
    private readonly Lock _gate = new();
    private Timer? _timer;
    private UpdateStatus _status;
    private UpdateOffer? _offer;
    private string _message = "";
    private DateTimeOffset? _checkedAt;

    public UpdateService(AgentHost host, IPlatform platform, FileLog log, UpdateOptions options, HttpMessageHandler? handler = null)
    {
        _host = host; _platform = platform; _log = log; _options = options;
        var loopback = options.BaseUri.IsLoopback;
        if (options.BaseUri.Scheme != Uri.UriSchemeHttps && !(loopback && options.BaseUri.Scheme == Uri.UriSchemeHttp))
            throw new ArgumentException("update source must be https", nameof(options));
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan; // every call carries its own cancellation
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LabelPrinterAgent/" + options.CurrentVersion);
        _status = UpdateStatus.Idle;
        if (options.Keys.Count == 0) { _status = UpdateStatus.Disabled; _message = "به‌روزرسانی خودکار هنوز فعال نشده است."; }
    }

    public UpdateStatus Status { get { lock (_gate) return _status; } }
    public UpdateOffer? Offer { get { lock (_gate) return _offer; } }
    public string Message { get { lock (_gate) return _message; } }
    public DateTimeOffset? CheckedAt { get { lock (_gate) return _checkedAt; } }
    private string UpdateDir => Path.Combine(_host.DataDir, "update");

    public void Start()
    {
        lock (_gate)
        {
            if (_status == UpdateStatus.Disabled || _timer is not null) return;
            CleanOldFiles();
            var jitter = TimeSpan.FromSeconds(Random.Shared.NextDouble() * _options.CheckInterval.TotalSeconds * 0.1);
            _timer = new Timer(_ => { try { CheckNow(); } catch (Exception ex) { _log.Write("update check crashed: " + ex.GetType().Name); } }, null, _options.FirstCheckDelay, _options.CheckInterval + jitter);
        }
    }

    private void CleanOldFiles()
    {
        try { if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* leftovers are harmless; they are never run */ }
    }

    private void Set(UpdateStatus status, string message)
    {
        lock (_gate) { _status = status; _message = message; }
    }

    /// <summary>Asks the server whether a newer, correctly signed version exists. Never downloads the installer.</summary>
    public ActionResult CheckNow()
    {
        lock (_gate)
        {
            if (_status == UpdateStatus.Disabled) return new(false, _message);
            if (_status is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.Installing) return new(false, "یک کار به‌روزرسانی در حال انجام است.");
            _status = UpdateStatus.Checking; _message = "در حال بررسی…";
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var manifest = Fetch("agent-update.json", UpdateManifest.MaxManifestBytes, cts.Token);
            var sigText = Fetch("agent-update.sig", UpdateManifest.MaxSignatureTextBytes, cts.Token);
            byte[] sig;
            try { sig = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(sigText).Trim()); }
            catch (FormatException) { throw new UpdateRejectedException("SIGNATURE_FORMAT", "signature is not base64"); }
            var offer = UpdateManifest.Verify(manifest, sig, _options.Keys, _options.CurrentVersion);
            lock (_gate) { _offer = offer; _checkedAt = DateTimeOffset.UtcNow; _status = UpdateStatus.Available; _message = "نسخه‌ی تازه آماده‌ی نصب است."; }
            _log.Write("update available: " + offer.Version);
            return new(true, _message);
        }
        catch (UpdateRejectedException ex) when (ex.Code == "NOT_NEWER")
        {
            lock (_gate) { _offer = null; _checkedAt = DateTimeOffset.UtcNow; _status = UpdateStatus.UpToDate; _message = "برنامه به‌روز است."; }
            return new(true, _message);
        }
        catch (UpdateRejectedException ex)
        {
            _log.Write("update offer refused: " + ex.Code);
            lock (_gate) { _offer = null; _checkedAt = DateTimeOffset.UtcNow; }
            Set(UpdateStatus.Failed, "اعلان به‌روزرسانی معتبر نبود و نادیده گرفته شد.");
            return new(false, Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            lock (_gate) _checkedAt = DateTimeOffset.UtcNow;
            Set(UpdateStatus.Failed, "ارتباط برای بررسی به‌روزرسانی برقرار نشد؛ بعداً خودکار دوباره تلاش می‌شود.");
            return new(false, Message);
        }
    }

    /// <summary>Called only from the user's confirmation. Downloads, verifies and starts the installer.</summary>
    public ActionResult Install()
    {
        UpdateOffer offer;
        lock (_gate)
        {
            if (_status != UpdateStatus.Available || _offer is null) return new(false, "نسخه‌ی تازه‌ای برای نصب آماده نیست. ابتدا «بررسی» را بزنید.");
            offer = _offer; _status = UpdateStatus.Downloading; _message = "در حال دریافت نسخه‌ی تازه…";
        }
        string path;
        try
        {
            path = Download(offer);
        }
        catch (UpdateRejectedException ex)
        {
            _log.Write("update download refused: " + ex.Code);
            Set(UpdateStatus.Failed, "فایل دریافت‌شده با اعلان امضاشده نمی‌خواند و نصب نشد.");
            return new(false, Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            _log.Write("update download failed: " + ex.GetType().Name);
            Set(UpdateStatus.Failed, "دریافت نسخه‌ی تازه کامل نشد. دوباره تلاش کنید.");
            return new(false, Message);
        }

        Set(UpdateStatus.Installing, "نصب شروع شد؛ برنامه چند ثانیه دیگر دوباره باز می‌شود.");
        _log.Write("starting installer for " + offer.Version);
        if (!_platform.LaunchInstaller(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /UPDATE=1"))
        {
            Set(UpdateStatus.Failed, "اجرای نصب‌کننده ممکن نشد.");
            return new(false, Message);
        }
        return new(true, Message);
    }

    private byte[] Fetch(string name, int maxBytes, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUri, Dir + name));
        using var res = _http.Send(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("status " + (int)res.StatusCode);
        if (res.Content.Headers.ContentLength is { } len && len > maxBytes) throw new UpdateRejectedException("TOO_LARGE", name);
        using var s = res.Content.ReadAsStream(ct);
        using var ms = new MemoryStream();
        var buf = new byte[4096]; int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > maxBytes) throw new UpdateRejectedException("TOO_LARGE", name);
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private string Download(UpdateOffer offer)
    {
        Directory.CreateDirectory(UpdateDir);
        var final = Path.Combine(UpdateDir, offer.File); // name was validated against the version by Verify
        var part = final + ".part";
        try { File.Delete(part); File.Delete(final); } catch (IOException) { }
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUri, Dir + offer.File));
        using var res = _http.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("status " + (int)res.StatusCode);
        if (res.Content.Headers.ContentLength is { } len && len != offer.SizeBytes) throw new UpdateRejectedException("SIZE_MISMATCH", "content-length");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        try
        {
            using (var s = res.Content.ReadAsStream(cts.Token))
            using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920]; int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > offer.SizeBytes) throw new UpdateRejectedException("SIZE_MISMATCH", "too many bytes");
                    hash.AppendData(buf, 0, n);
                    fs.Write(buf, 0, n);
                }
                fs.Flush(true);
            }
            if (total != offer.SizeBytes) throw new UpdateRejectedException("SIZE_MISMATCH", "too few bytes");
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), offer.Sha256, StringComparison.OrdinalIgnoreCase)) throw new UpdateRejectedException("HASH_MISMATCH", "sha256");
            File.Move(part, final);
            return final;
        }
        catch
        {
            try { File.Delete(part); } catch (IOException) { }
            throw;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _http.Dispose();
    }
}
