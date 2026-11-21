using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LabelPrinter.Agent.Core.Updates;

/// <summary>Why an update offer was refused. The code is stable and safe to show; the message is for the log.</summary>
public sealed class UpdateRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Semantic version with optional pre-release part (<c>0.2.2-exp.1</c>). Build metadata after '+' is ignored.</summary>
public sealed class AgentVersion : IComparable<AgentVersion>
{
    private readonly int _major, _minor, _patch;
    private readonly string[] _pre;
    private AgentVersion(int major, int minor, int patch, string[] pre) { _major = major; _minor = minor; _patch = patch; _pre = pre; }

    public static AgentVersion Parse(string text)
    {
        var m = Regex.Match(text ?? "", @"^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})(?:-([0-9A-Za-z.-]{1,40}))?(?:\+[0-9A-Za-z.-]{1,40})?$", RegexOptions.CultureInvariant);
        if (!m.Success) throw new FormatException("not a version: " + text);
        var pre = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : [];
        if (pre.Any(p => p.Length == 0 || (p.All(char.IsAsciiDigit) && p.Length > 1 && p[0] == '0'))) throw new FormatException("bad pre-release: " + text);
        return new AgentVersion(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), pre);
    }

    public static bool TryParse(string? text, out AgentVersion? v)
    {
        try { v = Parse(text ?? ""); return true; } catch (FormatException) { v = null; return false; }
    }

    public int CompareTo(AgentVersion? other)
    {
        if (other is null) return 1;
        var c = _major.CompareTo(other._major); if (c != 0) return c;
        c = _minor.CompareTo(other._minor); if (c != 0) return c;
        c = _patch.CompareTo(other._patch); if (c != 0) return c;
        if (_pre.Length == 0 || other._pre.Length == 0) return _pre.Length == other._pre.Length ? 0 : _pre.Length == 0 ? 1 : -1; // a release beats its pre-releases
        for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var a = _pre[i]; var b = other._pre[i];
            var an = a.All(char.IsAsciiDigit); var bn = b.All(char.IsAsciiDigit);
            c = an && bn ? long.Parse(a, CultureInfo.InvariantCulture).CompareTo(long.Parse(b, CultureInfo.InvariantCulture))
              : an ? -1 : bn ? 1 : string.CompareOrdinal(a, b);
            if (c != 0) return c;
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }
}

public sealed record UpdateOffer(string Version, string File, long SizeBytes, string Sha256, DateTimeOffset IssuedAt, string Notes);

/// <summary>
/// The signed update description. The installer itself is not Authenticode-signed, so the update channel has its
/// own signature: an ECDSA P-256 / SHA-256 signature over the exact manifest bytes, checked against public keys
/// compiled into the program. Nothing in the manifest is trusted (not even parsed) before the signature verifies;
/// the downloaded installer is then accepted only if its size and SHA-256 equal the signed values, and only a
/// strictly newer version is ever offered (no downgrade, no replay of an old manifest).
/// </summary>
public static class UpdateManifest
{
    public const string Channel = "experimental";
    public const int MaxManifestBytes = 16 * 1024;
    public const int MaxSignatureTextBytes = 512;
    public const long MaxInstallerBytes = 200L * 1024 * 1024;
    public const int MaxNotesChars = 400;

    private static readonly Regex Sha = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string InstallerFileName(string version) => $"LabelPrinterAgent-{version}-win-x64-unsigned-setup.exe";

    public static UpdateOffer Verify(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signature, IReadOnlyList<byte[]> trustedKeysSpki, string currentVersion)
    {
        if (trustedKeysSpki.Count == 0) throw new UpdateRejectedException("NO_TRUSTED_KEY", "no update signing key is built in");
        if (manifest.Length is 0 or > MaxManifestBytes) throw new UpdateRejectedException("MANIFEST_SIZE", "manifest size");
        if (signature.Length != 64) throw new UpdateRejectedException("SIGNATURE_FORMAT", "signature must be 64 bytes (IEEE P1363)");

        var data = manifest.ToArray();
        var sig = signature.ToArray();
        var valid = false;
        foreach (var key in trustedKeysSpki)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(key, out _);
                if (ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) { valid = true; break; }
            }
            catch (CryptographicException) { /* a malformed built-in key never accepts anything */ }
        }
        if (!valid) throw new UpdateRejectedException("SIGNATURE_INVALID", "signature does not match any trusted key");

        // Authentic from here on. Still validated strictly: a valid signature does not excuse a malformed document.
        UpdateOffer offer;
        try
        {
            using var doc = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 4 });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new UpdateRejectedException("MANIFEST_SHAPE", "not an object");
            if (!r.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1) throw new UpdateRejectedException("MANIFEST_SCHEMA", "unsupported schema");
            if (Str(r, "channel") != Channel) throw new UpdateRejectedException("MANIFEST_CHANNEL", "other channel");
            var version = Str(r, "version");
            var file = Str(r, "file");
            var sha = Str(r, "sha256");
            if (!r.TryGetProperty("sizeBytes", out var sz) || sz.ValueKind != JsonValueKind.Number || !sz.TryGetInt64(out var size) || size < 1 || size > MaxInstallerBytes) throw new UpdateRejectedException("MANIFEST_SIZEBYTES", "installer size out of range");
            if (!AgentVersion.TryParse(version, out _)) throw new UpdateRejectedException("MANIFEST_VERSION", "bad version");
            if (file != InstallerFileName(version)) throw new UpdateRejectedException("MANIFEST_FILE", "file name must be the installer of that exact version");
            if (!Sha.IsMatch(sha)) throw new UpdateRejectedException("MANIFEST_SHA256", "bad sha256");
            if (!DateTimeOffset.TryParse(Str(r, "issuedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var issued)) throw new UpdateRejectedException("MANIFEST_ISSUED", "bad issuedAt");
            var notes = r.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? Clean(n.GetString()!) : "";
            offer = new UpdateOffer(version, file, size, sha, issued, notes);
        }
        catch (JsonException) { throw new UpdateRejectedException("MANIFEST_JSON", "not valid JSON"); }
        catch (InvalidOperationException) { throw new UpdateRejectedException("MANIFEST_SHAPE", "unexpected value type"); }
        catch (FormatException) { throw new UpdateRejectedException("MANIFEST_SHAPE", "unexpected value format"); }

        if (!AgentVersion.TryParse(currentVersion, out var current) || current is null) throw new UpdateRejectedException("CURRENT_VERSION", "own version is unreadable; refusing to update");
        if (AgentVersion.Parse(offer.Version).CompareTo(current) <= 0) throw new UpdateRejectedException("NOT_NEWER", "offered version is not newer");
        return offer;
    }

    private static string Str(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Clean(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, MaxNotesChars));
        foreach (var ch in s)
        {
            if (sb.Length >= MaxNotesChars) break;
            sb.Append(char.IsControl(ch) && ch is not '\n' ? ' ' : ch);
        }
        return sb.ToString().Trim();
    }

    // ---- release side (used by tools/update/sign-update.cs and by tests; never needed by the running agent) ----

    public static byte[] Create(UpdateOffer offer)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("channel", Channel);
            w.WriteString("version", offer.Version);
            w.WriteString("file", offer.File);
            w.WriteNumber("sizeBytes", offer.SizeBytes);
            w.WriteString("sha256", offer.Sha256);
            w.WriteString("issuedAt", offer.IssuedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            w.WriteString("notes", offer.Notes);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    public static byte[] Sign(ReadOnlySpan<byte> manifest, ECDsa privateKey) =>
        privateKey.SignData(manifest.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static (string PrivatePkcs8Base64, string PublicSpkiBase64) GenerateKeyPair()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(k.ExportPkcs8PrivateKey()), Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>The release job passes the installer path; hash and size come from the bytes, never from the caller.</summary>
    public static (string Sha256, long Size) Measure(string path)
    {
        using var fs = File.OpenRead(path);
        return (Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant(), fs.Length);
    }
}
