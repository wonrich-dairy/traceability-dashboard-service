using TraceabilityService.Observability;

namespace TraceabilityService.UnitTests;

public class TraceabilityMetricsTests
{
    [Fact]
    public void Projected_event_counts_and_moves_the_newest_timestamp_forward_only()
    {
        var newer = DateTimeOffset.UtcNow;
        var older = newer.AddHours(-1);
        var before = TraceabilityMetrics.EventsConsumed.WithLabels("unit.test.topic").Value;

        TraceabilityMetrics.RecordEventProjected("unit.test.topic", newer);
        TraceabilityMetrics.RecordEventProjected("unit.test.topic", older);

        Assert.Equal(before + 2, TraceabilityMetrics.EventsConsumed.WithLabels("unit.test.topic").Value);
        Assert.Equal(newer.ToUnixTimeSeconds(), TraceabilityMetrics.NewestProjectedEventTimestamp.Value);
    }

    [Fact]
    public void Dead_letter_is_counted_per_source_topic()
    {
        var before = TraceabilityMetrics.EventsDeadLettered.WithLabels("unit.dlq.topic").Value;

        TraceabilityMetrics.RecordDeadLetter("unit.dlq.topic");

        Assert.Equal(before + 1, TraceabilityMetrics.EventsDeadLettered.WithLabels("unit.dlq.topic").Value);
    }
}
