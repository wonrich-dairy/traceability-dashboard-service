using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Contracts;
public class BatchTraceResponseDto
{
    public required string BatchId{get; set;}
    public DateOnly ProductionDate{get; set;}
    public Status CurrentStatus{get; set;}
    public Result? FinalLabOutcome{get; set;}

    public List<CheckpointDto> Checkpoints {get; set;} = [];
    public List<string> SourceConsignments {get; set;} = [];
}

