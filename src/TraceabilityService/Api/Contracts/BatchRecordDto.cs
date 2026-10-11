using System.Text.Json;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Contracts;

/// <summary>
/// The full record for one batch: GET /api/batches/{batchId}. Built by
/// BatchRecordAssembler.
/// </summary>
public class BatchRecordDto : BatchSummaryDto
{
    // Null when the batch has no upstream snapshot row yet.
    public UpstreamStatusDto? Upstream{get; set;}

    // Always four, in stage order: Mcc, Intake, Processing, Lab.
    public List<CheckpointSectionDto> Checkpoints{get; set;} = [];

    // dispatch -> tanks -> consignments -> societies. Null until the snapshot resolves.
    public BatchLinksDto? Links{get; set;}
}

public enum CheckpointState
{
    // The checkpoint has data.
    Recorded,

    // No data, and the batch has not got that far yet.
    NotYetReached,

    // No data, although the batch is past this point: a gap in the trace.
    NoDataReceived
}

public class CheckpointSectionDto
{
    public Checkpoint Checkpoint{get; set;}
    public CheckpointState State{get; set;}

    // Earliest and latest OccurredAt among the records; null when there are none.
    public DateTime? FirstRecordedAt{get; set;}
    public DateTime? LastRecordedAt{get; set;}

    // In the order they happened.
    public List<CheckpointRecordDto> Records{get; set;} = [];
    public List<DeviationDto> Deviations{get; set;} = [];
}

public class CheckpointRecordDto
{
    // What was recorded: the upstream event type for Processing and Lab, or
    // DispatchCreated, ConsignmentRegistered or ArrivalScreened from the snapshot.
    public required string Kind{get; set;}
    public DateTime OccurredAt{get; set;}
    public string? RecordedBy{get; set;}

    // The record's details as a JSON object: the event payload, or the snapshot part.
    public JsonElement Data{get; set;}

    // The upstream event, for records that came from one.
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

    // Why it has not resolved: not found upstream, or the service failed.
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
