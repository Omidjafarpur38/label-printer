namespace LabelPrinter.Agent.Core.Updates;

/// <summary>
/// Public keys (base64 SubjectPublicKeyInfo, ECDSA P-256) whose signatures on the update manifest are accepted.
/// EMPTY until the owner creates the update signing key (docs/auto-update.md): with no key the update check is
/// switched off and says so; it can never accept an unsigned or unknown-key manifest. Two slots allow rotating
/// the key without stranding installed copies: ship a release that trusts both, then retire the old one.
/// </summary>
public static class TrustedKeys
{
    public static readonly string[] SpkiBase64 = [];

    public static IReadOnlyList<byte[]> Parsed() => [.. SpkiBase64.Select(Convert.FromBase64String)];
}
