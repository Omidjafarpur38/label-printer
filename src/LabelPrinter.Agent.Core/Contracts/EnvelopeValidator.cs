using System.Security.Cryptography;

namespace LabelPrinter.Agent.Core.Contracts;

public sealed record ValidationError(string Code, string Field);

/// <summary>
/// Structural and integrity checks. These detect malformed, oversized or corrupted input. They do NOT
/// authenticate the origin of a job (that rests on TLS plus the agent credential, both outside A1), and they
/// do not inspect printer commands inside the payload (see docs/architecture.md, "Payload command validation").
/// </summary>
public static class EnvelopeValidator
{
    public static IReadOnlyList<ValidationError> ValidateEnvelope(JobEnvelope e, Guid pairedAgentId)
    {
        var errors = new List<ValidationError>();
        void Add(string code, string field) => errors.Add(new ValidationError(code, field));

        if (!ContractInfo.Supported.Contains(e.ContractVersion)) Add("CONTRACT_VERSION_UNSUPPORTED", "contractVersion");
        if (e.JobId == Guid.Empty) Add("REQUIRED", "jobId");
        if (e.AgentId != pairedAgentId) Add("AGENT_MISMATCH", "agentId");
        if (e.PrinterId == Guid.Empty) Add("REQUIRED", "printerId");
        if (e.ProfileId == Guid.Empty) Add("REQUIRED", "profileId");
        if (e.ProfileVersion < 1) Add("OUT_OF_RANGE", "profileVersion");
        if (string.IsNullOrEmpty(e.IdempotencyKey) || e.IdempotencyKey.Length > ContractInfo.MaxIdempotencyKeyLength)
            Add("OUT_OF_RANGE", "idempotencyKey");
        if (e.Kind is not ("test" or "label")) Add("UNSUPPORTED", "kind");
        if (e.Copies < 1 || e.Copies > ContractInfo.MaxCopies) Add("OUT_OF_RANGE", "copies");
        if (string.IsNullOrEmpty(e.CorrelationId) || e.CorrelationId.Length > 64) Add("OUT_OF_RANGE", "correlationId");
        if (e.ReprintOf is { } r && (r == Guid.Empty || r == e.JobId)) Add("INVALID", "reprintOf");
        if (e.DeliveryAttempt < 1 || e.DeliveryAttempt > ContractInfo.MaxDeliveryAttempts) Add("OUT_OF_RANGE", "deliveryAttempt");

        if (e.AuthorizationExpiresAt <= e.CreatedAt) Add("INVALID", "authorizationExpiresAt");
        else if (e.AuthorizationExpiresAt - e.CreatedAt > ContractInfo.MaxAuthorizationWindow) Add("OUT_OF_RANGE", "authorizationExpiresAt");
        if (e.AgentMustNotSubmitAfter > e.AuthorizationExpiresAt || e.AgentMustNotSubmitAfter <= e.CreatedAt)
            Add("INVALID", "agentMustNotSubmitAfter");

        var o = e.Output;
        var profile = OutputProfile.Find(o.Profile);
        if (profile is null) Add("UNSUPPORTED", "output.profile");
        else if (OutputProfile.WireName(profile.Language) != o.Language) Add("PROFILE_LANGUAGE_MISMATCH", "output.language");
        if (o.ContentType != "application/octet-stream") Add("UNSUPPORTED", "output.contentType");
        if (o.SizeBytes < 1 || o.SizeBytes > ContractInfo.MaxPayloadBytes) Add("OUT_OF_RANGE", "output.sizeBytes");
        if (!IsSha256Hex(o.Sha256)) Add("INVALID", "output.sha256");
        if (o.Pages < 1 || o.Pages > ContractInfo.MaxPages) Add("OUT_OF_RANGE", "output.pages");
        if (o.Dpi < ContractInfo.MinDpi || o.Dpi > ContractInfo.MaxDpi) Add("OUT_OF_RANGE", "output.dpi");
        if (!InRange(o.MediaWidthMm, 1, 500)) Add("OUT_OF_RANGE", "output.mediaMm.w");
        if (!InRange(o.MediaHeightMm, 1, 1000)) Add("OUT_OF_RANGE", "output.mediaMm.h");
        if (!InRange(o.GapMm, 0, 100)) Add("OUT_OF_RANGE", "output.gapMm");
        return errors;
    }

    public static IReadOnlyList<ValidationError> ValidatePayload(JobOutput output, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != output.SizeBytes) return [new("PAYLOAD_SIZE_MISMATCH", "payload")];
        return Sha256Hex(payload) == output.Sha256 ? [] : [new("PAYLOAD_HASH_MISMATCH", "payload")];
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Lowercase hex only: the ERP emits node's digest('hex'); mixed case would defeat exact comparison.</summary>
    public static bool IsSha256Hex(string? s) =>
        s is { Length: 64 } && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool InRange(double? v, double min, double max) =>
        v is null || (double.IsFinite(v.Value) && v.Value >= min && v.Value <= max);
}
