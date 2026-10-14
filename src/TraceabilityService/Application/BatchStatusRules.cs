using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// What has been stored for a batch so far, as far as its status is concerned. The
/// handlers build it from the batch's lab_determinations and timeline_events after
/// storing each event.
/// </summary>
public sealed record StoredBatchFacts
{
    // The result of the batch's active (not superseded) lab determination, if any.
    public DeterminationResult? ActiveDetermination { get; init; }

    // HoldIds from ProcessingHoldRaised and ProcessingHoldResolved events.
    public IReadOnlyCollection<string> HoldsRaised { get; init; } = [];

    public IReadOnlyCollection<string> HoldsResolved { get; init; } = [];

    public bool ProcessingCompleted { get; init; }
}

/// <summary>
/// A batch's status, worked out from everything stored for it rather than step by step from
/// the previous status (SCRUM-130). Events can arrive in any order, so this gives the same
/// answer whichever order they came in, and resolving a hold needs no saved "status before
/// the hold": the status falls back to whatever the other events say. Pure, so the rules are
/// unit-tested without a database.
/// </summary>
public static class BatchStatusRules
{
    public static BatchStatus StatusOf(StoredBatchFacts facts) => facts switch
    {
        // The lab's decision outranks everything before it (SCRUM-131).
        { ActiveDetermination: DeterminationResult.Cleared } => BatchStatus.Cleared,
        { ActiveDetermination: DeterminationResult.Failed } => BatchStatus.Failed,
        _ when OpenHolds(facts).Count > 0 => BatchStatus.OnHold,
        { ProcessingCompleted: true } => BatchStatus.AwaitingLab,
        _ => BatchStatus.Processing,
    };

    /// <summary>
    /// Holds raised and not resolved. A hold whose HoldResolved arrived first is never open,
    /// even once its HoldRaised arrives.
    /// </summary>
    public static IReadOnlySet<string> OpenHolds(StoredBatchFacts facts)
    {
        var open = new HashSet<string>(facts.HoldsRaised, StringComparer.OrdinalIgnoreCase);
        open.ExceptWith(facts.HoldsResolved);
        return open;
    }
}
