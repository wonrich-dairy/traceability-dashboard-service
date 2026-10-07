using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Contracts;
public class DashboardSummaryDto
{
    // The production-date window these figures cover, both ends inclusive.
    // Always populated, including when the caller supplied neither bound.
    public DateOnly From {get; set;}
    public DateOnly To {get; set;}

    public int TotalBatches {get; set;}
    public int InProgress {get; set;}
    public int Passed {get; set;}
    public int Failed {get; set;}

    // Passed / (Passed + Failed), as a fraction of 1. Null rather than zero when
    // nothing has been decided yet: no rate exists, and a 0 would read as 0%.
    public double? PassRate {get; set;}

    public List<RecentFailureDto> RecentFailures {get; set;} = [];

    public Dictionary<Status, int> ByStatus {get; set;} = [];
}
