namespace TraceabilityService.Domain.Entities;

public enum DeterminationResult
{
    // Quality Lab's BatchCleared.
    Cleared,

    // Quality Lab's BatchFailed.
    Failed
}

/// <summary>
/// One Quality Lab determination for a batch. A supersede adds a new row and marks
/// the one it replaced, so the earlier determination is kept rather than edited.
/// </summary>
public class LabDetermination
{
    public int Id{get; set;}

    // Quality Lab's DeterminationId. Unique, so a redelivered event cannot add a
    // second row.
    public Guid DeterminationId{get; set;}

    public required string BatchId{get; set;}

    public DeterminationResult Result{get; set;}

    // When the lab made the determination, from the event's DeterminedAtUtc.
    public DateTime DeterminedAt{get; set;}

    // The user ID of the analyst, from the event's DeterminedBy.
    public required string DeterminedBy{get; set;}

    // The determination this one replaced, from the event's SupersedesDeterminationId.
    // Null for a batch's first determination. Not a foreign key: the replaced
    // determination may never have reached this service, e.g. it was published
    // before Traceability started consuming the topic.
    public Guid? SupersedesDeterminationId{get; set;}

    // Set on this row when a later determination names it in SupersedesDeterminationId.
    public bool IsSuperseded{get; set;}

    // The later determination's DeterminedAt. Null while this one is current.
    public DateTime? SupersededAt{get; set;}

    // The timeline row the determination event was recorded as. Its deviation_records
    // rows hang off that, so the out-of-spec parameters are reached through it.
    public long TimelineEventId{get; set;}
}
