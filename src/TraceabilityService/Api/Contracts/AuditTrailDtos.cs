using System.Text.Json;
using System.Text.Json.Serialization;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Contracts;

/// <summary>
/// Where an audit trail entry was read from (SCRUM-138).
/// </summary>
public enum AuditSource
{
    /// <summary>
    /// An event Traceability consumed, as stored on the batch's timeline.
    /// </summary>
    ConsumedEvent,

    /// <summary>
    /// A record in the batch's cached upstream snapshot, from the MCC and Intake Service.
    /// </summary>
    UpstreamSnapshot,

    /// <summary>
    /// Traceability's own record of what was done in it: opening a batch or exporting.
    /// </summary>
    Traceability
}

/// <summary>
/// One page of GET /api/audit (SCRUM-138): the matching entries, newest first.
/// </summary>
public class AuditTrailPageDto
{
    public List<AuditTrailEntryDto> Items{get; set;} = [];

    /// <summary>
    /// The page returned, from 1.
    /// </summary>
    public int Page{get; set;}

    public int PageSize{get; set;}

    /// <summary>
    /// Every entry matching the filters, across all pages.
    /// </summary>
    public int TotalCount{get; set;}

    /// <summary>
    /// 0 when nothing matches.
    /// </summary>
    public int TotalPages{get; set;}

    /// <summary>
    /// Said when nothing matches, so a client can show it as it is. Null otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Message{get; set;}

    /// <summary>
    /// The filters as applied, trimmed.
    /// </summary>
    public required AuditTrailFiltersDto Filters{get; set;}
}

/// <summary>
/// The filters an audit trail page ran with. Null for a filter that was not given.
/// </summary>
public class AuditTrailFiltersDto
{
    public string? User{get; set;}
    public string? Action{get; set;}
    public DateOnly? From{get; set;}
    public DateOnly? To{get; set;}
    public string? BatchId{get; set;}
}

/// <summary>
/// Who did what, when, at which checkpoint and to which batch.
/// </summary>
public class AuditTrailEntryDto
{
    /// <summary>
    /// Stable across requests, e.g. event-42 or audit-7, for a client to key rows by.
    /// </summary>
    public required string Id{get; set;}

    /// <summary>
    /// UTC.
    /// </summary>
    public DateTime OccurredAt{get; set;}

    /// <summary>
    /// The user ID who did it. Null when the upstream service recorded no user, e.g. an
    /// event raised by a system.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? User{get; set;}

    /// <summary>
    /// What was done: the upstream event's name (ProcessingHoldRaised, BatchFailed), the
    /// upstream record (ConsignmentRegistered, MilkDispatched, FactoryIntakeScreened), or
    /// Traceability's own (BatchTraceOpened, BatchRecordOpened, BatchSearchExported).
    /// </summary>
    public required string Action{get; set;}

    /// <summary>
    /// Null for Traceability's own actions, which belong to no checkpoint.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Checkpoint? Checkpoint{get; set;}

    /// <summary>
    /// Null for an export, which covers many batches.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? BatchId{get; set;}

    /// <summary>
    /// The batch's facility. Null when there is no batch, or it has since been deleted.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Facility{get; set;}

    public AuditSource Source{get; set;}

    /// <summary>
    /// What identifies the record at its source: the upstream EventId, the dispatch or
    /// consignment reference, the batch ID opened, or the export's file name.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Reference{get; set;}

    /// <summary>
    /// Traceability's own entries only: e.g. an export's filters, row count and the caller's role.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Details{get; set;}
}

/// <summary>
/// GET /api/audit/coverage (SCRUM-138): which actions the consolidated audit trail shows, and
/// which it does not.
/// </summary>
public class AuditCoverageDto
{
    public List<CoveredActionDto> Covered{get; set;} = [];
    public List<UncoveredActionDto> NotCovered{get; set;} = [];

    /// <summary>
    /// Where the same list is kept with the code, for the traceability matrix to link to.
    /// </summary>
    public required string Documentation{get; set;}
}

/// <summary>
/// An action that appears in the audit trail.
/// </summary>
public class CoveredActionDto
{
    /// <summary>
    /// The name it appears under, and the action filter takes.
    /// </summary>
    public required string Action{get; set;}

    /// <summary>
    /// Null for Traceability's own actions.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Checkpoint? Checkpoint{get; set;}

    public AuditSource Source{get; set;}

    /// <summary>
    /// What it records, and whose user ID and time the entry carries.
    /// </summary>
    public required string Description{get; set;}
}

/// <summary>
/// Something done in the Wonrich services that the audit trail does not show, and why.
/// </summary>
public class UncoveredActionDto
{
    public required string Action{get; set;}
    public required string Reason{get; set;}
}
