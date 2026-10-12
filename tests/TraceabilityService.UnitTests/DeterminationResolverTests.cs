using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.UnitTests;

/// <summary>
/// What a Quality Lab determination does to a batch (SCRUM-131): the Definition of Done's
/// cases, plus redelivery and arrival order. Out-of-order cases run through
/// <see cref="Batch"/>, which applies each resolution the way the consumer does, so they
/// check the end state rather than one call.
/// </summary>
public class DeterminationResolverTests
{
    private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = new("cccccccc-0000-0000-0000-000000000003");

    private static readonly DateTime Day = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_first_cleared_determination_moves_the_batch_from_pending_to_cleared()
    {
        var result = DeterminationResolver.Resolve(BatchStatus.AwaitingLab, [], Cleared(A, minute: 0));

        Assert.True(result.IsActive);
        Assert.Empty(result.Supersede);
        Assert.Equal(BatchStatus.Cleared, result.NewStatus);
        Assert.Equal(Counts((StatusBucket.Pending, -1), (StatusBucket.Cleared, 1)), result.Changes.StatusCounts);
        Assert.Equal(0, result.Changes.LabDeviations);
        Assert.Empty(result.Changes.ReasonCodes);
    }

    [Fact]
    public void A_first_failed_determination_counts_each_reason_as_a_lab_deviation()
    {
        var result = DeterminationResolver.Resolve(
            BatchStatus.AwaitingLab, [], Failed(A, minute: 0, reasons: ["LOW_FAT", "LOW_PH"]));

        Assert.True(result.IsActive);
        Assert.Equal(BatchStatus.Failed, result.NewStatus);
        Assert.Equal(Counts((StatusBucket.Pending, -1), (StatusBucket.Failed, 1)), result.Changes.StatusCounts);
        Assert.Equal(2, result.Changes.LabDeviations);
        Assert.Equal(Reasons(("LOW_FAT", 1), ("LOW_PH", 1)), result.Changes.ReasonCodes);
    }

    [Fact]
    public void A_cleared_determination_superseding_a_failed_one_undoes_the_failure()
    {
        var a = Failed(A, minute: 0, reasons: ["LOW_FAT", "LOW_PH"]);
        var b = Cleared(B, minute: 30, supersedes: A);

        var result = DeterminationResolver.Resolve(BatchStatus.Failed, [a], b);

        Assert.True(result.IsActive);
        Assert.Equal([new SupersededDetermination(A, b.DeterminedAt)], result.Supersede);
        Assert.Equal(BatchStatus.Cleared, result.NewStatus);
        Assert.Equal(Counts((StatusBucket.Failed, -1), (StatusBucket.Cleared, 1)), result.Changes.StatusCounts);
        Assert.Equal(-2, result.Changes.LabDeviations);
        Assert.Equal(Reasons(("LOW_FAT", -1), ("LOW_PH", -1)), result.Changes.ReasonCodes);
    }

    [Fact]
    public void A_supersede_that_arrives_before_the_determination_it_replaces_becomes_current()
    {
        var result = DeterminationResolver.Resolve(BatchStatus.AwaitingLab, [], Cleared(B, minute: 30, supersedes: A));

        Assert.True(result.IsActive);
        Assert.Empty(result.Supersede);
        Assert.Equal(BatchStatus.Cleared, result.NewStatus);
        Assert.Equal(Counts((StatusBucket.Pending, -1), (StatusBucket.Cleared, 1)), result.Changes.StatusCounts);
    }

    [Fact]
    public void The_replaced_determination_arriving_late_is_stored_superseded_and_changes_nothing()
    {
        var b = Cleared(B, minute: 30, supersedes: A);

        var result = DeterminationResolver.Resolve(
            BatchStatus.Cleared, [b], Failed(A, minute: 0, reasons: ["LOW_FAT", "LOW_PH"]));

        // A was never counted, so there is nothing to undo.
        Assert.False(result.IsActive);
        Assert.Equal(b.DeterminedAt, result.SupersededAt);
        Assert.Empty(result.Supersede);
        Assert.Equal(BatchStatus.Cleared, result.NewStatus);
        Assert.Same(AggregateChanges.None, result.Changes);
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("ACB")]
    [InlineData("BAC")]
    [InlineData("BCA")]
    [InlineData("CAB")]
    [InlineData("CBA")]
    public void In_a_chain_only_the_last_determination_is_current_whatever_the_arrival_order(string order)
    {
        // C supersedes B supersedes A.
        var determinations = new Dictionary<char, DeterminationFacts>
        {
            ['A'] = Failed(A, minute: 0, reasons: ["LOW_PH"]),
            ['B'] = Failed(B, minute: 30, supersedes: A, reasons: ["LOW_FAT"]),
            ['C'] = Cleared(C, minute: 60, supersedes: B),
        };
        var batch = new Batch();

        foreach (var name in order)
        {
            batch.Receive(determinations[name]);
        }

        Assert.Equal([C], batch.Current.Select(d => d.DeterminationId));
        Assert.Equal(BatchStatus.Cleared, batch.Status);

        // Each replaced determination ends up with the time it was replaced, even when the
        // determination that replaced it arrived after it was marked.
        Assert.Equal(determinations['B'].DeterminedAt, batch.SupersededAt[A]);
        Assert.Equal(determinations['C'].DeterminedAt, batch.SupersededAt[B]);

        // Net effect on the aggregates is the same as C alone: one pending batch cleared.
        Assert.Equal(Counts((StatusBucket.Pending, -1), (StatusBucket.Cleared, 1)), batch.StatusCounts);
        Assert.Equal(0, batch.LabDeviations);
        Assert.Empty(batch.ReasonCodes);
    }

    [Fact]
    public void An_unknown_reason_code_is_counted_under_its_own_name()
    {
        var result = DeterminationResolver.Resolve(BatchStatus.AwaitingLab, [], Failed(A, minute: 0, reasons: ["XYZ"]));

        Assert.True(result.IsActive);
        Assert.Equal(Reasons(("XYZ", 1)), result.Changes.ReasonCodes);
        Assert.Equal(1, result.Changes.LabDeviations);
    }

    [Fact]
    public void A_redelivered_determination_changes_nothing()
    {
        var a = Failed(A, minute: 0, reasons: ["LOW_FAT"]);

        var result = DeterminationResolver.Resolve(BatchStatus.Failed, [a], a);

        Assert.True(result.IsDuplicate);
        Assert.Empty(result.Supersede);
        Assert.Equal(BatchStatus.Failed, result.NewStatus);
        Assert.Same(AggregateChanges.None, result.Changes);
    }

    [Fact]
    public void Superseding_a_failure_with_a_failure_moves_only_the_reasons_that_changed()
    {
        var a = Failed(A, minute: 0, reasons: ["LOW_FAT", "LOW_PH"]);
        var b = Failed(B, minute: 30, supersedes: A, reasons: ["LOW_FAT"]);

        var result = DeterminationResolver.Resolve(BatchStatus.Failed, [a], b);

        Assert.Equal(BatchStatus.Failed, result.NewStatus);
        Assert.Empty(result.Changes.StatusCounts);
        Assert.Equal(-1, result.Changes.LabDeviations);
        Assert.Equal(Reasons(("LOW_PH", -1)), result.Changes.ReasonCodes);
    }

    [Fact]
    public void A_newer_determination_replaces_the_current_one_across_a_gap_in_the_chain()
    {
        // B never arrived: C names B, but A is the current one.
        var a = Failed(A, minute: 0, reasons: ["LOW_PH"]);

        var result = DeterminationResolver.Resolve(BatchStatus.Failed, [a], Cleared(C, minute: 60, supersedes: B));

        Assert.True(result.IsActive);
        Assert.Equal([new SupersededDetermination(A, SupersededAt: null)], result.Supersede);
        Assert.Equal(Counts((StatusBucket.Failed, -1), (StatusBucket.Cleared, 1)), result.Changes.StatusCounts);
        Assert.Equal(Reasons(("LOW_PH", -1)), result.Changes.ReasonCodes);
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("BA")]
    public void A_supersede_link_wins_over_timestamps_from_skewed_clocks(string order)
    {
        // Quality Lab instances a second apart: B replaced A but carries an earlier time.
        var determinations = new Dictionary<char, DeterminationFacts>
        {
            ['A'] = Failed(A, minute: 1, reasons: ["LOW_PH"]),
            ['B'] = Cleared(B, minute: 0, supersedes: A),
        };
        var batch = new Batch();

        foreach (var name in order)
        {
            batch.Receive(determinations[name]);
        }

        Assert.Equal([B], batch.Current.Select(d => d.DeterminationId));
        Assert.Equal(BatchStatus.Cleared, batch.Status);
        Assert.Equal(Counts((StatusBucket.Pending, -1), (StatusBucket.Cleared, 1)), batch.StatusCounts);
        Assert.Empty(batch.ReasonCodes);
    }

    [Fact]
    public void A_held_batch_leaves_the_on_hold_count()
    {
        var result = DeterminationResolver.Resolve(BatchStatus.OnHold, [], Cleared(A, minute: 0));

        Assert.Equal(Counts((StatusBucket.OnHold, -1), (StatusBucket.Cleared, 1)), result.Changes.StatusCounts);
    }

    private static DeterminationFacts Cleared(Guid id, int minute, Guid? supersedes = null) =>
        new(id, DeterminationResult.Cleared, Day.AddMinutes(minute), supersedes, []);

    private static DeterminationFacts Failed(Guid id, int minute, string[] reasons, Guid? supersedes = null) =>
        new(id, DeterminationResult.Failed, Day.AddMinutes(minute), supersedes, reasons);

    private static Dictionary<StatusBucket, int> Counts(params (StatusBucket Bucket, int Change)[] changes) =>
        changes.ToDictionary(c => c.Bucket, c => c.Change);

    private static Dictionary<string, int> Reasons(params (string Code, int Change)[] changes) =>
        changes.ToDictionary(c => c.Code, c => c.Change);

    /// <summary>
    /// One batch's stored determinations and the aggregate changes applied so far, updated
    /// from each resolution the way the consumer updates the database.
    /// </summary>
    private sealed class Batch
    {
        private readonly List<DeterminationFacts> _stored = [];

        public BatchStatus Status { get; private set; } = BatchStatus.AwaitingLab;
        public Dictionary<Guid, DateTime?> SupersededAt { get; } = [];
        public Dictionary<StatusBucket, int> StatusCounts { get; } = [];
        public Dictionary<string, int> ReasonCodes { get; } = [];
        public int LabDeviations { get; private set; }

        public IEnumerable<DeterminationFacts> Current => _stored.Where(d => !d.IsSuperseded);

        public void Receive(DeterminationFacts incoming)
        {
            var result = DeterminationResolver.Resolve(Status, _stored, incoming);
            if (result.IsDuplicate)
            {
                return;
            }

            foreach (var replaced in result.Supersede)
            {
                var index = _stored.FindIndex(d => d.DeterminationId == replaced.DeterminationId);
                _stored[index] = _stored[index] with { IsSuperseded = true };
                SupersededAt[replaced.DeterminationId] = replaced.SupersededAt;
            }

            _stored.Add(incoming with { IsSuperseded = !result.IsActive });
            if (!result.IsActive)
            {
                SupersededAt[incoming.DeterminationId] = result.SupersededAt;
            }

            Status = result.NewStatus;
            LabDeviations += result.Changes.LabDeviations;
            Add(StatusCounts, result.Changes.StatusCounts);
            Add(ReasonCodes, result.Changes.ReasonCodes);
        }

        private static void Add<TKey>(Dictionary<TKey, int> totals, IReadOnlyDictionary<TKey, int> changes)
            where TKey : notnull
        {
            foreach (var (key, change) in changes)
            {
                totals[key] = totals.GetValueOrDefault(key) + change;
                if (totals[key] == 0)
                {
                    totals.Remove(key);
                }
            }
        }
    }
}
