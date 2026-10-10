namespace TraceabilityService.Domain.Entities;

/// <summary>
/// One deviation found in a batch, with the timeline event that reported it.
/// </summary>
public class DeviationRecord
{
    public int Id{get; set;}

    public required string BatchId{get; set;}

    public long TimelineEventId{get; set;}

    // Copied from the timeline event so deviations can be listed and counted
    // without joining back to it.
    public Checkpoint Checkpoint{get; set;}

    public DateTime OccurredAt{get; set;}

    // Why it is a deviation, e.g. TemperatureHigh, PhLow, SensoryAbnormal. The
    // dashboard groups deviations by it. A string rather than an enum so a new
    // upstream reason does not need a migration here. Null when the upstream event
    // only set its deviation flag.
    public string? ReasonCode{get; set;}

    // What was out of range, e.g. TemperatureC.
    public string? Parameter{get; set;}

    // The reading and the limit it broke, e.g. 12.40 against 4.00. Exact decimals,
    // so 3.85 is stored as 3.85. Null for checks with no number, such as sensory
    // grades.
    public decimal? ObservedValue{get; set;}

    public decimal? Limit{get; set;}

    public string? Description{get; set;}
}
