namespace TraceabilityService.Domain.Entities;

public enum Checkpoint
{
    Mcc,
    Intake,
    Processing,
    Lab
}

/// <summary>
/// One upstream event as it applies to a batch, in the order things happened.
/// </summary>
public class TimelineEvent
{
    public long Id{get; set;}

    public required string BatchId{get; set;}

    public Checkpoint Checkpoint{get; set;}

    // The upstream event name, e.g. ProcessingStageRecorded. A string rather than
    // an enum so a new upstream event type does not need a migration here.
    public required string EventType{get; set;}

    // When it happened upstream, not when this service received it.
    public DateTime OccurredAt{get; set;}

    // Null for events raised by a system rather than a person.
    public string? RecordedBy{get; set;}

    public bool IsDeviation{get; set;}

    // The event body as received, as JSON. Measurements live here rather than
    // in columns because each checkpoint reports a different set.
    public required string Payload{get; set;}

    // The upstream EventId. Unique, so replaying an event cannot add a second row.
    public Guid SourceEventId{get; set;}

    // The upstream SchemaVersion, e.g. v1.
    public required string ContractVersion{get; set;}
}
