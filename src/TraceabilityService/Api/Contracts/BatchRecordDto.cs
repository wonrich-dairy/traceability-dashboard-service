using System.Text.Json;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Contracts;

/// <summary>
/// The full record for one batch: GET /api/batches/{batchId}. Built by
/// BatchRecordAssembler.
/// </summary>
public class BatchRecordDto : BatchSummaryDto
{
    /// <summary>
    /// Null when the batch has no upstream snapshot row yet.
    /// </summary>
    public UpstreamStatusDto? Upstream{get; set;}

    /// <summary>
    /// Always four, in stage order: Mcc, Intake, Processing, Lab.
    /// </summary>
    public List<CheckpointSectionDto> Checkpoints{get; set;} = [];

    /// <summary>
    /// dispatch -> tanks -> consignments -> societies. Null until the snapshot resolves.
    /// </summary>
    public BatchLinksDto? Links{get; set;}
}

public enum CheckpointState
{
    /// <summary>
    /// The checkpoint has data.
    /// </summary>
    Recorded,

    /// <summary>
    /// No data, and the batch has not got that far yet.
    /// </summary>
    NotYetReached,

    /// <summary>
    /// No data, although the batch is past this point: a gap in the trace.
    /// </summary>
    NoDataReceived
}

public class CheckpointSectionDto
{
    public Checkpoint Checkpoint{get; set;}
    public CheckpointState State{get; set;}

    /// <summary>
    /// Earliest OccurredAt among the records; null when there are none.
    /// </summary>
    public DateTime? FirstRecordedAt{get; set;}

    /// <summary>
    /// Latest OccurredAt among the records; null when there are none.
    /// </summary>
    public DateTime? LastRecordedAt{get; set;}

    /// <summary>
    /// In the order they happened.
    /// </summary>
    public List<CheckpointRecordDto> Records{get; set;} = [];

    /// <summary>
    /// In the order they happened.
    /// </summary>
    public List<DeviationDto> Deviations{get; set;} = [];
}

public class CheckpointRecordDto
{
    /// <summary>
    /// What was recorded: the upstream event type for Processing and Lab, or
    /// DispatchCreated, ConsignmentRegistered or ArrivalScreened from the snapshot.
    /// </summary>
    public required string Kind{get; set;}
    public DateTime OccurredAt{get; set;}
    public string? RecordedBy{get; set;}

    /// <summary>
    /// The record's details as a JSON object: the event payload, or the snapshot part.
    /// </summary>
    public JsonElement Data{get; set;}

    /// <summary>
    /// The upstream event, for records that came from one.
    /// </summary>
    public Guid? SourceEventId{get; set;}
}

public class DeviationDto
{
    public DateTime OccurredAt{get; set;}
    public string? ReasonCode{get; set;}
    public string? Parameter{get; set;}
    public decimal? ObservedValue{get; set;}
    public decimal? Limit{get; set;}
    public string? Description{get; set;}
}

public class UpstreamStatusDto
{
    public ResolutionStatus Status{get; set;}
    public int AttemptCount{get; set;}
    public DateTime? LastAttemptAt{get; set;}
    public DateTime? ResolvedAt{get; set;}

    /// <summary>
    /// Why it has not resolved: not found upstream, or the service failed.
    /// </summary>
    public string? FailureReason{get; set;}
}

public class BatchLinksDto
{
    public required DispatchLinkDto Dispatch{get; set;}
    public List<TankLinkDto> Tanks{get; set;} = [];
}

public class DispatchLinkDto
{
    public required string Reference{get; set;}
    public DateTime DispatchedAt{get; set;}
    public string? BowserRegistration{get; set;}
    public decimal TotalLitres{get; set;}
}

public class TankLinkDto
{
    public required string Code{get; set;}
    public decimal QuantityDrawnLitres{get; set;}
    public List<ConsignmentLinkDto> Consignments{get; set;} = [];
}

public class ConsignmentLinkDto
{
    public required string Reference{get; set;}
    public decimal VolumeLitres{get; set;}
    public required SocietyLinkDto Society{get; set;}
}

public class SocietyLinkDto
{
    public required string Code{get; set;}
    public string? Name{get; set;}
}
