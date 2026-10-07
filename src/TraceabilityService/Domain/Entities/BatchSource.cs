namespace TraceabilityService.Domain.Entities;

public class BatchSource
{
    public int Id{get; set;}

    public required string BatchId{get; set;}

    public required string ConsignmentReference{get; set;}
}