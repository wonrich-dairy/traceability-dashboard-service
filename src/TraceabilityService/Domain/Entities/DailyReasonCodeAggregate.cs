namespace TraceabilityService.Domain.Entities;

/// <summary>
/// Per-day, per-product-line, per-facility deviation counts by reason code, for the
/// QC dashboard's breakdown of what went wrong.
/// </summary>
public class DailyReasonCodeAggregate
{
    // Deviations whose upstream event gave no reason are counted under this code,
    // because the reason code is part of this table's key and cannot be null.
    public const string Unspecified = "Unspecified";

    public int Id{get; set;}

    public DateOnly Date{get; set;}

    public required string ProductLine{get; set;}

    public required string Facility{get; set;}

    public required string ReasonCode{get; set;}

    public int DeviationCount{get; set;}

    public DateTime UpdatedAt{get; set;}
}
