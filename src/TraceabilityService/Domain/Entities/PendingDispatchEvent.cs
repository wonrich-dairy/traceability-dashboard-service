namespace TraceabilityService.Domain.Entities;

/// <summary>
/// A Processing event for a dispatch that has no batch here yet, e.g. a hold raised at
/// unload, before the milk is allocated: Processing then puts the dispatch number in BatchId.
/// Kept until a batch with that DispatchRef exists, then applied to it and removed.
/// </summary>
public class PendingDispatchEvent
{
    public long Id{get; set;}

    // The MCC dispatch number the event was raised against, e.g. DN-20260919-01.
    public required string DispatchNumber{get; set;}

    // The eventType header as received, e.g. ProcessingHoldRaisedEvent, so the event can be
    // read back as its contract type.
    public required string EventType{get; set;}

    // The upstream EventId. Unique, so a redelivered event cannot be kept twice.
    public Guid SourceEventId{get; set;}

    public required string Topic{get; set;}

    // The event body as received.
    public required string Payload{get; set;}

    public DateTime ReceivedAt{get; set;}
}
