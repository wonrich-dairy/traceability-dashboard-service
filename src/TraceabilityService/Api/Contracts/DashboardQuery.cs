using Microsoft.AspNetCore.Mvc;

namespace TraceabilityService.Api.Contracts;

/// <summary>
/// The query string every dashboard endpoint takes. All optional. The names are set
/// explicitly so a 400's errors are keyed by them (from, not From).
/// </summary>
public class DashboardQuery
{
    /// <summary>
    /// Facility code, e.g. FACTORY-01. Leave out for every facility.
    /// </summary>
    [FromQuery(Name = "facility")]
    public string? Facility{get; set;}

    /// <summary>
    /// One of the six product line codes (FM, FLM, SY, SK, DY, CD), in any case. Leave out for
    /// all of them.
    /// </summary>
    [FromQuery(Name = "productLine")]
    public string? ProductLine{get; set;}

    /// <summary>
    /// First production day, inclusive. Default: 29 days before to, so the range is 30 days.
    /// </summary>
    [FromQuery(Name = "from")]
    public DateOnly? From{get; set;}

    /// <summary>
    /// Last production day, inclusive. Default: today in Asia/Colombo. At most one year after from.
    /// </summary>
    [FromQuery(Name = "to")]
    public DateOnly? To{get; set;}
}
