using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

public enum LookupResult
{
    // The MCC and Intake Service resolved the whole tree.
    Found,

    // It has no batch from that dispatch note, or no batch with that reference. Asking again
    // will not change that.
    NotFound,

    // It has the batch, but part of the tree could not be resolved there, e.g. a tank with no
    // pours recorded yet. A partial tree is never stored, and the gap may be filled later.
    Incomplete,

    // No usable answer: unreachable, a timeout, an error status, or a body that cannot be read.
    Failed
}

/// <summary>What one lookup came back with. <see cref="Payload"/> is set only when <see cref="Result"/> is Found.</summary>
public sealed record UpstreamLookup(LookupResult Result, string? Reason = null, UpstreamSnapshotPayload? Payload = null)
{
    public static UpstreamLookup Found(UpstreamSnapshotPayload payload) => new(LookupResult.Found, Payload: payload);

    public static UpstreamLookup NotFound(string reason) => new(LookupResult.NotFound, reason);

    public static UpstreamLookup Incomplete(string reason) => new(LookupResult.Incomplete, reason);

    public static UpstreamLookup Failed(string reason) => new(LookupResult.Failed, reason);
}

/// <summary>What to store on the snapshot after an attempt.</summary>
public sealed record ResolutionDecision(ResolutionStatus Status, string? FailureReason, DateTime? NextAttemptAt);

/// <summary>
/// How an upstream lookup moves a snapshot on (SCRUM-132). Pure, so the rules are unit-tested
/// without HTTP or a database:
/// <list type="bullet">
/// <item>Found: Resolved.</item>
/// <item>NotFound: Unavailable at once, with the reason.</item>
/// <item>Incomplete or Failed: PendingRetry with the reason, due again after a backoff that doubles
/// from <see cref="FirstRetryDelay"/> up to <see cref="MaxRetryDelay"/>; Unavailable once the
/// attempts reach Upstream:MaxAttempts, saying the retries ran out and why the last one failed.</item>
/// </list>
/// </summary>
public static class UpstreamResolutionRules
{
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    // upstream_snapshots.FailureReason is varchar(500).
    public const int MaxReasonLength = 500;

    /// <param name="attemptCount">Attempts so far, this one included.</param>
    public static ResolutionDecision Decide(UpstreamLookup lookup, int attemptCount, int maxAttempts, DateTime now) =>
        lookup.Result switch
        {
            LookupResult.Found => new(ResolutionStatus.Resolved, null, null),
            LookupResult.NotFound => new(ResolutionStatus.Unavailable, Fit(lookup.Reason), null),
            _ when attemptCount >= maxAttempts => new(
                ResolutionStatus.Unavailable,
                Fit($"Retries ran out after {attemptCount} attempts. Last: {lookup.Reason}"),
                null),
            _ => new(ResolutionStatus.PendingRetry, Fit(lookup.Reason), now + RetryDelay(attemptCount)),
        };

    /// <summary>The wait after the given failed attempt: 30s, 1m, 2m, 4m, ... capped at 30m.</summary>
    public static TimeSpan RetryDelay(int attemptCount)
    {
        var doublings = Math.Clamp(attemptCount - 1, 0, 30);
        var delay = FirstRetryDelay * Math.Pow(2, doublings);
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }

    private static string? Fit(string? reason) =>
        reason is { Length: > MaxReasonLength } ? reason[..(MaxReasonLength - 1)] + "…" : reason;
}
