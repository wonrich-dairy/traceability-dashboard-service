using System.Text.Json.Serialization;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Contracts;

/// <summary>
/// The backward trace for one batch: GET /api/batches/{batchId}/trace (SCRUM-134). Built
/// by BackwardTraceAssembler. The nullable fields are marked Never-ignore, so they are
/// written as null rather than left out even if the API is later set to drop nulls: a
/// trace never omits a checkpoint or the origin.
/// </summary>
public class BatchTraceResponse : BatchSummaryDto
{
    // True when any checkpoint reported a deviation.
    public bool HasDeviation{get; set;}

    // The earliest checkpoint in stage order (MCC, Intake, Processing, Lab) with a
    // deviation, whatever the times on the deviations: a deviation at the MCC is the
    // origin even when Processing deviated earlier by the clock. Null with no deviation.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Checkpoint? OriginCheckpoint{get; set;}

    // The first deviation at the origin checkpoint, by OccurredAt then SourceEventId.
    // Null with no deviation.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TraceDeviation? Origin{get; set;}

    // Always four, in stage order: Mcc, Intake, Processing, Lab. A checkpoint with no data
    // is MissingData or NotYetReached, never left out.
    public List<TraceCheckpoint> Chain{get; set;} = [];

    // Processing holds, in the order they were raised.
    public List<TraceHold> Holds{get; set;} = [];

    // dispatch -> tanks -> consignments -> societies, in the snapshot's order. Null until
    // the snapshot resolves.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public BatchLinksDto? Links{get; set;}

    // Each society behind the batch once, in the order the snapshot lists them: tank by
    // tank, consignment by consignment. Not sorted. Empty until the snapshot resolves.
    public List<SocietyLinkDto> Societies{get; set;} = [];
}

public enum TraceCheckpointStatus
{
    // The checkpoint has data and no deviation.
    WithinSpec,

    // The checkpoint reported at least one deviation.
    Deviation,

    // No data, although the batch is past this point: a gap in the trace.
    MissingData,

    // No data, and the batch has not got that far yet.
    NotYetReached
}

public class TraceCheckpoint
{
    public Checkpoint Checkpoint{get; set;}
    public TraceCheckpointStatus Status{get; set;}

    // Earliest and latest time the checkpoint recorded something; null when it has no data.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? FirstRecordedAt{get; set;}
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? LastRecordedAt{get; set;}

    // Why the data is missing, for MissingData. Null otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? MissingReason{get; set;}

    // By OccurredAt, then SourceEventId.
    public List<TraceDeviation> Deviations{get; set;} = [];
}

public class TraceDeviation
{
    public Checkpoint Checkpoint{get; set;}
    public DateTime OccurredAt{get; set;}
    public string? ReasonCode{get; set;}
    public string? Parameter{get; set;}
    public decimal? ObservedValue{get; set;}
    public decimal? Limit{get; set;}
    public string? Description{get; set;}

    // The upstream event that reported it.
    public Guid SourceEventId{get; set;}
}

public class TraceHold
{
    public required string HoldId{get; set;}

    // From ProcessingHoldRaised; when only the ProcessingHoldResolved has arrived, the
    // raise time and reason it carries.
    public DateTime RaisedAt{get; set;}
    public string? RaisedBy{get; set;}
    public string? Reason{get; set;}
    public string? FailedParameter{get; set;}
    public string? FailedValue{get; set;}

    // Raised and not resolved, by BatchStatusRules.OpenHolds.
    public bool IsOpen{get; set;}

    // From ProcessingHoldResolved; null while the hold is open.
    public DateTime? ResolvedAt{get; set;}
    public string? ResolvedBy{get; set;}
    public string? Resolution{get; set;}
}
