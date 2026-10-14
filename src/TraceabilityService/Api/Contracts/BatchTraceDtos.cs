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
    /// <summary>
    /// True when any checkpoint reported a deviation.
    /// </summary>
    public bool HasDeviation{get; set;}

    /// <summary>
    /// The earliest checkpoint in stage order (MCC, Intake, Processing, Lab) with a
    /// deviation, whatever the times on the deviations: a deviation at the MCC is the
    /// origin even when Processing deviated earlier by the clock. Null with no deviation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Checkpoint? OriginCheckpoint{get; set;}

    /// <summary>
    /// The first deviation at the origin checkpoint, by OccurredAt then SourceEventId.
    /// Null with no deviation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TraceDeviation? Origin{get; set;}

    /// <summary>
    /// Always four, in backward-trace order: Lab, Processing, Intake, Mcc. A checkpoint with no data
    /// is MissingData or NotYetReached, never left out.
    /// </summary>
    public List<TraceCheckpoint> Chain{get; set;} = [];

    /// <summary>
    /// Processing holds, in the order they were raised.
    /// </summary>
    public List<TraceHold> Holds{get; set;} = [];

    /// <summary>
    /// dispatch -> tanks -> consignments -> societies, in the snapshot's order. Null until
    /// the snapshot resolves.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public BatchLinksDto? Links{get; set;}

    /// <summary>
    /// Each society behind the batch once, in the order the snapshot lists them: tank by
    /// tank, consignment by consignment. Not sorted. Empty until the snapshot resolves.
    /// </summary>
    public List<SocietyLinkDto> Societies{get; set;} = [];
}

public enum TraceCheckpointStatus
{
    /// <summary>
    /// The checkpoint has data and no deviation.
    /// </summary>
    WithinSpec,

    /// <summary>
    /// The checkpoint reported at least one deviation.
    /// </summary>
    Deviation,

    /// <summary>
    /// No data, although the batch is past this point: a gap in the trace.
    /// </summary>
    MissingData,

    /// <summary>
    /// No data, and the batch has not got that far yet.
    /// </summary>
    NotYetReached
}

public class TraceCheckpoint
{
    public Checkpoint Checkpoint{get; set;}
    public TraceCheckpointStatus Status{get; set;}

    /// <summary>
    /// Earliest time the checkpoint recorded something; null when it has no data.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? FirstRecordedAt{get; set;}

    /// <summary>
    /// Latest time the checkpoint recorded something; null when it has no data.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? LastRecordedAt{get; set;}

    /// <summary>
    /// Why the data is missing, for MissingData. Null otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? MissingReason{get; set;}

    /// <summary>
    /// By OccurredAt, then SourceEventId.
    /// </summary>
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

    /// <summary>
    /// The upstream event that reported it.
    /// </summary>
    public Guid SourceEventId{get; set;}
}

public class TraceHold
{
    public required string HoldId{get; set;}

    /// <summary>
    /// From ProcessingHoldRaised; when only the ProcessingHoldResolved has arrived, the
    /// raise time and reason it carries.
    /// </summary>
    public DateTime RaisedAt{get; set;}
    public string? RaisedBy{get; set;}
    public string? Reason{get; set;}
    public string? FailedParameter{get; set;}
    public string? FailedValue{get; set;}

    /// <summary>
    /// Raised and not resolved, by BatchStatusRules.OpenHolds.
    /// </summary>
    public bool IsOpen{get; set;}

    /// <summary>
    /// From ProcessingHoldResolved; null while the hold is open.
    /// </summary>
    public DateTime? ResolvedAt{get; set;}
    public string? ResolvedBy{get; set;}
    public string? Resolution{get; set;}
}
