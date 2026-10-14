namespace TraceabilityService.Domain.Entities;

/// <summary>Daily gate-test totals for a society, populated by ConsignmentTested events.</summary>
public class DailySocietyAggregate
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public required string SocietyCode { get; set; }
    public string? SocietyName { get; set; }
    public int TotalConsignments { get; set; }
    public int AcceptedCount { get; set; }
    public int RejectedCount { get; set; }
    public decimal FatPercentTotal { get; set; }
    public decimal SnfTotal { get; set; }
    public int MarginalCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}
