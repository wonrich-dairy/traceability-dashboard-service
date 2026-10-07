namespace TraceabilityService.Api.Contracts;

public class RecentFailureDto
{
    public required string BatchId{get; set;}
    public DateOnly ProductionDate{get; set;}
}