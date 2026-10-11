using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Contracts;

/// <summary>
/// A batch without its history: what GET /api/batches lists.
/// </summary>
public class BatchSummaryDto
{
    public required string BatchId{get; set;}
    public string? DispatchRef{get; set;}
    public string? BatchReference{get; set;}
    public required string ProductLine{get; set;}
    public required string Facility{get; set;}
    public BatchStatus Status{get; set;}
    public TraceCompleteness Completeness{get; set;}
    public DateTime FirstEventAt{get; set;}
    public DateTime LastEventAt{get; set;}
}
