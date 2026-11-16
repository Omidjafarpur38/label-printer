using System.Reflection;
using System.Runtime.InteropServices;
using LabelPrinter.Agent.Core;
using LabelPrinter.Agent.Core.Erp;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Journal;
using LabelPrinter.Agent.Core.Runtime;
using LabelPrinter.Agent.Core.Spooling;
using LabelPrinter.Agent.Windows;

namespace LabelPrinter.Agent;

/// <summary>Size-capped text log. Callers pass codes and ids only; no credential or payload ever reaches it.</summary>
internal sealed class FileLog(string directory, bool echo)
{
    private const long MaxBytes = 1024 * 1024;
    private readonly Lock _gate = new();
    private string LogPath => Path.Combine(directory, "agent.log");

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'} {message}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes) File.Move(LogPath, LogPath + ".1", overwrite: true);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* logging must never stop the agent */ }
        }
        if (echo) Console.WriteLine(line);
    }
}

/// <summary>Dev/test stand-ins used only off Windows and only when LPAGENT_DEV_INSECURE=1.</summary>
internal sealed class DevPlainProtector : ISecretProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plain) => plain.ToArray();
    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => protectedBytes.ToArray();
}

internal sealed class DevDiscardSpooler(Action<string> log) : ISpooler
{
    public SpoolResult Submit(SpoolRequest request)
    {
        log($"DEV spooler: discarded {request.Payload.Length} bytes for job {request.JobId}");
        return new SpoolResult(SpoolOutcome.Accepted, "dev-discard");
    }
}

internal sealed class AgentHost(string dataDir)
{
    public const string ProductName = "LabelPrinterAgent";
    public string DataDir { get; } = dataDir;
    public string JournalPath => Path.Combine(DataDir, "journal.lpj");
    public string PayloadDir => Path.Combine(DataDir, "payloads");

    public static string Version =>
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static bool DevInsecure => !OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("LPAGENT_DEV_INSECURE") == "1";

    public static string DefaultDataDir() =>
        Environment.GetEnvironmentVariable("LPAGENT_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    public void EnsureDataDir()
    {
        var created = !Directory.Exists(DataDir);
        Directory.CreateDirectory(DataDir);
        if (created && OperatingSystem.IsWindows()) Acl.RestrictDirectoryToCurrentUser(DataDir);
        else if (created) File.SetUnixFileMode(DataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public IdentityStore Identities()
    {
        if (OperatingSystem.IsWindows()) return new IdentityStore(DataDir, new DpapiProtector(), Acl.RestrictToCurrentUser);
        if (!DevInsecure) throw new PlatformNotSupportedException("Credential storage is implemented for Windows only.");
        return new IdentityStore(DataDir, new DevPlainProtector(), p => { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite); });
    }

    public static IReadOnlyList<string> Queues() => OperatingSystem.IsWindows() ? Winspool.PrinterNames() : [];

    private static string StopEventName(string dataDir) =>
        "Local\\LabelPrinterAgent.Stop." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).ToUpperInvariant())))[..16];

    /// <summary>Asks a running agent that uses this data directory to stop. Returns false if none was found.</summary>
    public bool SignalStop()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!EventWaitHandle.TryOpenExisting(StopEventName(DataDir), out var handle)) return false;
        using (handle) handle.Set();
        return true;
    }

    /// <summary>True if another process holds the journal (the single-owner lock).</summary>
    public bool IsRunning()
    {
        if (!File.Exists(JournalPath)) return false;
        try { using var probe = new FileStream(JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    /// <summary>
    /// The agent loop. Refuses to start (and so never claims or submits) unless the agent is paired and a
    /// printer has been selected explicitly and exists. Returns a process exit code.
    /// </summary>
    public int Run(bool echo, CancellationToken cancel)
    {
        EnsureDataDir();
        var log = new FileLog(DataDir, echo);
        var identities = Identities();
        var identity = identities.Load();
        if (identity is null || identities.Current() is null) { log.Write("not paired; run 'pair' first"); return 3; }

        var settings = AgentSettings.Load(DataDir);
        if (settings.Printer is not { } printer) { log.Write("no printer selected; run 'select-printer' first"); return 4; }
        if (OperatingSystem.IsWindows() && !Winspool.PrinterNames().Contains(printer.Queue, StringComparer.OrdinalIgnoreCase))
        {
            log.Write($"selected printer queue '{printer.Queue}' does not exist; not starting");
            return 4;
        }
        if (!OperatingSystem.IsWindows() && !DevInsecure) { log.Write("printing is implemented for Windows only"); return 5; }

        JobLedger ledger;
        try
        {
            ledger = JobLedger.Open(new FileJournalStore(JournalPath), new FileBlobStore(PayloadDir), identity.AgentId, identity.Origin, TimeProvider.System, Guid.NewGuid, out var recovery);
            log.Write($"journal opened: {recovery.RecordsReplayed} records, torn bytes {recovery.TornBytesDiscarded}, marked unknown {recovery.MarkedUnknown.Count}, resumable {recovery.Resumable.Count}");
        }
        catch (JournalLockedException) { log.Write("another agent instance owns this journal; not starting"); return 6; }
        catch (JournalCorruptException ex) { log.Write("journal is corrupt and was not opened: " + ex.Message + " — see docs/recovery.md"); return 7; }
        catch (JournalMismatchException ex) { log.Write("journal refused: " + ex.Message + " — see docs/recovery.md"); return 7; }

        using (ledger)
        using (var http = new HttpErpClient(identity.Origin, identities.Current, new HttpErpOptions { AgentVersion = Version, ClaimWaitSec = Math.Clamp(identity.ClaimWaitSec, 0, 15) }))
        using (var stop = OperatingSystem.IsWindows() ? new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName(DataDir)) : new ManualResetEvent(false))
        {
            if (ledger.QuarantineReason is { } reason) log.Write($"QUARANTINED ({reason}): nothing will be submitted until 'recover --clear-quarantine'");

            // Tell the ERP which printer this agent was explicitly given, and learn the ERP's id for it.
            Guid erpPrinterId;
            try
            {
                var map = http.ReportPrinters([new PrinterReport(printer.LocalPrinterId, printer.Queue, printer.Queue, printer.Language, printer.Dpi, printer.WidthMm, printer.HeightMm)]);
                erpPrinterId = map[printer.LocalPrinterId];
                settings = settings with { Printer = printer with { ErpPrinterId = erpPrinterId } };
                settings.Save(DataDir);
            }
            catch (Exception ex) when (ex is ErpException or ErpUnavailableException or KeyNotFoundException)
            {
                if (printer.ErpPrinterId is not { } known) { log.Write("could not register the printer with the ERP (" + ex.Message + "); not starting"); return 8; }
                erpPrinterId = known;
                log.Write("could not refresh the printer registration (" + ex.Message + "); continuing with the stored one");
            }

            WindowsRawSpooler? windowsSpooler = null;
            ISpooler spooler;
            if (OperatingSystem.IsWindows()) spooler = windowsSpooler = new WindowsRawSpooler(printer.Queue, erpPrinterId, log.Write);
            else spooler = new DevDiscardSpooler(log.Write);

            var sync = new ErpSync(ledger, http, spooler, TimeProvider.System) { Admit = settings.Refuse };
            var runner = new AgentRunner(ledger, http, sync, identities, identity, TimeProvider.System, log.Write);
            log.Write($"agent {Version} running: origin {identity.Origin}, agent {identity.AgentId}, printer '{printer.Queue}' {printer.Language} {printer.Dpi}dpi");

            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    if (OperatingSystem.IsWindows() && windowsSpooler is not null) WatchSpoolerJobs(ledger, windowsSpooler, printer.Queue, log);
                    if (runner.RunOnce() is { } why)
                    {
                        log.Write("stopped: " + why);
                        return why == RunnerStop.CredentialRejected ? 9 : 10;
                    }
                    if (WaitHandle.WaitAny([cancel.WaitHandle, stop], runner.LastDelay) != WaitHandle.WaitTimeout) break;
                }
                log.Write("stopped on request");
                return 0;
            }
            catch (Exception ex) when (ex is JournalWriteException or LedgerFaultedException)
            {
                // Storage cannot be trusted right now. Stop; the next start recovers from what is really on disk.
                log.Write("storage failure, stopping: " + ex.GetType().Name + " " + ex.Message);
                return 11;
            }
        }
    }

    /// <summary>
    /// Reports completion only when the Windows spooler itself flags the job as printed/complete. A job that
    /// simply disappears from the queue proves nothing and is left as submitted_to_spooler.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WatchSpoolerJobs(JobLedger ledger, WindowsRawSpooler spooler, string queue, FileLog log)
    {
        foreach (var (jobId, spoolJob) in spooler.Submitted.ToList())
        {
            if (ledger.Find(jobId) is not { State: JobState.SubmittedToSpooler }) { spooler.Submitted.Remove(jobId); continue; }
            var status = Winspool.JobStatus(queue, spoolJob);
            if (status is null) { spooler.Submitted.Remove(jobId); continue; }
            if ((status & (Winspool.JOB_STATUS_PRINTED | Winspool.JOB_STATUS_COMPLETE)) != 0)
            {
                ledger.ConfirmCompleted(jobId, new Evidence(Evidence.SpoolerReportedComplete, "windows job status 0x" + status.Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture)));
                spooler.Submitted.Remove(jobId);
                log.Write($"job {jobId}: spooler reports complete");
            }
            else if ((status & (Winspool.JOB_STATUS_DELETING | Winspool.JOB_STATUS_DELETED)) != 0)
            {
                ledger.MarkUnknown(jobId, "SPOOLER_JOB_DELETED");
                spooler.Submitted.Remove(jobId);
                log.Write($"job {jobId}: deleted from the spooler queue; outcome unknown");
            }
        }
    }

    public static string OsDescription() => RuntimeInformation.OSDescription;
}
