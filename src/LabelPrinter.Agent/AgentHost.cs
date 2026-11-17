using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabelPrinter.Agent.Core.Runtime;
using LabelPrinter.Agent.Core.Spooling;
using LabelPrinter.Agent.Windows;

namespace LabelPrinter.Agent;

/// <summary>Size-capped text log with a short in-memory tail for the UI. Callers pass codes and ids only; no
/// credential or payload ever reaches it.</summary>
internal sealed class FileLog(string directory, bool echo)
{
    private const long MaxBytes = 1024 * 1024;
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<string> _tail = new();
    private string LogPath => Path.Combine(directory, "agent.log");

    public IReadOnlyList<string> Recent(int count) => [.. _tail.TakeLast(count)];

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'} {message}";
        _tail.Enqueue(line);
        while (_tail.Count > 200 && _tail.TryDequeue(out _)) { }
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

internal sealed record UiInfo(int Port, string Token);

internal sealed class AgentHost(string dataDir, ISecretProtector? protectorOverride = null)
{
    public const string ProductName = "LabelPrinterAgent";
    public string DataDir { get; } = dataDir;
    public string JournalPath => Path.Combine(DataDir, "journal.lpj");
    public string PayloadDir => Path.Combine(DataDir, "payloads");
    public string UiInfoPath => Path.Combine(DataDir, "ui.json");
    public string BrowserProfileDir => Path.Combine(DataDir, "browser");

    public static string Version =>
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static bool DevInsecure => !OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("LPAGENT_DEV_INSECURE") == "1";

    public static IPlatform CreatePlatform() => OperatingSystem.IsWindows() ? new WindowsPlatform() : new DevPlatform();

    public static string DefaultDataDir() =>
        Environment.GetEnvironmentVariable("LPAGENT_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    public void EnsureDataDir()
    {
        var created = !Directory.Exists(DataDir);
        Directory.CreateDirectory(DataDir);
        if (created && OperatingSystem.IsWindows()) Acl.RestrictDirectoryToCurrentUser(DataDir);
        else if (created && !OperatingSystem.IsWindows()) File.SetUnixFileMode(DataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public IdentityStore Identities()
    {
        if (protectorOverride is not null) return new IdentityStore(DataDir, protectorOverride);
        if (OperatingSystem.IsWindows()) return new IdentityStore(DataDir, new DpapiProtector(), Acl.RestrictToCurrentUser);
        if (!DevInsecure) throw new PlatformNotSupportedException("Credential storage is implemented for Windows only.");
        return new IdentityStore(DataDir, new DevPlainProtector(), p => { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite); });
    }

    private string Hash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(DataDir).ToUpperInvariant())))[..16];
    private string StopEventName => "Local\\LabelPrinterAgent.Stop." + Hash();
    private string InstanceName => "Local\\LabelPrinterAgent.Instance." + Hash();

    /// <summary>Event that a running agent waits on; the installer and <c>stop</c> signal it.</summary>
    public EventWaitHandle CreateStopEvent() =>
        OperatingSystem.IsWindows() ? new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName) : new ManualResetEvent(false);

    public bool SignalStop()
    {
        if (!OperatingSystem.IsWindows()) return false; // named events exist on Windows only; elsewhere stop the process
        if (!EventWaitHandle.TryOpenExisting(StopEventName, out var handle)) return false;
        using (handle) handle.Set();
        return true;
    }

    /// <summary>Takes the per-data-directory instance lock; null when another instance already holds it.</summary>
    public Mutex? TryAcquireInstance()
    {
        var mutex = new Mutex(false, InstanceName);
        try { if (mutex.WaitOne(0)) return mutex; }
        catch (AbandonedMutexException) { return mutex; } // previous owner died; we own it now
        mutex.Dispose();
        return null;
    }

    public bool InstanceRunning()
    {
        if (!Mutex.TryOpenExisting(InstanceName, out var m)) return false;
        m.Dispose();
        return true;
    }

    /// <summary>True if another process holds the journal or the instance lock.</summary>
    public bool IsRunning()
    {
        if (InstanceRunning()) return true;
        if (!File.Exists(JournalPath)) return false;
        try { using var probe = new FileStream(JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    public void WriteUiInfo(UiInfo info)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(info);
        var tmp = UiInfoPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (OperatingSystem.IsWindows()) Acl.RestrictToCurrentUser(tmp); else File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            fs.Write(bytes);
        }
        File.Move(tmp, UiInfoPath, overwrite: true);
    }

    public UiInfo? ReadUiInfo()
    {
        try { return File.Exists(UiInfoPath) ? JsonSerializer.Deserialize<UiInfo>(File.ReadAllBytes(UiInfoPath)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static string OsDescription() => RuntimeInformation.OSDescription;
}
