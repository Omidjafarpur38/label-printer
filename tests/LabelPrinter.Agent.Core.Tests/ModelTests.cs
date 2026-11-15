using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Tests.Harness;

namespace LabelPrinter.Agent.Core.Tests;

public sealed class ModelValidationTests
{
    private static readonly byte[] P = Rig.Payload();
    private static JobEnvelope Valid => Rig.Envelope(P);

    private static void Rejects(JobEnvelope e, string code, string field)
    {
        var errors = EnvelopeValidator.ValidateEnvelope(e, Rig.Agent);
        Check.True(errors.Any(x => x.Code == code && x.Field == field),
            $"expected {code} on {field}, got [{string.Join(", ", errors.Select(x => x.Code + ":" + x.Field))}]");
    }

    [Test] public void ValidEnvelopeAndPayloadPass()
    {
        Check.Equal(0, EnvelopeValidator.ValidateEnvelope(Valid, Rig.Agent).Count, "envelope errors");
        Check.Equal(0, EnvelopeValidator.ValidatePayload(Valid.Output, P).Count, "payload errors");
        Check.Equal(0, EnvelopeValidator.ValidateEnvelope(
            Valid with { Output = Valid.Output with { Language = "zpl", Profile = "zpl-bitmap-v1", MediaWidthMm = null, MediaHeightMm = null, GapMm = null } },
            Rig.Agent).Count, "zpl profile with absent media");
    }

    [Test] public void UnsupportedContractVersionRejected() =>
        Rejects(Valid with { ContractVersion = "0.2" }, "CONTRACT_VERSION_UNSUPPORTED", "contractVersion");

    [Test] public void DocumentationVersionStringIsNotTheWireVersion() =>
        Rejects(Valid with { ContractVersion = "0.1.0-draft" }, "CONTRACT_VERSION_UNSUPPORTED", "contractVersion");

    [Test] public void JobForAnotherAgentRejected() =>
        Rejects(Valid with { AgentId = Guid.NewGuid() }, "AGENT_MISMATCH", "agentId");

    [Test] public void UndeclaredOutputProfileRejected()
    {
        Rejects(Valid with { Output = Valid.Output with { Profile = "escpos-v1" } }, "UNSUPPORTED", "output.profile");
        Rejects(Valid with { Output = Valid.Output with { Language = "zpl" } }, "PROFILE_LANGUAGE_MISMATCH", "output.language");
        Rejects(Valid with { Output = Valid.Output with { ContentType = "text/plain" } }, "UNSUPPORTED", "output.contentType");
    }

    [Test] public void NumericBoundsEnforced()
    {
        Rejects(Valid with { Copies = 0 }, "OUT_OF_RANGE", "copies");
        Rejects(Valid with { Copies = ContractInfo.MaxCopies + 1 }, "OUT_OF_RANGE", "copies");
        Rejects(Valid with { ProfileVersion = 0 }, "OUT_OF_RANGE", "profileVersion");
        Rejects(Valid with { DeliveryAttempt = 0 }, "OUT_OF_RANGE", "deliveryAttempt");
        Rejects(Valid with { DeliveryAttempt = ContractInfo.MaxDeliveryAttempts + 1 }, "OUT_OF_RANGE", "deliveryAttempt");
        Rejects(Valid with { Output = Valid.Output with { Pages = 0 } }, "OUT_OF_RANGE", "output.pages");
        Rejects(Valid with { Output = Valid.Output with { Dpi = 99 } }, "OUT_OF_RANGE", "output.dpi");
        Rejects(Valid with { Output = Valid.Output with { Dpi = 1201 } }, "OUT_OF_RANGE", "output.dpi");
        Rejects(Valid with { Output = Valid.Output with { MediaWidthMm = double.NaN } }, "OUT_OF_RANGE", "output.mediaMm.w");
        Rejects(Valid with { Output = Valid.Output with { MediaHeightMm = 1001 } }, "OUT_OF_RANGE", "output.mediaMm.h");
        Rejects(Valid with { IdempotencyKey = new string('k', 129) }, "OUT_OF_RANGE", "idempotencyKey");
    }

    [Test] public void PayloadSizeLimitEnforced()
    {
        Rejects(Valid with { Output = Valid.Output with { SizeBytes = 0 } }, "OUT_OF_RANGE", "output.sizeBytes");
        Rejects(Valid with { Output = Valid.Output with { SizeBytes = ContractInfo.MaxPayloadBytes + 1 } }, "OUT_OF_RANGE", "output.sizeBytes");
    }

    [Test] public void Sha256FormatEnforced()
    {
        Rejects(Valid with { Output = Valid.Output with { Sha256 = "abc" } }, "INVALID", "output.sha256");
        Rejects(Valid with { Output = Valid.Output with { Sha256 = Valid.Output.Sha256.ToUpperInvariant() } }, "INVALID", "output.sha256");
        Rejects(Valid with { Output = Valid.Output with { Sha256 = new string('g', 64) } }, "INVALID", "output.sha256");
    }

    [Test] public void PayloadMustMatchDeclaredSizeAndHash()
    {
        var shorter = P[..^1];
        Check.Equal("PAYLOAD_SIZE_MISMATCH", EnvelopeValidator.ValidatePayload(Valid.Output, shorter).Single().Code, "size");
        var altered = (byte[])P.Clone();
        altered[3] ^= 1;
        Check.Equal("PAYLOAD_HASH_MISMATCH", EnvelopeValidator.ValidatePayload(Valid.Output, altered).Single().Code, "hash");
    }

    [Test] public void TimeWindowsEnforced()
    {
        Rejects(Valid with { AuthorizationExpiresAt = Valid.CreatedAt }, "INVALID", "authorizationExpiresAt");
        Rejects(Valid with { AuthorizationExpiresAt = Valid.CreatedAt.AddHours(25), AgentMustNotSubmitAfter = Valid.CreatedAt.AddHours(1) },
            "OUT_OF_RANGE", "authorizationExpiresAt");
        Rejects(Valid with { AgentMustNotSubmitAfter = Valid.AuthorizationExpiresAt.AddSeconds(1) }, "INVALID", "agentMustNotSubmitAfter");
    }

    [Test] public void ReprintMustReferenceADifferentJob()
    {
        Rejects(Valid with { ReprintOf = Valid.JobId }, "INVALID", "reprintOf");
        Check.Equal(0, EnvelopeValidator.ValidateEnvelope(Valid with { ReprintOf = Rig.JobId(99) }, Rig.Agent).Count, "valid reprint");
    }

    [Test] public void ContentHashCoversEveryImmutableFieldAndIgnoresDeliveryMetadata()
    {
        var h = ContentHash.Compute(Valid);
        Check.Equal(64, h.Length, "hash length");
        Check.Equal(h, ContentHash.Compute(Valid with { DeliveryAttempt = 3, LeaseUntil = Valid.LeaseUntil.AddMinutes(5) }), "delivery metadata excluded");

        JobEnvelope[] changed =
        [
            Valid with { JobId = Rig.JobId(2) }, Valid with { IdempotencyKey = "other" }, Valid with { PrinterId = Guid.NewGuid() },
            Valid with { ProfileId = Guid.NewGuid() }, Valid with { ProfileVersion = 2 }, Valid with { Kind = "label" },
            Valid with { Copies = 2 }, Valid with { CreatedAt = Valid.CreatedAt.AddMilliseconds(1) },
            Valid with { AuthorizationExpiresAt = Valid.AuthorizationExpiresAt.AddSeconds(-1) },
            Valid with { AgentMustNotSubmitAfter = Valid.AgentMustNotSubmitAfter.AddSeconds(-1) },
            Valid with { CorrelationId = "x" }, Valid with { ReprintOf = Rig.JobId(9) },
            Valid with { Output = Valid.Output with { Sha256 = new string('0', 64) } },
            Valid with { Output = Valid.Output with { SizeBytes = 65 } }, Valid with { Output = Valid.Output with { Pages = 2 } },
            Valid with { Output = Valid.Output with { Dpi = 300 } }, Valid with { Output = Valid.Output with { MediaWidthMm = 51 } },
            Valid with { Output = Valid.Output with { MediaHeightMm = null } }, Valid with { Output = Valid.Output with { GapMm = 3 } },
            Valid with { Output = Valid.Output with { Language = "zpl" } }, Valid with { Output = Valid.Output with { Profile = "zpl-bitmap-v1" } },
        ];
        foreach (var e in changed) Check.True(ContentHash.Compute(e) != h, "immutable change must alter the hash");
        Check.Equal(changed.Length, changed.Select(ContentHash.Compute).Distinct().Count(), "no accidental collisions between fields");
    }

    [Test] public void ProfilesKeepLanguageRenderModeAndVerificationSeparate()
    {
        var tspl = OutputProfile.Find("tspl-bitmap-v1")!;
        Check.Equal(PrinterLanguage.Tspl, tspl.Language, "language");
        Check.Equal(RenderMode.Bitmap1Bit, tspl.RenderMode, "render mode");
        Check.True(OutputProfile.Initial.All(p => p.Verification == VerificationStatus.Unverified), "no profile claims physical verification");
    }
}

public sealed class StateMachineTests
{
    private static readonly Evidence Ev = new(Evidence.SpoolerReportedComplete, null);

    [Test] public void TransitionTableIsExactlyAsSpecified()
    {
        var allowed = new HashSet<(JobState, JobState)>
        {
            (JobState.Accepted, JobState.Submitting), (JobState.Accepted, JobState.Failed), (JobState.Accepted, JobState.Cancelled),
            (JobState.Submitting, JobState.SubmittedToSpooler), (JobState.Submitting, JobState.Failed), (JobState.Submitting, JobState.Unknown),
            (JobState.SubmittedToSpooler, JobState.ConfirmedCompleted), (JobState.SubmittedToSpooler, JobState.Unknown),
            (JobState.Unknown, JobState.ConfirmedCompleted), (JobState.Unknown, JobState.SubmittedToSpooler),
        };
        foreach (var from in Enum.GetValues<JobState>())
            foreach (var to in Enum.GetValues<JobState>())
                Check.Equal(allowed.Contains((from, to)), JobStateMachine.IsAllowed(from, to), $"{from} -> {to}");
    }

    [Test] public void UnknownNeverLeadsBackToSubmission()
    {
        Check.True(!JobStateMachine.IsAllowed(JobState.Unknown, JobState.Submitting), "unknown -> submitting");
        Check.True(!JobStateMachine.IsAllowed(JobState.Unknown, JobState.Accepted), "unknown -> accepted");
        Check.True(!JobStateMachine.IsAllowed(JobState.Unknown, JobState.Failed), "unknown -> failed needs proof the core does not have");
        Check.True(!JobStateMachine.IsAllowed(JobState.Unknown, JobState.Cancelled), "unknown -> cancelled");
    }

    [Test] public void TerminalStatesHaveNoExit()
    {
        foreach (var s in new[] { JobState.ConfirmedCompleted, JobState.Failed, JobState.Cancelled })
        {
            Check.True(JobStateMachine.IsTerminal(s), $"{s} terminal");
            Check.True(Enum.GetValues<JobState>().All(to => !JobStateMachine.IsAllowed(s, to)), $"{s} has no exit");
        }
        Check.True(!JobStateMachine.IsTerminal(JobState.Unknown), "unknown is not terminal");
    }

    [Test] public void CompletionRequiresRecognisedEvidence()
    {
        Check.Throws<InvalidTransitionException>(() => JobStateMachine.Ensure(JobState.SubmittedToSpooler, JobState.ConfirmedCompleted, null, null), "no evidence");
        Check.Throws<InvalidTransitionException>(() => JobStateMachine.Ensure(JobState.SubmittedToSpooler, JobState.ConfirmedCompleted, new Evidence("operator_said_so", null), null), "unknown level");
        Check.Throws<InvalidTransitionException>(() => JobStateMachine.Ensure(JobState.Unknown, JobState.ConfirmedCompleted, null, "ref"), "spooler ref is not completion evidence");
        JobStateMachine.Ensure(JobState.SubmittedToSpooler, JobState.ConfirmedCompleted, Ev, null);
        JobStateMachine.Ensure(JobState.Unknown, JobState.ConfirmedCompleted, new Evidence(Evidence.DeviceReportedComplete, "d"), null);
    }

    [Test] public void LeavingUnknownForSubmittedRequiresObservedSpoolerReference()
    {
        Check.Throws<InvalidTransitionException>(() => JobStateMachine.Ensure(JobState.Unknown, JobState.SubmittedToSpooler, null, " "), "blank ref");
        JobStateMachine.Ensure(JobState.Unknown, JobState.SubmittedToSpooler, null, "spool-7");
    }

    [Test] public void WireNamesMatchErpEventStates()
    {
        Check.Sequence(
            ["accepted_by_agent", "submitting", "submitted_to_spooler", "confirmed_completed", "failed", "cancelled", "unknown"],
            Enum.GetValues<JobState>().Select(JobStateMachine.WireName), "wire names");
    }
}
