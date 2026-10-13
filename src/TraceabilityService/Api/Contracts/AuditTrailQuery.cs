using Microsoft.AspNetCore.Mvc;

namespace TraceabilityService.Api.Contracts;

/// <summary>
/// The query string the consolidated audit trail takes (SCRUM-138). All optional, and every
/// filter given narrows the result: they combine with AND. The names are set explicitly so a
/// 400's errors are keyed by them (from, not From).
/// </summary>
public class AuditTrailQuery
{
    /// <summary>
    /// A user ID, e.g. proc.tech-1: what that user recorded, opened or exported. Not case sensitive.
    /// </summary>
    [FromQuery(Name = "user")]
    public string? User{get; set;}

    /// <summary>
    /// One action, e.g. ProcessingHoldRaised or BatchTraceOpened. Not case sensitive. GET
    /// /api/audit/coverage lists them.
    /// </summary>
    [FromQuery(Name = "action")]
    public string? Action{get; set;}

    /// <summary>
    /// First factory day (Asia/Colombo) an entry may fall on, inclusive.
    /// </summary>
    [FromQuery(Name = "from")]
    public DateOnly? From{get; set;}

    /// <summary>
    /// Last factory day (Asia/Colombo) an entry may fall on, inclusive.
    /// </summary>
    [FromQuery(Name = "to")]
    public DateOnly? To{get; set;}

    /// <summary>
    /// Processing's batch code, e.g. 262-FM-B: that batch's entries only. An export covers
    /// many batches, so it has none and is left out.
    /// </summary>
    [FromQuery(Name = "batchId")]
    public string? BatchId{get; set;}
}
