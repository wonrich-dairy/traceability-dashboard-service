using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// Decides what a Quality Lab determination does to a batch (SCRUM-131): whether it
/// becomes the batch's current determination, which stored one it replaces, the batch's
/// new status, and how the daily aggregates move. Pure, so the rules are unit-tested
/// without a database or Kafka; the consumer applies the result in one transaction.
/// </summary>
/// <remarks>
/// Determinations can arrive out of order, e.g. one replayed from a dead-letter topic
/// after the one that superseded it. Only the current determination is ever counted in
/// the aggregates, so a late arrival that is already superseded changes nothing.
/// </remarks>
public static class DeterminationResolver
{
    public static DeterminationResolution Resolve(
        BatchStatus currentStatus,
        IReadOnlyCollection<DeterminationFacts> existing,
        DeterminationFacts incoming)
    {
        // A redelivery: it is already stored, and already counted if it was current.
        if (existing.Any(e => e.DeterminationId == incoming.DeterminationId))
        {
            return new DeterminationResolution
            {
                IsDuplicate = true,
                IsActive = false,
                NewStatus = currentStatus,
            };
        }

        // Newest first, in case earlier data left two current rows.
        var current = existing.Where(e => !e.IsSuperseded).MaxBy(e => e.DeterminedAt);
        var replacedBy = existing.FirstOrDefault(e => e.SupersedesDeterminationId == incoming.DeterminationId);
        var replaces = incoming.SupersedesDeterminationId is { } replacedId
            ? existing.FirstOrDefault(e => e.DeterminationId == replacedId)
            : null;

        // Current unless a stored determination replaced it, or a stored one is newer:
        // then one in between has not arrived yet, and that one replaced it.
        var isActive = replacedBy is null
            && (current is null
                || current.DeterminationId == replaces?.DeterminationId
                || incoming.DeterminedAt > current.DeterminedAt);

        var supersede = new List<SupersededDetermination>();

        // The row the incoming determination names, replaced when the incoming one was made.
        // When that row is already marked (it was replaced via a gap in the chain), this
        // fills in the time that was not known then.
        if (replaces is not null && (isActive || replaces.IsSuperseded))
        {
            supersede.Add(new SupersededDetermination(replaces.DeterminationId, incoming.DeterminedAt));
        }

        // A newer determination replaces the current one even when it names another: the
        // one it names never got here, so when the current one was replaced is not known.
        if (isActive && current is not null && current.DeterminationId != replaces?.DeterminationId)
        {
            supersede.Add(new SupersededDetermination(current.DeterminationId, SupersededAt: null));
        }

        if (!isActive)
        {
            // Never counted, so nothing to undo: store it marked and leave the batch alone.
            return new DeterminationResolution
            {
                IsActive = false,
                SupersededAt = replacedBy?.DeterminedAt,
                Supersede = supersede,
                NewStatus = currentStatus,
            };
        }

        var newStatus = incoming.Result == DeterminationResult.Cleared ? BatchStatus.Cleared : BatchStatus.Failed;

        return new DeterminationResolution
        {
            IsActive = true,
            Supersede = supersede,
            NewStatus = newStatus,
            Changes = AggregateChanges.Between(
                BucketOf(currentStatus), current?.ReasonCodes ?? [],
                BucketOf(newStatus), incoming.ReasonCodes),
        };
    }

    /// <summary>Which daily_quality_aggregates count a batch in this status adds to.</summary>
    public static StatusBucket BucketOf(BatchStatus status) => status switch
    {
        BatchStatus.Cleared => StatusBucket.Cleared,
        BatchStatus.Failed => StatusBucket.Failed,
        BatchStatus.OnHold => StatusBucket.OnHold,
        _ => StatusBucket.Pending,
    };
}

/// <summary>
/// The status counts on daily_quality_aggregates. Pending is Chilling, Processing or
/// AwaitingLab: not decided and not held.
/// </summary>
public enum StatusBucket
{
    Pending,
    Cleared,
    Failed,
    OnHold
}

/// <summary>
/// What the resolver needs to know about one determination. ReasonCodes has one entry per
/// lab deviation the determination produces, with the code it is counted under
/// (<see cref="LabDeviation.AggregateReasonCode"/>), so the aggregates always match the
/// deviation_records rows. The handler builds these from the batch's lab_determinations
/// rows with their deviation rows, and from the incoming event via <see cref="LabDeviations"/>.
/// </summary>
public sealed record DeterminationFacts(
    Guid DeterminationId,
    DeterminationResult Result,
    DateTime DeterminedAt,
    Guid? SupersedesDeterminationId,
    IReadOnlyList<string> ReasonCodes,
    bool IsSuperseded = false);

/// <summary>
/// A stored determination to mark IsSuperseded. SupersededAt is null when the
/// determination that replaced it has not arrived, so when is not known.
/// </summary>
public sealed record SupersededDetermination(Guid DeterminationId, DateTime? SupersededAt);

public sealed record DeterminationResolution
{
    // The incoming determination is already stored. Nothing changes.
    public bool IsDuplicate { get; init; }

    // Whether the incoming determination becomes the batch's current one. False means
    // it arrives already superseded: store it with IsSuperseded set.
    public required bool IsActive { get; init; }

    // When the incoming determination was replaced, if it arrives already superseded and
    // the determination that replaced it is stored. Otherwise null.
    public DateTime? SupersededAt { get; init; }

    // Stored determinations to mark superseded.
    public IReadOnlyList<SupersededDetermination> Supersede { get; init; } = [];

    // Unchanged unless the incoming determination is the current one.
    public required BatchStatus NewStatus { get; init; }

    public AggregateChanges Changes { get; init; } = AggregateChanges.None;
}

/// <summary>
/// How the batch's day, product line and facility rows in the aggregate tables move.
/// Only non-zero entries are listed. DeviationCount on daily_quality_aggregates moves
/// with LabDeviations, since it is the sum of the four checkpoint counts.
/// </summary>
public sealed record AggregateChanges(
    IReadOnlyDictionary<StatusBucket, int> StatusCounts,
    int LabDeviations,
    IReadOnlyDictionary<string, int> ReasonCodes)
{
    public static AggregateChanges None { get; } = new(
        new Dictionary<StatusBucket, int>(),
        0,
        new Dictionary<string, int>(StringComparer.Ordinal));

    // Each entry is one lab deviation, counted under its code exactly as given, so an
    // unfamiliar code is counted under its own name.
    internal static AggregateChanges Between(
        StatusBucket from, IReadOnlyList<string> removedReasons,
        StatusBucket to, IReadOnlyList<string> addedReasons)
    {
        var statusCounts = new Dictionary<StatusBucket, int>();
        if (from != to)
        {
            statusCounts[from] = -1;
            statusCounts[to] = 1;
        }

        var reasonCodes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var code in removedReasons)
        {
            reasonCodes[code] = reasonCodes.GetValueOrDefault(code) - 1;
        }
        foreach (var code in addedReasons)
        {
            reasonCodes[code] = reasonCodes.GetValueOrDefault(code) + 1;
        }

        // A code on both the replaced and the new determination stays as it was.
        foreach (var code in reasonCodes.Where(r => r.Value == 0).Select(r => r.Key).ToList())
        {
            reasonCodes.Remove(code);
        }

        return new AggregateChanges(statusCounts, addedReasons.Count - removedReasons.Count, reasonCodes);
    }
}
