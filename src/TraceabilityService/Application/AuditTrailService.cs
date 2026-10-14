using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// The consolidated audit trail (SCRUM-138): one list, across every facility, of who recorded or
/// did what and when, read from three places at query time rather than copied: the consumed
/// events on timeline_events, the records in each resolved upstream snapshot, and Traceability's
/// own audit_entries. Nothing is written here but audit_entries rows, which are never changed.
/// </summary>
public class AuditTrailService(TraceabilityDbContext db, TimeProvider time) : IAuditTrailService
{
    public const int PageSize = 50;
    public const string NoMatchMessage = "No audit entries match these filters. Remove or change a filter and search again.";
    public const string BatchEntityType = "Batch";

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    // Every source as rows of one shape, for the filters, order and paging to be composed over in
    // LINQ (AuditTrailFilter.Apply). Text columns are all given one collation, the schema's case and
    // accent insensitive one, so the UNION never mixes two and a user ID matches in any case.
    // Snapshot times are JSON strings written in UTC with a Z, which MySQL does not parse, so the Z
    // is dropped before the CAST. EntryId is unique across the sources and breaks ties in the order.
    private const string ConsolidatedSql = $"""
        SELECT 'ConsumedEvent' AS Source,
               CONCAT('event-', e.Id) AS EntryId,
               e.OccurredAt AS OccurredAt,
               e.RecordedBy COLLATE utf8mb4_0900_ai_ci AS UserId,
               e.EventType COLLATE utf8mb4_0900_ai_ci AS Action,
               e.Checkpoint COLLATE utf8mb4_0900_ai_ci AS Checkpoint,
               e.BatchId COLLATE utf8mb4_0900_ai_ci AS BatchId,
               b.Facility COLLATE utf8mb4_0900_ai_ci AS Facility,
               CONVERT(e.SourceEventId USING utf8mb4) COLLATE utf8mb4_0900_ai_ci AS Reference,
               NULL AS Details
        FROM timeline_events AS e
        JOIN batch_records AS b ON b.BatchId = e.BatchId

        UNION ALL
        SELECT 'UpstreamSnapshot', CONCAT('consignment-', s.Id, '-', c.n),
               CAST(REPLACE(REPLACE(c.registeredAt, 'T', ' '), 'Z', '') AS DATETIME(6)),
               c.registeredBy, '{AuditActions.ConsignmentRegistered}', 'Mcc',
               s.BatchId COLLATE utf8mb4_0900_ai_ci, b.Facility COLLATE utf8mb4_0900_ai_ci, c.reference, NULL
        FROM upstream_snapshots AS s
        JOIN batch_records AS b ON b.BatchId = s.BatchId
        CROSS JOIN JSON_TABLE(s.Payload, '$.tanks[*].consignments[*]' COLUMNS (
            n FOR ORDINALITY,
            reference VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci PATH '$.reference',
            registeredAt VARCHAR(40) PATH '$.registeredAt',
            registeredBy VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci PATH '$.registeredBy')) AS c
        WHERE s.ResolutionStatus = 'Resolved' AND c.registeredAt IS NOT NULL

        UNION ALL
        SELECT 'UpstreamSnapshot', CONCAT('dispatch-', s.Id),
               CAST(REPLACE(REPLACE(d.dispatchedAt, 'T', ' '), 'Z', '') AS DATETIME(6)),
               d.dispatchedBy, '{AuditActions.MilkDispatched}', 'Mcc',
               s.BatchId COLLATE utf8mb4_0900_ai_ci, b.Facility COLLATE utf8mb4_0900_ai_ci, d.reference, NULL
        FROM upstream_snapshots AS s
        JOIN batch_records AS b ON b.BatchId = s.BatchId
        CROSS JOIN JSON_TABLE(s.Payload, '$.dispatch' COLUMNS (
            reference VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci PATH '$.reference',
            dispatchedAt VARCHAR(40) PATH '$.dispatchedAt',
            dispatchedBy VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci PATH '$.dispatchedBy')) AS d
        WHERE s.ResolutionStatus = 'Resolved' AND d.dispatchedAt IS NOT NULL

        UNION ALL
        SELECT 'UpstreamSnapshot', CONCAT('screening-', s.Id),
               CAST(REPLACE(REPLACE(i.screenedAt, 'T', ' '), 'Z', '') AS DATETIME(6)),
               i.screenedBy, '{AuditActions.FactoryIntakeScreened}', 'Intake',
               s.BatchId COLLATE utf8mb4_0900_ai_ci, b.Facility COLLATE utf8mb4_0900_ai_ci, d.reference, NULL
        FROM upstream_snapshots AS s
        JOIN batch_records AS b ON b.BatchId = s.BatchId
        CROSS JOIN JSON_TABLE(s.Payload, '$' COLUMNS (
            screenedAt VARCHAR(40) PATH '$.intake.screenedAt',
            screenedBy VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci PATH '$.intake.screenedBy')) AS i
        CROSS JOIN JSON_TABLE(s.Payload, '$.dispatch' COLUMNS (
            reference VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci PATH '$.reference')) AS d
        WHERE s.ResolutionStatus = 'Resolved' AND i.screenedAt IS NOT NULL

        UNION ALL
        SELECT 'Traceability', CONCAT('audit-', a.Id), a.OccurredAt,
               a.Actor COLLATE utf8mb4_0900_ai_ci, a.Action COLLATE utf8mb4_0900_ai_ci, NULL,
               (CASE WHEN a.EntityType = '{BatchEntityType}' THEN a.EntityId END) COLLATE utf8mb4_0900_ai_ci,
               b.Facility COLLATE utf8mb4_0900_ai_ci,
               a.EntityId COLLATE utf8mb4_0900_ai_ci,
               CAST(a.Details AS CHAR)
        FROM audit_entries AS a
        LEFT JOIN batch_records AS b ON a.EntityType = '{BatchEntityType}' AND b.BatchId = a.EntityId
        """;

    private readonly TraceabilityDbContext _db = db;
    private readonly TimeProvider _time = time;

    public async Task<AuditTrailPageDto> SearchAsync(
        AuditTrailFilter filter, int page, CancellationToken cancellationToken = default)
    {
        var matching = filter.Apply(_db.Database.SqlQueryRaw<AuditTrailRow>(ConsolidatedSql));
        var total = await matching.CountAsync(cancellationToken);

        var rows = total == 0
            ? []
            : await matching
                .OrderByDescending(r => r.OccurredAt)
                .ThenBy(r => r.EntryId)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync(cancellationToken);

        return new AuditTrailPageDto
        {
            Items = [.. rows.Select(ToDto)],
            Page = page,
            PageSize = PageSize,
            TotalCount = total,
            TotalPages = (total + PageSize - 1) / PageSize,
            Message = total == 0 ? NoMatchMessage : null,
            Filters = new AuditTrailFiltersDto
            {
                User = filter.User,
                Action = filter.Action,
                From = filter.From,
                To = filter.To,
                BatchId = filter.BatchId,
            },
        };
    }

    public async Task RecordBatchOpenedAsync(
        string action, string batchId, AuditRequester requester, CancellationToken cancellationToken = default)
    {
        _db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = _time.GetUtcNow().UtcDateTime,
            Actor = Truncate(requester.UserId, 100),
            Action = action,
            EntityType = BatchEntityType,
            EntityId = batchId,
            Details = JsonSerializer.Serialize(new { role = requester.Role }, AuditJson),
            CorrelationId = requester.CorrelationId,
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordAdminActionAsync(
        string action, string entityType, string entityId, object? details, AuditRequester requester, CancellationToken cancellationToken = default)
    {
        _db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = _time.GetUtcNow().UtcDateTime,
            Actor = Truncate(requester.UserId, 100),
            Action = action,
            EntityType = entityType,
            EntityId = Truncate(entityId, 100),
            Details = details is null ? null : JsonSerializer.Serialize(details, AuditJson),
            CorrelationId = requester.CorrelationId,
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    public static AuditTrailEntryDto ToDto(AuditTrailRow row) => new()
    {
        Id = row.EntryId,
        OccurredAt = DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc),
        User = row.UserId,
        Action = row.Action,
        Checkpoint = row.Checkpoint is null ? null : Enum.Parse<Checkpoint>(row.Checkpoint),
        BatchId = row.BatchId,
        Facility = row.Facility,
        Source = Enum.Parse<AuditSource>(row.Source),
        Reference = row.Reference,
        Details = row.Details is null ? null : JsonDocument.Parse(row.Details).RootElement.Clone(),
    };

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
