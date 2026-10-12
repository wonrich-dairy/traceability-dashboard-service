using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.UnitTests;

/// <summary>
/// A batch's status from its stored events (SCRUM-130): each event alone, every arrival order
/// of a hold being raised and resolved and processing completing, and overlapping holds.
/// </summary>
public class BatchStatusRulesTests
{
    [Fact]
    public void A_batch_with_only_allocation_or_stage_events_is_processing()
    {
        Assert.Equal(BatchStatus.Processing, BatchStatusRules.StatusOf(new StoredBatchFacts()));
    }

    [Fact]
    public void A_raised_hold_alone_puts_the_batch_on_hold()
    {
        Assert.Equal(BatchStatus.OnHold, BatchStatusRules.StatusOf(new StoredBatchFacts { HoldsRaised = ["H1"] }));
    }

    [Fact]
    public void A_resolved_hold_alone_never_shows_as_open()
    {
        Assert.Equal(BatchStatus.Processing, BatchStatusRules.StatusOf(new StoredBatchFacts { HoldsResolved = ["H1"] }));
    }

    [Fact]
    public void Processing_completed_alone_awaits_the_lab()
    {
        Assert.Equal(BatchStatus.AwaitingLab, BatchStatusRules.StatusOf(new StoredBatchFacts { ProcessingCompleted = true }));
    }

    [Theory]
    [InlineData(DeterminationResult.Cleared, BatchStatus.Cleared)]
    [InlineData(DeterminationResult.Failed, BatchStatus.Failed)]
    public void An_active_determination_alone_sets_the_result(DeterminationResult result, BatchStatus expected)
    {
        Assert.Equal(expected, BatchStatusRules.StatusOf(new StoredBatchFacts { ActiveDetermination = result }));
    }

    [Theory]
    [InlineData(DeterminationResult.Cleared, BatchStatus.Cleared)]
    [InlineData(DeterminationResult.Failed, BatchStatus.Failed)]
    public void The_lab_result_outranks_an_open_hold_and_completion(DeterminationResult result, BatchStatus expected)
    {
        var facts = new StoredBatchFacts { ActiveDetermination = result, HoldsRaised = ["H1"], ProcessingCompleted = true };

        Assert.Equal(expected, BatchStatusRules.StatusOf(facts));
    }

    [Theory]
    // R = HoldRaised, S = HoldResolved (same hold), C = ProcessingCompleted. The expected
    // status after each arrival, in order.
    [InlineData("RSC", BatchStatus.OnHold, BatchStatus.Processing, BatchStatus.AwaitingLab)]
    [InlineData("RCS", BatchStatus.OnHold, BatchStatus.OnHold, BatchStatus.AwaitingLab)]
    [InlineData("SRC", BatchStatus.Processing, BatchStatus.Processing, BatchStatus.AwaitingLab)]
    [InlineData("SCR", BatchStatus.Processing, BatchStatus.AwaitingLab, BatchStatus.AwaitingLab)]
    [InlineData("CRS", BatchStatus.AwaitingLab, BatchStatus.OnHold, BatchStatus.AwaitingLab)]
    [InlineData("CSR", BatchStatus.AwaitingLab, BatchStatus.AwaitingLab, BatchStatus.AwaitingLab)]
    public void Every_arrival_order_of_raise_resolve_and_complete_ends_awaiting_the_lab(
        string order, BatchStatus afterFirst, BatchStatus afterSecond, BatchStatus afterThird)
    {
        var raised = new List<string>();
        var resolved = new List<string>();
        var completed = false;
        var statuses = new List<BatchStatus>();

        foreach (var arrival in order)
        {
            switch (arrival)
            {
                case 'R': raised.Add("H1"); break;
                case 'S': resolved.Add("H1"); break;
                case 'C': completed = true; break;
            }

            statuses.Add(BatchStatusRules.StatusOf(new StoredBatchFacts
            {
                HoldsRaised = [.. raised],
                HoldsResolved = [.. resolved],
                ProcessingCompleted = completed,
            }));
        }

        Assert.Equal([afterFirst, afterSecond, afterThird], statuses);
    }

    [Fact]
    public void With_two_overlapping_holds_the_batch_stays_on_hold_until_both_are_resolved()
    {
        var bothRaised = new StoredBatchFacts { HoldsRaised = ["H1", "H2"] };
        var firstResolved = bothRaised with { HoldsResolved = ["H1"] };
        var bothResolved = bothRaised with { HoldsResolved = ["H1", "H2"] };

        Assert.Equal(BatchStatus.OnHold, BatchStatusRules.StatusOf(bothRaised));
        Assert.Equal(BatchStatus.OnHold, BatchStatusRules.StatusOf(firstResolved));
        Assert.Equal(["H2"], BatchStatusRules.OpenHolds(firstResolved));
        Assert.Equal(BatchStatus.Processing, BatchStatusRules.StatusOf(bothResolved));
        Assert.Equal(BatchStatus.AwaitingLab, BatchStatusRules.StatusOf(bothResolved with { ProcessingCompleted = true }));
    }

    [Fact]
    public void Resolving_one_hold_does_not_close_another()
    {
        var facts = new StoredBatchFacts { HoldsRaised = ["H1"], HoldsResolved = ["H2"] };

        Assert.Equal(BatchStatus.OnHold, BatchStatusRules.StatusOf(facts));
    }
}
