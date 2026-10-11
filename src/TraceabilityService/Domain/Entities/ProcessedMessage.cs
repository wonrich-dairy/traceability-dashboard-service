namespace TraceabilityService.Domain.Entities;

/// <summary>
/// Records that a consumer has handled an event. Upstream relays are at-least-once,
/// so the same event can arrive twice; a consumer skips any event already listed
/// here for its group.
/// </summary>
public class ProcessedMessage
{
    public long Id{get; set;}

    public required string ConsumerGroup{get; set;}

    public Guid SourceEventId{get; set;}

    public required string Topic{get; set;}

    public DateTime ProcessedAt{get; set;}
}
