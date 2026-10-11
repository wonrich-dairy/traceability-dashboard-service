namespace TraceabilityService.Domain.Entities;

/// <summary>
/// Per-day, per-product-line, per-facility batch counts by status, for the QC
/// dashboard. Kept up to date as events arrive so the dashboard does not count
/// batches on every request.
/// </summary>
public class DailyQualityAggregate
{
    public int Id{get; set;}

    // The production day the batch code refers to.
    public DateOnly Date{get; set;}

    public required string ProductLine{get; set;}

    public required string Facility{get; set;}

    // Every batch, whatever its status. The four counts below add up to it.
    public int BatchCount{get; set;}

    // Not decided and not held: Chilling, Processing or AwaitingLab.
    public int PendingCount{get; set;}

    public int ClearedCount{get; set;}

    public int FailedCount{get; set;}

    public int OnHoldCount{get; set;}

    // All deviations for these batches. Broken down by reason in
    // DailyReasonCodeAggregate.
    public int DeviationCount{get; set;}

    public DateTime UpdatedAt{get; set;}
}
