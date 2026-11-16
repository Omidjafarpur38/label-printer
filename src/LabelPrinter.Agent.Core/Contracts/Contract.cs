namespace LabelPrinter.Agent.Core.Contracts;

/// <summary>How far a contract model has been checked against the companion ERP.</summary>
public enum ContractStatus
{
    /// <summary>Field names and semantics were read from ERP source at <see cref="ContractInfo.ErpBaselineCommit"/>.
    /// Not exercised against a running ERP; not production-ready.</summary>
    VerifiedAgainstErpSource,

    /// <summary>Derived from ERP documentation or an agent-side decision; the ERP may differ.</summary>
    Draft,
}

/// <summary>Marks a wire/contract model with its verification status. Nothing here is production-ready in A1.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum)]
public sealed class ContractModelAttribute(ContractStatus status, string note = "") : Attribute
{
    public ContractStatus Status { get; } = status;
    public string Note { get; } = note;
}

public static class ContractInfo
{
    /// <summary>ERP commit whose source was inspected (jeans-production-erp). A1 used 0f48373f; the agent-facing
    /// files are byte-identical at this later commit.</summary>
    public const string ErpBaselineCommit = "83a936a3573b89dda95990d04576e8d3b38cd266";

    /// <summary>Exact string the ERP negotiates and stamps on envelopes (SERVER_CONTRACTS = ['0.1']).
    /// The ERP documentation calls the same contract "0.1.0-draft"; the wire value is "0.1".</summary>
    public const string V0_1 = "0.1";

    /// <summary>Versions this agent core accepts. Negotiation in the ERP is exact string intersection.</summary>
    public static readonly IReadOnlyList<string> Supported = [V0_1];

    // Limits. Source noted per value; "draft" limits are defensive agent-side bounds.
    public const long MaxPayloadBytes = 8 * 1024 * 1024; // ERP output.ts MAX_PAYLOAD_BYTES
    public const int MaxCopies = 5000;                   // ERP print.service.ts MAX_COPIES
    public const int MaxPages = 5000;                    // ERP sets pages = copies for test prints (docs propose 2000)
    public const int MaxDeliveryAttempts = 5;            // ERP print.service.ts MAX_DELIVERY_ATTEMPTS
    public const int MinDpi = 100, MaxDpi = 1200;        // ERP controller printer schema
    public const int MaxIdempotencyKeyLength = 128;      // ERP migration 0246 varchar(128)
    public static readonly TimeSpan MaxAuthorizationWindow = TimeSpan.FromHours(24); // draft (ERP docs §3; code uses 1h)
}

// Printer language, render mode, transport and verification status are separate axes on purpose:
// a profile id names a (language, render mode) pair; transport belongs to the spool target;
// verification is evidence about a physical device and is never implied by the other three.

public enum PrinterLanguage { Tspl, Zpl }

public enum RenderMode { Bitmap1Bit }

public enum Transport { SpoolerRaw }

public enum VerificationStatus { Unverified, Verified }

[ContractModel(ContractStatus.VerifiedAgainstErpSource, "ids from ERP output.ts OUTPUT_PROFILE")]
public sealed record OutputProfile(string Id, PrinterLanguage Language, RenderMode RenderMode, VerificationStatus Verification)
{
    /// <summary>Initial profiles only. Bitmap-only is the current ERP encoder scope, not a product limit.
    /// Both are Unverified: no physical print has been observed by this project.</summary>
    public static readonly IReadOnlyList<OutputProfile> Initial =
    [
        new("tspl-bitmap-v1", PrinterLanguage.Tspl, RenderMode.Bitmap1Bit, VerificationStatus.Unverified),
        new("zpl-bitmap-v1", PrinterLanguage.Zpl, RenderMode.Bitmap1Bit, VerificationStatus.Unverified),
    ];

    public static OutputProfile? Find(string id) => Initial.FirstOrDefault(p => p.Id == id);

    public static string WireName(PrinterLanguage language) => language switch
    {
        PrinterLanguage.Tspl => "tspl",
        PrinterLanguage.Zpl => "zpl",
        _ => throw new ArgumentOutOfRangeException(nameof(language)),
    };
}

[ContractModel(ContractStatus.VerifiedAgainstErpSource, "print.service.ts envelope().output")]
public sealed record JobOutput(
    string Language,
    string Profile,
    string ContentType,
    long SizeBytes,
    string Sha256,
    int Pages,
    int Dpi,
    double? MediaWidthMm,
    double? MediaHeightMm,
    double? GapMm);

/// <summary>
/// Job envelope as returned by POST /jobs/claim. The tenant is never sent to the agent: it is implied by the
/// credential and therefore by <see cref="AgentId"/>, which the ERP issues per (tenant, install).
/// <see cref="DeliveryAttempt"/> and <see cref="LeaseUntil"/> change per delivery; everything else is immutable.
/// </summary>
[ContractModel(ContractStatus.VerifiedAgainstErpSource,
    "print.service.ts envelope(); templateId/templateVersion from the docs are not emitted by the ERP")]
public sealed record JobEnvelope(
    Guid JobId,
    string IdempotencyKey,
    string ContractVersion,
    Guid AgentId,
    Guid PrinterId,
    Guid ProfileId,
    int ProfileVersion,
    string Kind,
    JobOutput Output,
    int Copies,
    DateTimeOffset CreatedAt,
    DateTimeOffset AuthorizationExpiresAt,
    DateTimeOffset AgentMustNotSubmitAfter,
    string CorrelationId,
    Guid? ReprintOf,
    int DeliveryAttempt,
    DateTimeOffset LeaseUntil);

[ContractModel(ContractStatus.VerifiedAgainstErpSource, "print.service.ts reconcile()")]
public enum ReconcileAction
{
    /// <summary>ERP: "resume". The ERP also returns this for a locally Unknown job; the agent must only ever
    /// treat it as permission to continue a job whose local state is Accepted (never submitted).</summary>
    Resume,
    /// <summary>ERP: "abandon" (job missing, terminal, or cancel requested on the server).</summary>
    Abandon,
    /// <summary>ERP: "report_events" with fromSeq.</summary>
    ReportEvents,
}
