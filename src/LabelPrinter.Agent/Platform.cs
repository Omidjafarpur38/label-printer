using System.Diagnostics;
using System.Runtime.Versioning;
using LabelPrinter.Agent.Core;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Runtime;
using LabelPrinter.Agent.Core.Spooling;
using LabelPrinter.Agent.Windows;
using Microsoft.Win32;

namespace LabelPrinter.Agent;

/// <summary>Everything that touches the operating system, so that the service and the UI can be tested without it.</summary>
internal interface IPlatform
{
    /// <summary>False only for the development stand-in, which has no real queues to check.</summary>
    bool RequiresExistingQueue { get; }
    IReadOnlyList<string> Queues();
    ISpooler CreateSpooler(PrinterSelection printer, Guid erpPrinterId, Action<string> log);
    /// <summary>Called between cycles; the Windows implementation reports completion only when the spooler itself says so.</summary>
    void WatchSpooler(JobLedger ledger, ISpooler spooler, string queue, Action<string> log);
    /// <summary>Best-effort delete of a job from the OS print queue.</summary>
    bool DeleteSpoolerJob(string queue, string spoolerRef);
    bool AutostartEnabled { get; }
    void SetAutostart(bool enabled);
    void OpenWindow(string url, string profileDir);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsPlatform : IPlatform
{
    public bool RequiresExistingQueue => true;
    public IReadOnlyList<string> Queues() => Winspool.PrinterNames();

    public ISpooler CreateSpooler(PrinterSelection printer, Guid erpPrinterId, Action<string> log) =>
        new WindowsRawSpooler(printer.Queue, erpPrinterId, log);

    public void WatchSpooler(JobLedger ledger, ISpooler spooler, string queue, Action<string> log)
    {
        if (spooler is not WindowsRawSpooler raw) return;
        foreach (var (jobId, spoolJob) in raw.Submitted.ToList())
        {
            if (ledger.Find(jobId) is not { State: JobState.SubmittedToSpooler }) { raw.Submitted.Remove(jobId); continue; }
            var status = Winspool.JobStatus(queue, spoolJob);
            if (status is null) { raw.Submitted.Remove(jobId); continue; } // gone from the queue: proves nothing
            if ((status & (Winspool.JOB_STATUS_PRINTED | Winspool.JOB_STATUS_COMPLETE)) != 0)
            {
                ledger.ConfirmCompleted(jobId, new Evidence(Evidence.SpoolerReportedComplete, "windows job status 0x" + status.Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture)));
                raw.Submitted.Remove(jobId);
                log($"job {jobId}: spooler reports complete");
            }
            else if ((status & (Winspool.JOB_STATUS_DELETING | Winspool.JOB_STATUS_DELETED)) != 0)
            {
                ledger.MarkUnknown(jobId, "SPOOLER_JOB_DELETED");
                raw.Submitted.Remove(jobId);
                log($"job {jobId}: deleted from the spooler queue; outcome unknown");
            }
        }
    }

    public bool DeleteSpoolerJob(string queue, string spoolerRef) =>
        int.TryParse(spoolerRef, out var id) && Winspool.DeleteJob(queue, id);

    public bool AutostartEnabled => Autostart.Current() is not null;

    public void SetAutostart(bool enabled)
    {
        if (!enabled) { Autostart.Disable(); return; }
        var exe = Path.Combine(AppContext.BaseDirectory, "LabelPrinterAgentBg.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("background program not found", exe);
        Autostart.Enable(exe);
    }

    /// <summary>
    /// Opens the page in an app-mode browser window (no address bar, own taskbar entry, own profile), preferring
    /// Microsoft Edge, which ships with Windows 10/11. Falls back to Chrome, then to the default browser.
    /// </summary>
    public void OpenWindow(string url, string profileDir)
    {
        var browser = FindBrowser();
        if (browser is null)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }
        var psi = new ProcessStartInfo(browser) { UseShellExecute = false };
        psi.ArgumentList.Add("--app=" + url);
        psi.ArgumentList.Add("--user-data-dir=" + profileDir);
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--window-size=1060,780");
        Process.Start(psi);
    }

    private static string? FindBrowser()
    {
        foreach (var exe in new[] { "msedge.exe", "chrome.exe" })
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (key?.GetValue(null) is string path && File.Exists(path)) return path;
            }
        }
        foreach (var path in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
        })
            if (File.Exists(path)) return path;
        return null;
    }
}

/// <summary>Stand-in for development off Windows (LPAGENT_DEV_INSECURE=1): fake queue, discarding spooler.</summary>
internal sealed class DevPlatform : IPlatform
{
    private bool _autostart;
    public bool RequiresExistingQueue => false;
    public IReadOnlyList<string> Queues() => ["Dev Queue"];
    public ISpooler CreateSpooler(PrinterSelection printer, Guid erpPrinterId, Action<string> log) => new DevDiscardSpooler(log);
    public void WatchSpooler(JobLedger ledger, ISpooler spooler, string queue, Action<string> log) { }
    public bool DeleteSpoolerJob(string queue, string spoolerRef) => false;
    public bool AutostartEnabled => _autostart;
    public void SetAutostart(bool enabled) => _autostart = enabled;
    public void OpenWindow(string url, string profileDir) => Console.WriteLine("open " + url);
}
