using LabelPrinter.Agent.Core.Erp;

namespace LabelPrinter.Agent.Core.Runtime;

public enum RunnerStop
{
    Cancelled,
    /// <summary>The ERP refused the credential (revoked, expired, unknown). Network activity stops; the journal
    /// is kept; pairing again is an explicit operator action.</summary>
    CredentialRejected,
    /// <summary>No common contract version (HTTP 426).</summary>
    ContractUnsupported,
}

public sealed record RunnerOptions
{
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan BackoffCap { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ForbiddenDelay { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ThrottleFloor { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ThrottleCap { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>ERP credentials live 90 days; rotate well before that.</summary>
    public TimeSpan RotateAfter { get; init; } = TimeSpan.FromDays(60);
    public int ClaimMax { get; init; } = 1;
}

/// <summary>
/// The agent's only loop. One iteration: heartbeat when due, credential rotation when due, one sync cycle, then
/// a delay chosen from the outcome. Failures never cause an immediate retry: no usable answer → exponential
/// backoff with full jitter; 429 → Retry-After (bounded); 403 → a long pause; 401/426 → stop.
/// Storage failures are not handled here: they propagate so the host can stop and reopen the journal.
/// </summary>
public sealed class AgentRunner(
    JobLedger ledger, HttpErpClient http, ErpSync sync, IdentityStore identities, AgentIdentity identity,
    TimeProvider clock, Action<string> log, RunnerOptions? options = null, Random? random = null)
{
    private readonly RunnerOptions _o = options ?? new RunnerOptions();
    private readonly Random _random = random ?? Random.Shared;
    private DateTimeOffset _nextHeartbeat = DateTimeOffset.MinValue;
    private DateTimeOffset _credentialSetAt = identity.CredentialSetAt;
    private int _failures;

    /// <summary>Delay chosen by the last iteration (exposed for tests and diagnostics).</summary>
    public TimeSpan LastDelay { get; private set; }

    public RunnerStop Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (RunOnce() is { } stop) return stop;
            if (ct.WaitHandle.WaitOne(LastDelay)) break;
        }
        return RunnerStop.Cancelled;
    }

    /// <summary>One iteration without sleeping. Returns a stop reason, or null to continue after <see cref="LastDelay"/>.</summary>
    public RunnerStop? RunOnce()
    {
        ErpException? failure = null;
        var unavailable = false;
        var didWork = false;
        try
        {
            if (clock.GetUtcNow() >= _nextHeartbeat)
            {
                foreach (var id in http.Heartbeat().CancelJobIds)
                    if (ledger.Find(id) is not null && ledger.Cancel(id)) { didWork = true; log($"job {id}: cancelled on ERP request"); }
                _nextHeartbeat = clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Clamp(identity.HeartbeatSec, 10, 300));
                ConfirmCredential();
            }
            RotateIfDue();
        }
        catch (ErpException ex) { failure = ex; }
        catch (ErpUnavailableException) { unavailable = true; }

        if (failure is null && !unavailable)
        {
            var report = sync.RunCycle(claim: true, claimMax: _o.ClaimMax);
            foreach (var note in report.Notes) log($"{note.Code}{(note.JobId is { } j ? " job " + j : "")}{(note.Detail is null ? "" : " " + note.Detail)}");
            failure = report.ErpFailure;
            unavailable = report.ErpUnavailable && failure is null;
            didWork |= report.DidWork;
            if (failure is null && !unavailable) ConfirmCredential();
        }

        if (failure is not null)
        {
            switch (failure.Status)
            {
                case 401:
                    if (identities.HasPending)
                    {
                        // The rotated credential was not accepted (e.g. its 24 h window passed). The previous
                        // one is still valid until a rotated one is first used, so fall back to it once.
                        identities.DiscardPending();
                        log("rotated credential refused; falling back to the previous one");
                        LastDelay = _o.IdleDelay;
                        return null;
                    }
                    log($"credential refused by ERP ({failure.ErrorCode}); stopping network activity");
                    return RunnerStop.CredentialRejected;
                case 426:
                    log("no common contract version with ERP; stopping");
                    return RunnerStop.ContractUnsupported;
                case 429:
                    var asked = http.LastThrottle?.RetryAfter ?? Backoff();
                    LastDelay = Clamp(asked, _o.ThrottleFloor, _o.ThrottleCap);
                    log($"rate limited; waiting {LastDelay.TotalSeconds:F0}s");
                    return null;
                case 403:
                    LastDelay = _o.ForbiddenDelay;
                    log($"ERP refused ({failure.ErrorCode}); waiting {LastDelay.TotalSeconds:F0}s");
                    return null;
                default:
                    LastDelay = Backoff();
                    log($"ERP error {failure.Status} {failure.ErrorCode}; waiting {LastDelay.TotalSeconds:F0}s");
                    return null;
            }
        }

        if (unavailable)
        {
            LastDelay = Backoff();
            log($"ERP unreachable; waiting {LastDelay.TotalSeconds:F0}s");
            return null;
        }

        _failures = 0;
        LastDelay = didWork ? TimeSpan.FromMilliseconds(200) : _o.IdleDelay;
        return null;
    }

    /// <summary>Full jitter: uniform in (0, min(cap, base × 2^failures)], never below 0.5 s.</summary>
    private TimeSpan Backoff()
    {
        var ceiling = Math.Min(_o.BackoffCap.TotalSeconds, _o.BackoffBase.TotalSeconds * Math.Pow(2, Math.Min(_failures, 16)));
        _failures++;
        return TimeSpan.FromSeconds(Math.Max(0.5, _random.NextDouble() * ceiling));
    }

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;

    private void ConfirmCredential()
    {
        if (!identities.HasPending) return;
        identities.PromotePending(clock.GetUtcNow()); // an authenticated call succeeded with it: the ERP committed it
        _credentialSetAt = clock.GetUtcNow();
        log("credential rotated");
    }

    private void RotateIfDue()
    {
        if (identities.HasPending || clock.GetUtcNow() - _credentialSetAt < _o.RotateAfter) return;
        identities.SavePending(http.RotateCredential());
        log("credential rotation requested");
    }
}
