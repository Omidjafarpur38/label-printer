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
    // Windowless build: runs the agent loop only. Started by opt-in autostart or by the user.
    private static int Main()
    {
        using var cancel = new CancellationTokenSource();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { cancel.Cancel(); } catch (ObjectDisposedException) { } };
        return new AgentHost(AgentHost.DefaultDataDir()).Run(echo: false, cancel.Token);
    }
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
          run                             run in the foreground until Ctrl+C
          stop                            ask a running agent to stop
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
                "stop" => host.SignalStop() ? Print("stop requested") : Fail(1, "no running agent found for this data directory"),
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
        var names = AgentHost.Queues();
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
        if (OperatingSystem.IsWindows() && !AgentHost.Queues().Contains(queue, StringComparer.OrdinalIgnoreCase))
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
        return host.Run(echo: true, cancel.Token);
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
        if (!AgentHost.Queues().Contains(queue, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"no print queue named '{queue}'");
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
