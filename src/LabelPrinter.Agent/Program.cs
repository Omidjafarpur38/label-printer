using System.Globalization;
using System.Text;
using LabelPrinter.Agent.Core;
using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Erp;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Journal;
using LabelPrinter.Agent.Core.Runtime;
using LabelPrinter.Agent.Windows;

namespace LabelPrinter.Agent;

internal static class Program
{
#if BACKGROUND
    // Windowless build (LabelPrinterAgentBg.exe): the agent plus its local control panel. Started at logon with
    // --background (no window) or from the Start-menu icon (opens the window).
    private static int Main(string[] args) => UiHost.Run(args);
#else
    private const string Help = """
        LabelPrinterAgent — experimental label print agent (unsigned prerelease)

          version                         show the version
          status                          pairing, printer, journal and autostart state
          printers                        list Windows print queues visible to this user
          select-printer --queue NAME --language tspl|zpl --dpi N --width-mm W --height-mm H
                                          choose the one printer this agent may use (never the default implicitly)
          pair --origin https://HOST [--name NAME] [--code-file PATH]
                                          pair with an ERP; the pairing code is asked for, never passed as an argument
          unpair                          delete the stored credential (journal and settings are kept)
          gui                             start the agent with its control window (same as the Start-menu icon)
          run                             run in the foreground until Ctrl+C (no window)
          stop [--wait SECONDS]           ask a running agent to stop (and wait until it has)
          selftest                        local checks only: credential protection, file locking; no ERP, no printer
          autostart enable|disable|status start in the background at logon for this user (off by default)
          recover --clear-quarantine --yes
                                          operator recovery after a suspected journal rollback (see docs/recovery.md)
          spool-file --queue NAME --file PATH --yes
                                          diagnostics: send one file once, RAW, to the named queue

        Data directory: %LOCALAPPDATA%\LabelPrinterAgent (override: LPAGENT_DATA_DIR)
        """;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        var host = new AgentHost(AgentHost.DefaultDataDir());
        try
        {
            return command switch
            {
                "version" or "--version" => Print(AgentHost.Version),
                "help" or "--help" or "-h" or "/?" => Print(Help),
                "status" => Status(host),
                "printers" => Printers(),
                "select-printer" => SelectPrinter(host, args),
                "pair" => Pair(host, args),
                "unpair" => Unpair(host),
                "run" => RunForeground(host),
                "gui" => UiHost.Run(args),
                "stop" => Stop(host, args),
                "selftest" => SelfTest(),
                "autostart" => AutostartCommand(args),
                "recover" => Recover(host, args),
                "spool-file" => SpoolFile(args),
                _ => Fail(2, "unknown command '" + command + "'. Try: help"),
            };
        }
        catch (ArgumentException ex) { return Fail(2, ex.Message); }
        catch (PlatformNotSupportedException ex) { return Fail(5, ex.Message); }
        catch (JournalLockedException) { return Fail(6, "the agent is running and owns the journal; use 'stop' first"); }
        catch (JournalCorruptException ex) { return Fail(7, "journal is corrupt: " + ex.Message + " (see docs/recovery.md)"); }
        catch (JournalMismatchException ex) { return Fail(7, ex.Message + " (see docs/recovery.md)"); }
    }

    private static int Print(string text) { Console.WriteLine(text); return 0; }
    private static int Fail(int code, string text) { Console.Error.WriteLine("error: " + text); return code; }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Required(string[] args, string name) => Option(args, name) ?? throw new ArgumentException("missing " + name);
    private static bool Flag(string[] args, string name) => args.Contains(name);

    private static int Status(AgentHost host)
    {
        Console.WriteLine($"version        {AgentHost.Version}");
        Console.WriteLine($"data directory {host.DataDir}");
        Console.WriteLine($"running        {(host.IsRunning() ? "yes" : "no")}");

        AgentIdentity? identity = null;
        string keyId = "-";
        if (Directory.Exists(host.DataDir) && (OperatingSystem.IsWindows() || AgentHost.DevInsecure))
        {
            var identities = host.Identities();
            identity = identities.Load();
            keyId = identities.Current()?.KeyId ?? "-";
        }
        Console.WriteLine(identity is null
            ? "paired         no"
            : $"paired         yes: origin {identity.Origin}, agent {identity.AgentId}, key id {keyId}, contract {identity.NegotiatedContract}, since {identity.PairedAt:u}");

        var settings = Directory.Exists(host.DataDir) ? AgentSettings.Load(host.DataDir) : new AgentSettings();
        Console.WriteLine(settings.Printer is { } p
            ? $"printer        '{p.Queue}' {p.Language} {p.Dpi}dpi {p.WidthMm.ToString(CultureInfo.InvariantCulture)}x{p.HeightMm.ToString(CultureInfo.InvariantCulture)}mm (declared by operator; ERP id {p.ErpPrinterId?.ToString() ?? "not registered yet"})"
            : "printer        none selected — the agent will not start");
        if (OperatingSystem.IsWindows())
            Console.WriteLine($"autostart      {(Autostart.Current() is { } cmd ? "enabled: " + cmd : "disabled")}");

        if (identity is not null && File.Exists(host.JournalPath) && !host.IsRunning())
        {
            using var ledger = JobLedger.Open(new FileJournalStore(host.JournalPath), new FileBlobStore(host.PayloadDir), identity.AgentId, identity.Origin, TimeProvider.System, Guid.NewGuid, out _);
            var jobs = ledger.Jobs();
            Console.WriteLine($"journal        {jobs.Count} jobs: " + string.Join(", ", jobs.GroupBy(j => j.State).Select(g => $"{g.Key}={g.Count()}")));
            Console.WriteLine($"outbox         {ledger.PendingAccepts().Count} accepts, {ledger.PendingEvents().Count} events owed to the ERP");
            if (ledger.QuarantineReason is { } q) Console.WriteLine($"QUARANTINED    {q} — nothing is submitted until 'recover --clear-quarantine'");
            foreach (var j in jobs.Where(j => j.Block is not null))
                Console.WriteLine($"blocked        job {j.Key.JobId}: {j.Block!.Code} ×{j.Block.Count} since {j.Block.At:u}");
            foreach (var j in jobs.Where(j => j.State == JobState.Unknown))
                Console.WriteLine($"unknown        job {j.Key.JobId}: outcome unknown, will not be resubmitted");
        }
        else if (host.IsRunning()) Console.WriteLine("journal        in use by the running agent (stop it to inspect)");
        return 0;
    }

    private static int Printers()
    {
        if (!OperatingSystem.IsWindows()) return Fail(5, "printer listing is implemented for Windows only");
        var names = AgentHost.CreatePlatform().Queues();
        if (names.Count == 0) Console.WriteLine("(no print queues)");
        foreach (var n in names) Console.WriteLine(n);
        return 0;
    }

    private static int SelectPrinter(AgentHost host, string[] args)
    {
        var queue = Required(args, "--queue");
        var language = Required(args, "--language").ToLowerInvariant();
        if (language is not ("tspl" or "zpl")) throw new ArgumentException("--language must be tspl or zpl");
        var dpi = int.Parse(Required(args, "--dpi"), CultureInfo.InvariantCulture);
        if (dpi is not (203 or 300 or 600)) throw new ArgumentException("--dpi must be 203, 300 or 600 (the values the ERP accepts)");
        var width = double.Parse(Required(args, "--width-mm"), CultureInfo.InvariantCulture);
        var height = double.Parse(Required(args, "--height-mm"), CultureInfo.InvariantCulture);
        if (width is < 10 or > 200 || height is < 5 or > 300) throw new ArgumentException("media size out of range (10–200 × 5–300 mm)");
        if (OperatingSystem.IsWindows() && !AgentHost.CreatePlatform().Queues().Contains(queue, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"no print queue named '{queue}'. Use 'printers' to list them.");
        if (host.IsRunning()) return Fail(6, "stop the agent before changing the printer");

        host.EnsureDataDir();
        var old = AgentSettings.Load(host.DataDir).Printer;
        // A new local id for a different queue; the ERP then sees it as a different printer.
        var id = old is not null && string.Equals(old.Queue, queue, StringComparison.OrdinalIgnoreCase) ? old.LocalPrinterId : Guid.NewGuid();
        new AgentSettings(new PrinterSelection(id, queue, language, dpi, width, height)).Save(host.DataDir);
        Console.WriteLine($"selected '{queue}' as {language} {dpi}dpi {width.ToString(CultureInfo.InvariantCulture)}x{height.ToString(CultureInfo.InvariantCulture)}mm.");
        Console.WriteLine("These values are your declaration; the agent cannot verify that the printer understands this language.");
        return 0;
    }

    private static int Pair(AgentHost host, string[] args)
    {
        var origin = HttpErpClient.OriginKey(Required(args, "--origin"));
        if (host.IsRunning()) return Fail(6, "stop the agent before pairing");
        host.EnsureDataDir();
        var identities = host.Identities();
        if (identities.Load() is { } existing)
        {
            if (!string.Equals(existing.Origin, origin, StringComparison.Ordinal))
                return Fail(2, $"already paired with {existing.Origin}. A journal belongs to one ERP origin: run 'unpair' and move the data directory away before pairing with another (see docs/recovery.md).");
            return Fail(2, "already paired. Run 'unpair' first to pair again with the same ERP.");
        }
        if (File.Exists(host.JournalPath))
        {
            // Re-pairing with the same ERP keeps the journal only if it is the same agent identity; that is
            // checked when the journal is opened. A journal of another origin is refused there as well.
            Console.WriteLine("note: an existing journal will be reused only if the ERP returns the same agent identity.");
        }

        string code;
        if (Option(args, "--code-file") is { } file) code = File.ReadAllText(file).Trim();
        else
        {
            Console.Write("pairing code: ");
            code = (Console.IsInputRedirected ? Console.ReadLine() : ReadHidden())?.Trim() ?? "";
        }
        if (code.Length is < 6 or > 20) throw new ArgumentException("pairing code looks wrong");

        var name = Option(args, "--name") ?? Environment.MachineName;
        using var http = new HttpErpClient(origin, () => null, new HttpErpOptions { AgentVersion = AgentHost.Version });
        PairResult result;
        try { result = http.Pair(code, identities.InstallId(), name, OperatingSystem.IsWindows() ? "windows" : "other", Environment.OSVersion.Version.ToString()); }
        catch (ErpException ex) { return Fail(1, $"the ERP refused pairing ({ex.Status} {ex.ErrorCode}). Codes are single-use and expire after 10 minutes."); }
        catch (ErpUnavailableException ex) { return Fail(1, "could not reach the ERP: " + ex.Message + ". If a code was consumed, create a new one."); }

        var now = DateTimeOffset.UtcNow;
        identities.SavePairing(new AgentIdentity(origin, result.AgentId, identities.InstallId(), name, result.NegotiatedContract, now, now, result.HeartbeatSec, result.ClaimWaitSec), result.Credential);
        AgentCredential.TryParse(result.Credential, out var credential);
        Console.WriteLine($"paired with {origin} as agent {result.AgentId} (key id {credential!.KeyId}).");
        Console.WriteLine("Nothing is claimed or sent to a printer until a printer is selected and 'run' is started.");
        return 0;
    }

    private static string ReadHidden()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
    }

    private static int Unpair(AgentHost host)
    {
        if (host.IsRunning()) return Fail(6, "stop the agent before unpairing");
        if (!Directory.Exists(host.DataDir)) return Print("not paired");
        host.Identities().Unpair();
        Console.WriteLine("credential removed from this computer. Revoke the agent in the ERP as well to invalidate it there.");
        Console.WriteLine("The journal was kept.");
        return 0;
    }

    private static int RunForeground(AgentHost host)
    {
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        host.EnsureDataDir();
        var log = new FileLog(host.DataDir, echo: true);
        if (!OperatingSystem.IsWindows() && !AgentHost.DevInsecure) { log.Write("printing is implemented for Windows only"); return 5; }
        using var instance = host.TryAcquireInstance();
        if (instance is null) { log.Write("another agent instance is already running; not starting"); return 6; }
        using var service = new AgentService(host, AgentHost.CreatePlatform(), log, TimeProvider.System);
        switch (service.Start())
        {
            case WorkerState.NotPaired: log.Write("not paired; run 'pair' first"); return 3;
            case WorkerState.NoPrinter or WorkerState.PrinterMissing: log.Write(service.Message + " Run 'select-printer' first."); return 4;
            case WorkerState.JournalLocked: log.Write(service.Message); return 6;
            case WorkerState.JournalProblem: log.Write(service.Message); return 7;
        }
        using var stop = host.CreateStopEvent();
        while (service.WorkerAlive && WaitHandle.WaitAny([cancel.Token.WaitHandle, stop], 500) == WaitHandle.WaitTimeout) { }
        service.StopWorker();
        return service.State switch
        {
            WorkerState.CredentialRejected => 9,
            WorkerState.ContractUnsupported => 10,
            WorkerState.StorageFailure => 11,
            WorkerState.Failed => 12,
            _ => 0,
        };
    }

    private static int Stop(AgentHost host, string[] args)
    {
        var wait = int.Parse(Option(args, "--wait") ?? "0", CultureInfo.InvariantCulture);
        if (!host.IsRunning()) return Print("not running");
        if (!host.SignalStop()) return Fail(1, "an agent holds the journal but could not be signalled");
        for (var i = 0; i < wait * 4 && host.IsRunning(); i++) Thread.Sleep(250);
        if (wait > 0 && host.IsRunning()) return Fail(1, $"still running after {wait}s (it finishes its current ERP call first)");
        return Print(wait > 0 ? "stopped" : "stop requested");
    }

    /// <summary>Checks the pieces that depend on this machine, in a temporary directory. Touches no ERP, no
    /// printer, no registry and none of the agent's real data.</summary>
    private static int SelfTest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lpagent-selftest-" + Guid.NewGuid().ToString("N"));
        var failures = 0;
        void Step(string name, Action check)
        {
            try { check(); Console.WriteLine("ok    " + name); }
            catch (Exception ex) { failures++; Console.WriteLine($"FAIL  {name}: {ex.GetType().Name} {ex.Message}"); }
        }
        try
        {
            var host = new AgentHost(dir);
            host.EnsureDataDir();
            // Shaped like a credential but made of fixed filler bytes; it authenticates nowhere.
            var synthetic = AgentCredential.Scheme + "_" + new string('A', 22) + "." + new string('B', 43);
            Step("credential is stored protected and read back", () =>
            {
                var store = host.Identities();
                var now = DateTimeOffset.UtcNow;
                store.SavePairing(new AgentIdentity("https://selftest.invalid", Guid.NewGuid(), store.InstallId(), "selftest", ContractInfo.V0_1, now, now, 30, 20), synthetic);
                if (store.Current()?.ToAuthorizationHeader() != "Bearer " + synthetic) throw new InvalidOperationException("round trip mismatch");
                var onDisk = File.ReadAllBytes(Path.Combine(dir, "credential.bin"));
                if (OperatingSystem.IsWindows() && Encoding.ASCII.GetString(onDisk).Contains(new string('B', 43), StringComparison.Ordinal))
                    throw new InvalidOperationException("credential is readable in the file");
            });
            Step("journal has a single owner", () =>
            {
                using var first = new FileJournalStore(Path.Combine(dir, "journal.lpj"));
                try { using var second = new FileJournalStore(Path.Combine(dir, "journal.lpj")); }
                catch (JournalLockedException) { return; }
                throw new InvalidOperationException("second writer was not refused");
            });
            Step("journal survives reopen and refuses another origin", () =>
            {
                var agent = Guid.NewGuid();
                JobLedger.Open(new FileJournalStore(Path.Combine(dir, "j2.lpj")), new FileBlobStore(Path.Combine(dir, "p2")), agent, "https://a.invalid", TimeProvider.System, Guid.NewGuid, out _).Dispose();
                JobLedger.Open(new FileJournalStore(Path.Combine(dir, "j2.lpj")), new FileBlobStore(Path.Combine(dir, "p2")), agent, "https://a.invalid", TimeProvider.System, Guid.NewGuid, out _).Dispose();
                try { JobLedger.Open(new FileJournalStore(Path.Combine(dir, "j2.lpj")), new FileBlobStore(Path.Combine(dir, "p2")), agent, "https://b.invalid", TimeProvider.System, Guid.NewGuid, out _).Dispose(); }
                catch (JournalMismatchException) { return; }
                throw new InvalidOperationException("journal of another origin was accepted");
            });
            if (OperatingSystem.IsWindows())
                Step("print queues can be listed", () => Console.WriteLine($"      {AgentHost.CreatePlatform().Queues().Count} queue(s) visible"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures++; Console.WriteLine("FAIL  cleanup: " + ex.Message); }
        }
        Console.WriteLine(failures == 0 ? "selftest passed" : $"selftest failed ({failures})");
        return failures == 0 ? 0 : 1;
    }

    private static int AutostartCommand(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return Fail(5, "autostart is implemented for Windows only");
        switch (args.ElementAtOrDefault(1)?.ToLowerInvariant())
        {
            case "enable":
                var exe = Path.Combine(AppContext.BaseDirectory, "LabelPrinterAgentBg.exe");
                if (!File.Exists(exe)) return Fail(1, "background executable not found next to this program");
                Autostart.Enable(exe);
                return Print("autostart enabled for this user. It takes effect at the next logon; nothing was started now.");
            case "disable":
                Autostart.Disable();
                return Print("autostart disabled");
            case "status":
                return Print(Autostart.Current() is { } cmd ? "enabled: " + cmd : "disabled");
            default:
                throw new ArgumentException("use: autostart enable|disable|status");
        }
    }

    private static int Recover(AgentHost host, string[] args)
    {
        if (!Flag(args, "--clear-quarantine")) throw new ArgumentException("use: recover --clear-quarantine --yes");
        var identity = host.Identities().Load() ?? throw new ArgumentException("not paired");
        using var ledger = JobLedger.Open(new FileJournalStore(host.JournalPath), new FileBlobStore(host.PayloadDir), identity.AgentId, identity.Origin, TimeProvider.System, Guid.NewGuid, out _);
        if (ledger.QuarantineReason is not { } reason) return Print("the journal is not quarantined; nothing to do");
        var doubtful = ledger.Jobs().Where(j => j.State == JobState.Accepted).ToList();
        Console.WriteLine($"quarantine reason: {reason}");
        Console.WriteLine($"{doubtful.Count} accepted job(s) will be marked failed WITHOUT being printed, because this journal cannot prove they were never submitted:");
        foreach (var j in doubtful) Console.WriteLine("  " + j.Key.JobId);
        if (!Flag(args, "--yes")) return Fail(1, "re-run with --yes to confirm. Check physically printed labels first (docs/recovery.md).");
        ledger.ClearQuarantine();
        return Print("quarantine cleared.");
    }

    private static int SpoolFile(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return Fail(5, "spooling is implemented for Windows only");
        var queue = Required(args, "--queue");
        var bytes = File.ReadAllBytes(Required(args, "--file"));
        if (bytes.Length is 0 or > (int)ContractInfo.MaxPayloadBytes) throw new ArgumentException("file is empty or larger than 8 MiB");
        if (!AgentHost.CreatePlatform().Queues().Contains(queue, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"no print queue named '{queue}'");
        if (!Flag(args, "--yes")) return Fail(1, $"this sends {bytes.Length} bytes once, RAW, to '{queue}'. Re-run with --yes to confirm.");
        var printer = Guid.NewGuid();
        var spooler = new WindowsRawSpooler(queue, printer, Console.WriteLine);
        var result = spooler.Submit(new Core.Spooling.SpoolRequest(Guid.NewGuid(), printer, PrinterLanguage.Tspl, "manual", Transport.SpoolerRaw, bytes));
        Console.WriteLine($"outcome: {result.Outcome} spooler job: {result.SpoolerJobRef ?? "-"} error: {result.ErrorCode ?? "-"}");
        Console.WriteLine("A spooler acknowledgement does not prove that anything was printed.");
        return result.Outcome == Core.Spooling.SpoolOutcome.Accepted ? 0 : 1;
    }
#endif
}
