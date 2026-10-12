using Microsoft.AspNetCore.Mvc;

namespace TraceabilityService.Api.Contracts;

/// <summary>
/// The query string every dashboard endpoint takes. All optional. The names are set
/// explicitly so a 400's errors are keyed by them (from, not From).
/// </summary>
public class DashboardQuery
{
    // Null for every facility.
    [FromQuery(Name = "facility")]
    public string? Facility{get; set;}

    // One of the six product line codes; null for all of them.
    [FromQuery(Name = "productLine")]
    public string? ProductLine{get; set;}

    // Production days, both ends inclusive. Default: the 30 days ending today.
    [FromQuery(Name = "from")]
    public DateOnly? From{get; set;}

    [FromQuery(Name = "to")]
    public DateOnly? To{get; set;}
}
