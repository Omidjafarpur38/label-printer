using LabelPrinter.Agent.Core.Contracts;

namespace LabelPrinter.Agent.Core.Spooling;

public enum SpoolOutcome
{
    /// <summary>The spooler took the job. This is not evidence of physical completion.</summary>
    Accepted,
    /// <summary>The spooler refused and it is certain that no byte was written.</summary>
    RejectedNothingWritten,
    /// <summary>Anything else: partial write, timeout, lost reply. The job becomes Unknown.</summary>
    Indeterminate,
}

public sealed record SpoolResult(SpoolOutcome Outcome, string? SpoolerJobRef = null, string? ErrorCode = null);

/// <summary>
/// One approved payload, submitted once. There is deliberately no copy count here: the ERP embeds copies in the
/// printer commands, so any multiplication by the agent or the spooler would over-print.
/// </summary>
public sealed record SpoolRequest(
    Guid JobId,
    Guid PrinterId,
    PrinterLanguage Language,
    string OutputProfile,
    Transport Transport,
    ReadOnlyMemory<byte> Payload);

/// <summary>
/// Implementations must report <see cref="SpoolOutcome.RejectedNothingWritten"/> only when that is certain,
/// and may throw; an exception is treated as <see cref="SpoolOutcome.Indeterminate"/>.
/// </summary>
public interface ISpooler
{
    SpoolResult Submit(SpoolRequest request);
}
