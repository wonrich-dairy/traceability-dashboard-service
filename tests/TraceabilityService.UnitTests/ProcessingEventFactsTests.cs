using ProcessingService.Domain.Events;
using TraceabilityService.Application;

namespace TraceabilityService.UnitTests;

/// <summary>
/// What each of Processing's five events means for the read model (SCRUM-130): the timeline
/// name and time, who recorded it, holds, completion and deviations.
/// </summary>
public class ProcessingEventFactsTests
{
    private static readonly DateTime Start = new(2026, 10, 12, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Allocation_is_timed_by_when_the_milk_was_allocated_and_has_no_deviation()
    {
        var facts = ProcessingEventFacts.Of(new MilkAllocatedToMixingTankEvent
        {
            AllocatedAtUtc = Start, AllocatedBy = "tech-1", TimestampUtc = Start.AddMinutes(5),
        });

        Assert.Equal(("MilkAllocatedToMixingTank", Start, "tech-1"), (facts.EventType, facts.OccurredAt, facts.RecordedBy));
        Assert.Null(facts.Deviation);
        Assert.False(facts.Completed);
    }

    [Fact]
    public void A_deviating_stage_records_its_end_temperature()
    {
        var facts = ProcessingEventFacts.Of(new ProcessingStageRecordedEvent
        {
            StageType = "Pasteuriser", StartTimeUtc = Start, EndTimeUtc = Start.AddMinutes(30),
            EndTemperatureC = 70.50m, RecordedBy = "tech-2", IsDeviation = true,
        });

        Assert.Equal(("ProcessingStageRecorded", Start, "tech-2"), (facts.EventType, facts.OccurredAt, facts.RecordedBy));
        Assert.Equal(new ProcessingDeviation("EndTemperatureC", 70.50m, "Pasteuriser end temperature out of range"), facts.Deviation);
    }

    [Fact]
    public void A_stage_within_range_has_no_deviation()
    {
        var facts = ProcessingEventFacts.Of(new ProcessingStageRecordedEvent { StartTimeUtc = Start, EndTemperatureC = 73.00m });

        Assert.Null(facts.Deviation);
    }

    [Fact]
    public void Completion_marks_the_batch_completed_without_counting_its_stages_deviations_again()
    {
        var facts = ProcessingEventFacts.Of(new ProcessingCompletedEvent
        {
            CompletedAtUtc = Start, CompletedBy = "tech-3", IsDeviation = true, HasDeviation = true,
        });

        Assert.Equal(("ProcessingCompleted", Start, "tech-3"), (facts.EventType, facts.OccurredAt, facts.RecordedBy));
        Assert.True(facts.Completed);
        Assert.Null(facts.Deviation);
    }

    [Theory]
    [InlineData("2.8 < 3.0 min", "2.8")]
    [InlineData("8.13 < 8.0 min (Fat 3.2 + CLR 27.10)", "8.13")]
    [InlineData("Not OK", null)]
    [InlineData("Positive - clotted on boiling - overrides all", null)]
    [InlineData(null, null)]
    public void A_raised_hold_records_its_failed_parameter_and_the_reading_its_value_starts_with(string? failedValue, string? expected)
    {
        var facts = ProcessingEventFacts.Of(new ProcessingHoldRaisedEvent
        {
            HoldId = "H1", RaisedAtUtc = Start, RaisedBy = "qa-1", IsDeviation = true,
            Reason = "Failed: FatPercent " + failedValue, FailedParameter = "FatPercent", FailedValue = failedValue,
        });

        Assert.Equal(("ProcessingHoldRaised", Start, "qa-1", "H1"), (facts.EventType, facts.OccurredAt, facts.RecordedBy, facts.HoldRaised));
        Assert.NotNull(facts.Deviation);
        Assert.Equal("FatPercent", facts.Deviation.Parameter);
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), facts.Deviation.ObservedValue);
        Assert.Equal("Failed: FatPercent " + failedValue, facts.Deviation.Description);
    }

    [Fact]
    public void A_resolved_hold_is_timed_by_its_resolution_and_names_the_hold()
    {
        var facts = ProcessingEventFacts.Of(new ProcessingHoldResolvedEvent
        {
            HoldId = "H1", RaisedAtUtc = Start, ResolvedAtUtc = Start.AddHours(1), ResolvedBy = "qa-2",
        });

        Assert.Equal(("ProcessingHoldResolved", Start.AddHours(1), "qa-2", "H1"),
            (facts.EventType, facts.OccurredAt, facts.RecordedBy, facts.HoldResolved));
        Assert.Null(facts.Deviation);
    }
}
