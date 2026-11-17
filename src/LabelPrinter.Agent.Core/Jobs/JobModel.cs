using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LabelPrinter.Agent.Core.Contracts;

namespace LabelPrinter.Agent.Core.Jobs;

/// <summary>
/// Local job identity. The ERP never tells the agent its tenant; the agent id is issued per (tenant, install),
/// so (agentId, jobId) is tenant-scoped by construction. One journal belongs to exactly one agent id.
/// </summary>
public readonly record struct JobKey(Guid AgentId, Guid JobId);

/// <summary>Local states. Wire names match the ERP's agent event states.</summary>
public enum JobState
{
    /// <summary>Durably accepted locally; proven never handed to a spooler. Safe to resume after restart.</summary>
    Accepted,
    /// <summary>Submission intent is durable; the spooler may or may not have been invoked.</summary>
    Submitting,
    /// <summary>The spooler acknowledged the job. Says nothing about paper.</summary>
    SubmittedToSpooler,
    /// <summary>Completion recorded together with explicit evidence.</summary>
    ConfirmedCompleted,
    /// <summary>Failed in circumstances where it is known that nothing was written to a spooler.</summary>
    Failed,
    /// <summary>Cancelled before any submission intent existed.</summary>
    Cancelled,
    /// <summary>Physical outcome cannot be determined. Never resubmitted automatically.</summary>
    Unknown,
}

public sealed record Evidence(string Level, string? Detail)
{
    public const string SpoolerReportedComplete = "spooler_reported_complete";
    public const string DeviceReportedComplete = "device_reported_complete";
    public static bool IsKnownLevel(string? level) => level is SpoolerReportedComplete or DeviceReportedComplete;
}

public sealed class InvalidTransitionException(JobState from, JobState to, string reason)
    : InvalidOperationException($"Transition {from} -> {to} rejected: {reason}")
{
    public JobState From { get; } = from;
    public JobState To { get; } = to;
}

public static class JobStateMachine
{
    private static readonly Dictionary<JobState, JobState[]> Allowed = new()
    {
        [JobState.Accepted] = [JobState.Submitting, JobState.Failed, JobState.Cancelled],
        [JobState.Submitting] = [JobState.SubmittedToSpooler, JobState.Failed, JobState.Unknown],
        [JobState.SubmittedToSpooler] = [JobState.ConfirmedCompleted, JobState.Unknown],
        // Unknown leaves only on positive evidence. The ERP also permits unknown -> failed; the core does not,
        // because "failed" asserts that nothing was sent and Unknown is exactly the absence of that knowledge.
        [JobState.Unknown] = [JobState.ConfirmedCompleted, JobState.SubmittedToSpooler],
        [JobState.ConfirmedCompleted] = [],
        [JobState.Failed] = [],
        [JobState.Cancelled] = [],
    };

    public static bool IsTerminal(JobState s) => s is JobState.ConfirmedCompleted or JobState.Failed or JobState.Cancelled;

    public static bool IsAllowed(JobState from, JobState to) => Allowed[from].Contains(to);

    /// <summary>Checks the transition and the evidence it must carry.</summary>
    public static void Ensure(JobState from, JobState to, Evidence? evidence, string? spoolerJobRef)
    {
        if (!IsAllowed(from, to)) throw new InvalidTransitionException(from, to, "not permitted");
        if (to == JobState.ConfirmedCompleted && !Evidence.IsKnownLevel(evidence?.Level))
            throw new InvalidTransitionException(from, to, "completion requires evidence");
        if (from == JobState.Unknown && to == JobState.SubmittedToSpooler && string.IsNullOrWhiteSpace(spoolerJobRef))
            throw new InvalidTransitionException(from, to, "requires an observed spooler job reference");
    }

    public static string WireName(JobState s) => s switch
    {
        JobState.Accepted => "accepted_by_agent",
        JobState.Submitting => "submitting",
        JobState.SubmittedToSpooler => "submitted_to_spooler",
        JobState.ConfirmedCompleted => "confirmed_completed",
        JobState.Failed => "failed",
        JobState.Cancelled => "cancelled",
        JobState.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };
}

/// <summary>
/// SHA-256 over a canonical encoding of every immutable envelope field. Delivery attempt and lease are excluded
/// because the ERP changes them on each redelivery of the same job. This is an agent-local value; it is unrelated
/// to the ERP's own requestHash column. It detects divergent content under one identity; it is not a signature.
/// </summary>
public static class ContentHash
{
    public static string Compute(JobEnvelope e)
    {
        var sb = new StringBuilder("lp-job-content-v1\n");
        void S(string name, string? v) => sb.Append(name).Append('=').Append(v?.Length ?? -1).Append(':').Append(v).Append('\n');
        void N(string name, long v) => S(name, v.ToString(CultureInfo.InvariantCulture));
        void D(string name, double? v) => S(name, v?.ToString("R", CultureInfo.InvariantCulture));
        void T(string name, DateTimeOffset v) => N(name, v.ToUnixTimeMilliseconds());
        void G(string name, Guid? v) => S(name, v?.ToString("D"));

        G("jobId", e.JobId); S("idempotencyKey", e.IdempotencyKey); S("contractVersion", e.ContractVersion);
        G("agentId", e.AgentId); G("printerId", e.PrinterId); G("profileId", e.ProfileId); N("profileVersion", e.ProfileVersion);
        S("kind", e.Kind);
        S("output.language", e.Output.Language); S("output.profile", e.Output.Profile); S("output.contentType", e.Output.ContentType);
        N("output.sizeBytes", e.Output.SizeBytes); S("output.sha256", e.Output.Sha256); N("output.pages", e.Output.Pages);
        N("output.dpi", e.Output.Dpi); D("output.mediaMm.w", e.Output.MediaWidthMm); D("output.mediaMm.h", e.Output.MediaHeightMm);
        D("output.gapMm", e.Output.GapMm);
        N("copies", e.Copies); T("createdAt", e.CreatedAt); T("authorizationExpiresAt", e.AuthorizationExpiresAt);
        T("agentMustNotSubmitAfter", e.AgentMustNotSubmitAfter); S("correlationId", e.CorrelationId); G("reprintOf", e.ReprintOf);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

/// <summary>A state change that still has to be reported to the ERP (POST /jobs/{id}/events).</summary>
public sealed record StatusEvent(
    Guid EventId,
    Guid JobId,
    int Seq,
    int Attempt,
    JobState State,
    DateTimeOffset At,
    string? ErrorCode,
    string? ErrorMessage,
    string? SpoolerJobRef,
    Evidence? Evidence);

/// <summary>A durable acceptance the ERP has not yet acknowledged (POST /jobs/{id}/accept).</summary>
public sealed record PendingAccept(Guid JobId, int Attempt);

/// <summary>Delivery to the ERP for this job is stopped after an answer that is neither success nor retryable.
/// <see cref="Count"/> consecutive blocks drive the retry backoff; any acknowledged delivery clears it.</summary>
public sealed record OutboxBlock(string Code, int Count, DateTimeOffset At);

public sealed record JobSnapshot(
    JobKey Key,
    JobState State,
    string ContentHash,
    int DeliveryAttempt,
    bool AcceptAcknowledged,
    int LastSeq,
    JobEnvelope Envelope,
    OutboxBlock? Block = null,
    DateTimeOffset UpdatedAt = default,
    string? LastErrorCode = null,
    string? SpoolerJobRef = null);
