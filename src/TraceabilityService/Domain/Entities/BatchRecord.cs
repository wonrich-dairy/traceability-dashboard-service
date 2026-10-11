namespace TraceabilityService.Domain.Entities;

public enum BatchStatus
{
    Chilling,
    Processing,
    OnHold,
    AwaitingLab,

    // Quality Lab's BatchCleared.
    Cleared,

    // Quality Lab's BatchFailed.
    Failed
}

public enum TraceCompleteness
{
    // At least one checkpoint has not reported yet, or the upstream snapshot is
    // not resolved, so the trace has gaps.
    Partial,

    // Every checkpoint has reported and the upstream snapshot is resolved.
    Complete
}

/// <summary>
/// One row per batch: the head of the read model that the timeline, upstream
/// snapshot and deviations hang off.
/// </summary>
public class BatchRecord
{
    public int Id{get; set;}

    // Batch code [day]-[product]-[letter], e.g. 262-FM-A. The business key every
    // other batch table points at.
    public required string BatchId{get; set;}

    // MCC dispatch number, e.g. DN-20260919-01. Null until an event that carries
    // it has arrived.
    public string? DispatchRef{get; set;}

    // Product code from the batch code: FM, FLM, SY, SK, DY or CD.
    public required string ProductLine{get; set;}

    // The factory the batch was made at, as in the token's facility claim, e.g.
    // FACTORY-01. Every dashboard query filters on it.
    public required string Facility{get; set;}

    public BatchStatus Status{get; set;}

    public TraceCompleteness Completeness{get; set;}

    // OccurredAt of the earliest and latest timeline events seen for the batch.
    // Events can arrive out of order, so these are min/max, not first/last received.
    public DateTime FirstEventAt{get; set;}

    public DateTime LastEventAt{get; set;}

    public ICollection<TimelineEvent> TimelineEvents{get; set;} = [];

    public UpstreamSnapshot? UpstreamSnapshot{get; set;}

    public ICollection<DeviationRecord> Deviations{get; set;} = [];
}
