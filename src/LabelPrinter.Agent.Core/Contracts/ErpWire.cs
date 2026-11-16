using System.Text.Json;
using System.Text.RegularExpressions;

namespace LabelPrinter.Agent.Core.Contracts;

/// <summary>
/// Error body produced by the ERP global error handler (app.ts setErrorHandler). It is flat, unlike the nested
/// { error: { code, retryable } } shape in the ERP contract document:
/// { error: JS error name, message, trace_id, error_code, user_friendly_message_fa [, details] }.
/// <c>error</c> is usually just "Error"; the stable code is <c>error_code</c>. Routes that answer without the
/// handler (events jobId mismatch) omit error_code. Draft until observed on the wire.
/// </summary>
[ContractModel(ContractStatus.Draft, "shape read from app.ts; not observed over HTTP")]
public sealed record ErpErrorBody(string? Error, string? Message, string? ErrorCode, string? TraceId)
{
    /// <summary>Never throws: a body that is not the expected JSON object yields all-null fields.</summary>
    public static ErpErrorBody Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Empty;
            string? Text(string name) =>
                doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new ErpErrorBody(Text("error"), Text("message"), Text("error_code"), Text("trace_id"));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static readonly ErpErrorBody Empty = new(null, null, null, null);
}

/// <summary>
/// Shape of an agent credential as generated and parsed by the ERP (agent.service.ts): a fixed scheme prefix,
/// a base64url key id of 16–48 characters, a dot, and a base64url secret of 32–64 characters. This type only
/// recognises the shape; it does not store, transmit or verify anything, and never prints the secret.
/// </summary>
[ContractModel(ContractStatus.VerifiedAgainstErpSource, "newCredential() and CRED_RE in agent.service.ts")]
public sealed partial class AgentCredential
{
    public const string Scheme = "lpa1";

    [GeneratedRegex(@"^lpa1_([A-Za-z0-9_-]{16,48})\.([A-Za-z0-9_-]{32,64})$")]
    private static partial Regex Shape();

    private readonly string _value;

    private AgentCredential(string value, string keyId)
    {
        _value = value;
        KeyId = keyId;
    }

    /// <summary>Public identifier; safe to log.</summary>
    public string KeyId { get; }

    public static bool TryParse(string? text, out AgentCredential? credential)
    {
        credential = null;
        if (text is null) return false;
        var m = Shape().Match(text);
        if (!m.Success) return false;
        credential = new AgentCredential(text, m.Groups[1].Value);
        return true;
    }

    /// <summary>Value for the Authorization header. Callers must not log it.</summary>
    public string ToAuthorizationHeader() => "Bearer " + _value;

    public override string ToString() => $"{Scheme}_{KeyId}.<redacted>";
}
