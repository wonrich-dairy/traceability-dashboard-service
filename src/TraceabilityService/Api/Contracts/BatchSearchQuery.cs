using Microsoft.AspNetCore.Mvc;

namespace TraceabilityService.Api.Contracts;

/// <summary>
/// The query string the batch search and its CSV export take (SCRUM-139). All optional, and
/// every filter given narrows the result: they combine with AND. The names are set
/// explicitly so a 400's errors are keyed by them (from, not From).
/// </summary>
public class BatchSearchQuery
{
    /// <summary>
    /// One batch status (Chilling, Processing, OnHold, AwaitingLab, Cleared, Failed), in any case.
    /// </summary>
    [FromQuery(Name = "status")]
    public string? Status{get; set;}

    /// <summary>
    /// One of the six product line codes (FM, FLM, SY, SK, DY, CD), in any case.
    /// </summary>
    [FromQuery(Name = "productLine")]
    public string? ProductLine{get; set;}

    /// <summary>
    /// First factory day (Asia/Colombo) the batch's first event may fall on, inclusive.
    /// </summary>
    [FromQuery(Name = "from")]
    public DateOnly? From{get; set;}

    /// <summary>
    /// Last factory day (Asia/Colombo) the batch's first event may fall on, inclusive.
    /// </summary>
    [FromQuery(Name = "to")]
    public DateOnly? To{get; set;}

    /// <summary>
    /// A society code or name, e.g. VS001: batches made with milk from it, read from the
    /// resolved upstream snapshot. Not case sensitive.
    /// </summary>
    [FromQuery(Name = "society")]
    public string? Society{get; set;}

    /// <summary>
    /// An MCC tank code, e.g. T1: batches that drew from it, read from the resolved
    /// upstream snapshot. Not case sensitive.
    /// </summary>
    [FromQuery(Name = "tank")]
    public string? Tank{get; set;}

    /// <summary>
    /// A user ID: batches that user recorded something on, at Processing or the Quality Lab,
    /// or at the MCC and factory intake as the snapshot shows. Not case sensitive.
    /// </summary>
    [FromQuery(Name = "user")]
    public string? User{get; set;}

    /// <summary>
    /// A deviation reason code, e.g. LOW_FAT: batches with at least one deviation of it.
    /// </summary>
    [FromQuery(Name = "reasonCode")]
    public string? ReasonCode{get; set;}

    /// <summary>
    /// Facility code, e.g. FACTORY-01. Leave out for every facility.
    /// </summary>
    [FromQuery(Name = "facility")]
    public string? Facility{get; set;}
}
