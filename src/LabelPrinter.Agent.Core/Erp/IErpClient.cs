using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Jobs;

namespace LabelPrinter.Agent.Core.Erp;

// Transport-neutral port for the ERP agent API (/api/label-agent/v1). A2 ships no HTTP implementation: the
// only implementation is the in-memory mock in the test project. Shapes follow print.service.ts.

[ContractModel(ContractStatus.VerifiedAgainstErpSource, "claim()")]
public sealed record ClaimResponse(IReadOnlyList<JobEnvelope> Jobs, IReadOnlyList<Guid> CancelJobIds, bool Paused);

[ContractModel(ContractStatus.VerifiedAgainstErpSource, "payload controller: body + X-Payload-Sha256")]
public sealed record PayloadResponse(byte[] Bytes, string? Sha256Header);

/// <summary><see cref="State"/> is the server's state after the call, as a wire name.</summary>
[ContractModel(ContractStatus.VerifiedAgainstErpSource, "accept()")]
public sealed record AcceptResponse(string State, bool CancelRequested, bool Duplicate);

[ContractModel(ContractStatus.VerifiedAgainstErpSource, "applyEvents() results")]
public sealed record EventResult(Guid EventId, bool Applied, bool Duplicate, bool Stale);

[ContractModel(ContractStatus.VerifiedAgainstErpSource, "applyEvents()")]
public sealed record EventsResponse(IReadOnlyList<EventResult> Results, string? State, int LastSeq);

[ContractModel(ContractStatus.VerifiedAgainstErpSource, "reconcile() request item")]
public sealed record LocalJobReport(Guid JobId, string State, int LastSeq);

[ContractModel(ContractStatus.VerifiedAgainstErpSource, "reconcile() directive")]
public sealed record ReconcileDirective(Guid JobId, ReconcileAction Action, int? FromSeq);

/// <summary>The ERP answered with an error status. <see cref="ErrorCode"/> may be null (see ErpErrorBody).</summary>
public sealed class ErpException(int status, string? errorCode) : Exception($"ERP error {status} {errorCode}")
{
    public int Status { get; } = status;
    public string? ErrorCode { get; } = errorCode;
}

/// <summary>No usable answer: the request may or may not have been processed by the ERP.</summary>
public sealed class ErpUnavailableException(string message) : Exception(message);

public interface IErpClient
{
    public const int MaxEventsPerRequest = 20;   // controller eventsBody
    public const int MaxReconcileJobs = 100;     // controller reconcileBody
    public const int MaxClaim = 5;               // controller claimBody

    ClaimResponse Claim(int max);
    PayloadResponse GetPayload(Guid jobId);
    AcceptResponse Accept(Guid jobId, int attempt);
    EventsResponse PostEvents(Guid jobId, IReadOnlyList<StatusEvent> events);
    IReadOnlyList<ReconcileDirective> Reconcile(IReadOnlyList<LocalJobReport> jobs);
}
