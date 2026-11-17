using System.Globalization;
using LabelPrinter.Agent.Core;
using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Erp;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Journal;
using LabelPrinter.Agent.Core.Runtime;
using LabelPrinter.Agent.Core.Spooling;

namespace LabelPrinter.Agent;

internal enum WorkerState
{
    Stopped, NotPaired, NoPrinter, PrinterMissing, Starting, Running,
    CredentialRejected, ContractUnsupported, JournalLocked, JournalProblem, StorageFailure, Failed,
}

internal sealed record ActionResult(bool Ok, string Message);

/// <summary>
/// Owns the background worker (ledger + ERP loop) and every operator action. The UI and the console host both
/// go through it, so the safety rules live in one place:
/// the worker starts only when paired and a printer was selected explicitly; configuration changes stop the
/// worker first; nothing here can resubmit a job (cancel, hold and delete only ever lead to Cancelled/Unknown).
/// </summary>
internal sealed class AgentService(AgentHost host, IPlatform platform, FileLog log, TimeProvider clock) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Lock _settingsGate = new();
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private volatile JobLedger? _ledger;
    private volatile WorkerState _state = WorkerState.Stopped;
    private volatile string _message = "";
    private volatile bool _paused;
    private volatile bool _erpReachable = true;
    private DateTimeOffset? _lastCleanAt;

    public AgentHost Host => host;
    public IPlatform Platform => platform;
    public FileLog Log => log;
    public WorkerState State => _state;
    public string Message => _message;
    public bool Paused => _paused;
    public bool ErpReachable => _erpReachable;
    public DateTimeOffset? LastCleanAt => _lastCleanAt;
    public JobLedger? Ledger => _ledger;
    public bool WorkerAlive => _thread is { IsAlive: true };

    public AgentIdentity? Identity() => Directory.Exists(host.DataDir) ? host.Identities().Load() : null;
    public string? CredentialKeyId() => Directory.Exists(host.DataDir) ? host.Identities().Current()?.KeyId : null;
    public AgentSettings Settings() => Directory.Exists(host.DataDir) ? AgentSettings.Load(host.DataDir) : new AgentSettings();

    private WorkerState Set(WorkerState state, string message)
    {
        _message = message;
        _state = state;
        return state;
    }

    private void UpdateSettings(Func<AgentSettings, AgentSettings> change)
    {
        lock (_settingsGate)
        {
            host.EnsureDataDir();
            change(AgentSettings.Load(host.DataDir)).Save(host.DataDir);
        }
    }

    /// <summary>Starts the worker if everything it needs is in place. Idempotent.</summary>
    public WorkerState Start()
    {
        lock (_gate)
        {
            if (_thread is { IsAlive: true }) return _state;
            host.EnsureDataDir();
            var identities = host.Identities();
            var identity = identities.Load();
            if (identity is null || identities.Current() is null) return Set(WorkerState.NotPaired, "هنوز به سرور وصل نشده است.");
            var settings = AgentSettings.Load(host.DataDir);
            _paused = settings.Paused;
            if (settings.Printer is not { } printer) return Set(WorkerState.NoPrinter, "هنوز چاپگر انتخاب نشده است.");
            if (platform.RequiresExistingQueue && !platform.Queues().Contains(printer.Queue, StringComparer.OrdinalIgnoreCase))
                return Set(WorkerState.PrinterMissing, $"چاپگر «{printer.Queue}» روی این کامپیوتر پیدا نشد.");

            JobLedger ledger;
            try
            {
                ledger = JobLedger.Open(new FileJournalStore(host.JournalPath), new FileBlobStore(host.PayloadDir), identity.AgentId, identity.Origin, clock, Guid.NewGuid, out var recovery);
                log.Write($"journal opened: {recovery.RecordsReplayed} records, torn bytes {recovery.TornBytesDiscarded}, marked unknown {recovery.MarkedUnknown.Count}, resumable {recovery.Resumable.Count}");
            }
            catch (JournalLockedException) { return Set(WorkerState.JournalLocked, "نمونه‌ی دیگری از برنامه در حال اجراست."); }
            catch (JournalCorruptException ex)
            {
                log.Write("journal is corrupt and was not opened: " + ex.Message);
                return Set(WorkerState.JournalProblem, "پرونده‌ی ثبت کارها خراب است و برنامه چاپ نمی‌کند. راهنمای «recovery.md» را ببینید.");
            }
            catch (JournalMismatchException ex)
            {
                log.Write("journal refused: " + ex.Message);
                return Set(WorkerState.JournalProblem, "پرونده‌ی ثبت کارها متعلق به اتصال یا سرور دیگری است و استفاده نمی‌شود. راهنمای «recovery.md» را ببینید.");
            }

            var http = new HttpErpClient(identity.Origin, identities.Current, new HttpErpOptions { AgentVersion = AgentHost.Version, ClaimWaitSec = Math.Clamp(identity.ClaimWaitSec, 0, 15) });
            _ledger = ledger;
            _cts = new CancellationTokenSource();
            Set(WorkerState.Starting, "در حال اتصال…");
            var ct = _cts.Token;
            _thread = new Thread(() => Worker(ledger, http, identities, identity, printer, ct)) { IsBackground = true, Name = "agent-worker" };
            _thread.Start();
            return _state;
        }
    }

    /// <summary>Stops the worker and releases the journal. Waits for the current ERP call to finish.</summary>
    public void StopWorker()
    {
        Thread? thread;
        lock (_gate)
        {
            _cts?.Cancel();
            thread = _thread;
        }
        thread?.Join(TimeSpan.FromSeconds(40));
        lock (_gate)
        {
            if (_thread is { IsAlive: false }) { _thread = null; _cts?.Dispose(); _cts = null; }
            if (_state is WorkerState.Running or WorkerState.Starting) _state = WorkerState.Stopped;
        }
    }

    private void Worker(JobLedger ledger, HttpErpClient http, IdentityStore identities, AgentIdentity identity, PrinterSelection printer, CancellationToken ct)
    {
        try
        {
            var erpPrinterId = Guid.Empty;
            while (erpPrinterId == Guid.Empty)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var map = http.ReportPrinters([new PrinterReport(printer.LocalPrinterId, printer.Queue, printer.Queue, printer.Language, printer.Dpi, printer.WidthMm, printer.HeightMm)]);
                    erpPrinterId = map[printer.LocalPrinterId];
                }
                catch (ErpException ex) when (ex.Status == 401)
                {
                    Set(WorkerState.CredentialRejected, "سرور اجازه‌ی اتصال نمی‌دهد (دسترسی ابطال یا منقضی شده). دوباره وصل شوید.");
                    return;
                }
                catch (Exception ex) when (ex is ErpException or ErpUnavailableException or KeyNotFoundException)
                {
                    if (printer.ErpPrinterId is { } known) { erpPrinterId = known; log.Write("could not refresh the printer registration (" + ex.Message + "); continuing with the stored one"); break; }
                    _erpReachable = false;
                    Set(WorkerState.Starting, "در انتظار اتصال به سرور…");
                    if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(15))) return;
                }
            }

            AgentSettings settings = default!;
            UpdateSettings(s => settings = s with { Printer = (s.Printer ?? printer) with { ErpPrinterId = erpPrinterId } });

            var spooler = platform.CreateSpooler(printer, erpPrinterId, log.Write);
            var sync = new ErpSync(ledger, http, spooler, clock) { Admit = settings.Refuse, Hold = () => _paused };
            var runner = new AgentRunner(ledger, http, sync, identities, identity, clock, log.Write);
            if (ledger.QuarantineReason is { } reason) log.Write($"QUARANTINED ({reason}): nothing will be submitted until the quarantine is cleared");
            log.Write($"agent {AgentHost.Version} running: origin {identity.Origin}, agent {identity.AgentId}, printer '{printer.Queue}' {printer.Language} {printer.Dpi}dpi");
            Set(WorkerState.Running, "در حال کار");

            while (!ct.IsCancellationRequested)
            {
                platform.WatchSpooler(ledger, spooler, printer.Queue, log.Write);
                var stop = runner.RunOnce();
                _erpReachable = runner.ErpReachable;
                _lastCleanAt = runner.LastCleanAt;
                if (stop is { } why)
                {
                    log.Write("stopped: " + why);
                    if (why == RunnerStop.CredentialRejected) Set(WorkerState.CredentialRejected, "سرور اجازه‌ی اتصال نمی‌دهد (دسترسی ابطال یا منقضی شده). دوباره وصل شوید.");
                    else Set(WorkerState.ContractUnsupported, "نسخه‌ی این برنامه با سرور سازگار نیست؛ برنامه را به‌روز کنید.");
                    return;
                }
                if (ct.WaitHandle.WaitOne(runner.LastDelay)) break;
            }
            log.Write("stopped on request");
        }
        catch (Exception ex) when (ex is JournalWriteException or LedgerFaultedException)
        {
            log.Write("storage failure, stopping: " + ex.GetType().Name + " " + ex.Message);
            Set(WorkerState.StorageFailure, "نوشتن روی دیسک ناموفق بود؛ برنامه برای ایمنی ایستاد. فضای دیسک را بررسی و برنامه را دوباره باز کنید.");
        }
        catch (Exception ex)
        {
            log.Write("unexpected failure: " + ex.GetType().Name);
            Set(WorkerState.Failed, "خطای پیش‌بینی‌نشده؛ برنامه ایستاد. برنامه را دوباره باز کنید.");
        }
        finally
        {
            http.Dispose();
            _ledger = null;
            ledger.Dispose();
            if (_state is WorkerState.Running or WorkerState.Starting) _state = WorkerState.Stopped;
        }
    }

    // ---- operator actions ----

    public ActionResult Pair(string origin, string code, string name)
    {
        string normalized;
        try { normalized = HttpErpClient.OriginKey(origin); }
        catch (ArgumentException) { return new(false, "آدرس سرور باید با https:// شروع شود، مثل https://erp.example.com"); }
        code = (code ?? "").Trim();
        if (code.Length is < 6 or > 20) return new(false, "کد اتصال درست به نظر نمی‌رسد.");
        name = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
        if (name.Length > 120) name = name[..120];

        StopWorker();
        host.EnsureDataDir();
        var identities = host.Identities();
        if (identities.Load() is { } existing)
            return new(false, string.Equals(existing.Origin, normalized, StringComparison.Ordinal)
                ? "قبلاً وصل شده‌اید. برای اتصال دوباره، اول «قطع اتصال» را بزنید."
                : $"این برنامه به {existing.Origin} وصل است. برای سرور دیگر باید اول قطع اتصال و پوشه‌ی داده جابه‌جا شود (راهنمای recovery.md).");

        using var http = new HttpErpClient(normalized, () => null, new HttpErpOptions { AgentVersion = AgentHost.Version });
        PairResult result;
        try { result = http.Pair(code, identities.InstallId(), name, OperatingSystem.IsWindows() ? "windows" : "other", Environment.OSVersion.Version.ToString()); }
        catch (ErpException ex)
        {
            Start();
            return new(false, ex.Status == 400 ? "کد اتصال نامعتبر یا منقضی است. کدها یک‌بار مصرف‌اند و ۱۰ دقیقه اعتبار دارند."
                : ex.Status == 426 ? "نسخه‌ی این برنامه با سرور سازگار نیست."
                : ex.Status == 429 ? "تعداد تلاش‌ها زیاد بود؛ کمی صبر کنید."
                : $"سرور اتصال را نپذیرفت ({ex.Status}).");
        }
        catch (ErpUnavailableException)
        {
            return new(false, "به سرور دسترسی پیدا نشد. اینترنت و آدرس را بررسی کنید. اگر کد مصرف شده بود، کد تازه بسازید.");
        }

        var now = clock.GetUtcNow();
        identities.SavePairing(new AgentIdentity(normalized, result.AgentId, identities.InstallId(), name, result.NegotiatedContract, now, now, result.HeartbeatSec, result.ClaimWaitSec), result.Credential);
        log.Write($"paired with {normalized} as agent {result.AgentId}");
        Start();
        return new(true, "اتصال برقرار شد.");
    }

    public ActionResult Unpair()
    {
        StopWorker();
        if (Directory.Exists(host.DataDir)) host.Identities().Unpair();
        Set(WorkerState.NotPaired, "اتصال قطع شد.");
        log.Write("unpaired");
        return new(true, "اتصال از این کامپیوتر قطع شد. برای باطل شدن کامل، دسترسی را در ERP هم لغو کنید. پرونده‌ی ثبت کارها نگه داشته شد.");
    }

    public ActionResult SelectPrinter(string queue, string language, int dpi, double widthMm, double heightMm)
    {
        language = (language ?? "").ToLowerInvariant();
        if (language is not ("tspl" or "zpl")) return new(false, "زبان چاپگر باید TSPL یا ZPL باشد.");
        if (dpi is not (203 or 300 or 600)) return new(false, "DPI باید ۲۰۳، ۳۰۰ یا ۶۰۰ باشد.");
        if (widthMm is < 10 or > 200 || heightMm is < 5 or > 300 || double.IsNaN(widthMm) || double.IsNaN(heightMm)) return new(false, "اندازه‌ی لیبل خارج از محدوده است (عرض ۱۰ تا ۲۰۰، ارتفاع ۵ تا ۳۰۰ میلی‌متر).");
        if (platform.RequiresExistingQueue && !platform.Queues().Contains(queue, StringComparer.OrdinalIgnoreCase)) return new(false, "چاپگری با این نام پیدا نشد.");

        StopWorker();
        UpdateSettings(s =>
        {
            var old = s.Printer;
            var id = old is not null && string.Equals(old.Queue, queue, StringComparison.OrdinalIgnoreCase) ? old.LocalPrinterId : Guid.NewGuid();
            return s with { Printer = new PrinterSelection(id, queue, language, dpi, widthMm, heightMm) };
        });
        log.Write($"printer selected: '{queue}' {language} {dpi}dpi");
        Start();
        return new(true, "چاپگر ذخیره شد. زبان و DPI را شما اعلام کرده‌اید؛ برنامه نمی‌تواند درستی آن را بررسی کند.");
    }

    public ActionResult SetPaused(bool paused)
    {
        UpdateSettings(s => s with { Paused = paused });
        _paused = paused;
        log.Write(paused ? "paused by operator" : "resumed by operator");
        return new(true, paused ? "ارسال کارهای جدید متوقف شد." : "ارسال از سر گرفته شد.");
    }

    public ActionResult CancelJob(Guid jobId)
    {
        var ledger = _ledger;
        if (ledger is null) return new(false, "برنامه در حال کار نیست.");
        try
        {
            if (ledger.Find(jobId) is null) return new(false, "کار پیدا نشد.");
            return ledger.Cancel(jobId)
                ? new(true, "کار لغو شد و چیزی برای چاپ فرستاده نشد.")
                : new(false, "این کار دیگر قابل لغو نیست (به صف چاپ رفته یا تمام شده).");
        }
        catch (ObjectDisposedException) { return new(false, "برنامه در حال توقف است."); }
    }

    public ActionResult DeleteFromSpooler(Guid jobId)
    {
        var ledger = _ledger;
        var printer = Settings().Printer;
        if (ledger is null || printer is null) return new(false, "برنامه در حال کار نیست.");
        try
        {
            var job = ledger.Find(jobId);
            if (job is null) return new(false, "کار پیدا نشد.");
            if (job.State != JobState.SubmittedToSpooler || job.SpoolerJobRef is null) return new(false, "فقط کاری که به صف چاپ ویندوز رفته و هنوز تمام نشده قابل حذف از صف است.");
            if (!platform.DeleteSpoolerJob(printer.Queue, job.SpoolerJobRef)) return new(false, "ویندوز حذف را نپذیرفت (شاید کار پیش‌تر از صف رفته).");
            ledger.MarkUnknown(jobId, "OPERATOR_DELETED_FROM_SPOOLER");
            log.Write($"job {jobId}: deleted from the Windows queue by the operator");
            return new(true, "از صف ویندوز حذف شد. چون معلوم نیست چیزی چاپ شده یا نه، وضعیت «نامعلوم» ثبت شد و دوباره ارسال نمی‌شود.");
        }
        catch (ObjectDisposedException) { return new(false, "برنامه در حال توقف است."); }
    }

    public ActionResult ClearQuarantine()
    {
        var ledger = _ledger;
        if (ledger is null) return new(false, "برنامه در حال کار نیست.");
        if (ledger.QuarantineReason is null) return new(true, "قرنطینه‌ای وجود ندارد.");
        var given = ledger.ClearQuarantine();
        log.Write($"quarantine cleared by the operator; {given.Count} accepted job(s) marked failed without printing");
        return new(true, $"قرنطینه برداشته شد. {given.Count} کار پذیرفته‌شده بدون چاپ ناموفق ثبت شد.");
    }

    public ActionResult SetAutostart(bool enabled)
    {
        try { platform.SetAutostart(enabled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(false, "تنظیم اجرا با ویندوز انجام نشد."); }
        return new(true, enabled ? "از این به بعد با ورود به ویندوز بدون پنجره اجرا می‌شود." : "اجرا با ویندوز خاموش شد.");
    }

    public void Dispose() => StopWorker();
}
