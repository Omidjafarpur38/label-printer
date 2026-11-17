using System.Text;
using System.Text.Json;
using LabelPrinter.Agent.Core.Contracts;

namespace LabelPrinter.Agent.Core.Runtime;

/// <summary>Encrypts secrets for the identity that runs the agent (DPAPI on Windows).</summary>
public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plain);
    byte[] Unprotect(ReadOnlySpan<byte> protectedBytes);
}

/// <summary>Non-secret pairing facts. Safe to show in diagnostics.</summary>
public sealed record AgentIdentity(
    string Origin,
    Guid AgentId,
    Guid InstallId,
    string DisplayName,
    string NegotiatedContract,
    DateTimeOffset PairedAt,
    DateTimeOffset CredentialSetAt,
    int HeartbeatSec,
    int ClaimWaitSec);

/// <summary>
/// Pairing state on disk: identity.json (not secret) and the credential, which only ever touches the disk
/// through <see cref="ISecretProtector"/>. A rotated credential is kept beside the active one until it has been
/// used successfully. The install id survives unpairing so that re-pairing updates the same ERP agent row.
/// </summary>
public sealed class IdentityStore(string directory, ISecretProtector protector, Action<string>? restrictFile = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private string IdentityPath => Path.Combine(directory, "identity.json");
    private string InstallPath => Path.Combine(directory, "install-id");
    private string ActivePath => Path.Combine(directory, "credential.bin");
    private string PendingPath => Path.Combine(directory, "credential.next.bin");

    public AgentIdentity? Load() =>
        File.Exists(IdentityPath) ? JsonSerializer.Deserialize<AgentIdentity>(File.ReadAllBytes(IdentityPath), Json) : null;

    public Guid InstallId()
    {
        Directory.CreateDirectory(directory);
        if (File.Exists(InstallPath) && Guid.TryParseExact(File.ReadAllText(InstallPath).Trim(), "D", out var existing)) return existing;
        var created = Guid.NewGuid();
        WriteAtomic(InstallPath, Encoding.ASCII.GetBytes(created.ToString("D")));
        return created;
    }

    public void SavePairing(AgentIdentity identity, string credential)
    {
        Directory.CreateDirectory(directory);
        WriteSecret(ActivePath, credential);
        File.Delete(PendingPath);
        WriteAtomic(IdentityPath, JsonSerializer.SerializeToUtf8Bytes(identity, Json));
    }

    public bool HasPending => File.Exists(PendingPath);

    /// <summary>The credential to present: a not yet confirmed rotation result first, else the active one.</summary>
    public AgentCredential? Current() => Read(PendingPath) ?? Read(ActivePath);

    public void SavePending(string credential) => WriteSecret(PendingPath, credential);

    /// <summary>The rotated credential worked: it becomes the active one.</summary>
    public void PromotePending(DateTimeOffset now)
    {
        if (!File.Exists(PendingPath)) return;
        File.Move(PendingPath, ActivePath, overwrite: true);
        if (Load() is { } identity) WriteAtomic(IdentityPath, JsonSerializer.SerializeToUtf8Bytes(identity with { CredentialSetAt = now }, Json));
    }

    public void DiscardPending() => File.Delete(PendingPath);

    /// <summary>Removes the credential and pairing facts. Journal and settings are not touched.</summary>
    public void Unpair()
    {
        File.Delete(PendingPath);
        File.Delete(ActivePath);
        File.Delete(IdentityPath);
    }

    private AgentCredential? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var plain = protector.Unprotect(File.ReadAllBytes(path));
        try { return AgentCredential.TryParse(Encoding.ASCII.GetString(plain), out var c) ? c : null; }
        finally { Array.Clear(plain); }
    }

    private void WriteSecret(string path, string credential)
    {
        if (!AgentCredential.TryParse(credential, out _)) throw new ArgumentException("not a credential", nameof(credential));
        var plain = Encoding.ASCII.GetBytes(credential);
        try { WriteAtomic(path, protector.Protect(plain)); }
        finally { Array.Clear(plain); }
    }

    private void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            restrictFile?.Invoke(tmp); // tighten the ACL before any secret byte is written
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Operator-declared printer. Language, DPI and media are statements by the operator, not detections.</summary>
public sealed record PrinterSelection(Guid LocalPrinterId, string Queue, string Language, int Dpi, double WidthMm, double HeightMm, Guid? ErpPrinterId = null);

public sealed record AgentSettings(PrinterSelection? Printer = null, bool Paused = false)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AgentSettings Load(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<AgentSettings>(File.ReadAllBytes(path), Json) ?? new AgentSettings() : new AgentSettings();
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(this, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Reason a job cannot be taken with this configuration, or null.</summary>
    public string? Refuse(JobEnvelope job)
    {
        if (Printer is not { ErpPrinterId: { } erpId } p) return "PRINTER_NOT_CONFIGURED";
        if (job.PrinterId != erpId) return "PRINTER_NOT_SELECTED";
        if (job.Output.Language != p.Language) return "PRINTER_LANGUAGE_MISMATCH";
        if (job.Output.Dpi != p.Dpi) return "PRINTER_DPI_MISMATCH";
        return null;
    }
}
