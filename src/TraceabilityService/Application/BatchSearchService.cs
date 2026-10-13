using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// Batch search and CSV export (SCRUM-139), read from batch_records and the tables that hang
/// off it. Every filter is a condition on batch_records itself, an EXISTS or IN subquery rather
/// than a join, so a batch with several tanks, societies, users or reasons is still one row and
/// the total counts batches, not matches.
/// </summary>
public class BatchSearchService(TraceabilityDbContext db, FactoryCalendar calendar, TimeProvider time) : IBatchSearchService
{
    public const int PageSize = 50;
    public const string NoMatchMessage = "No batches match these filters. Remove or change a filter and search again.";
    public const string ExportAction = "BatchSearchExported";
    public const string ExportEntityType = "BatchSearch";

    // How many batches' details are read per round trip when exporting, to keep the IN lists short.
    private const int DetailBatchSize = 500;

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TraceabilityDbContext _db = db;
    private readonly FactoryCalendar _calendar = calendar;
    private readonly TimeProvider _time = time;

    public async Task<BatchSearchPageDto> SearchAsync(
        BatchSearchFilter filter, int page, CancellationToken cancellationToken = default)
    {
        var matching = Matching(filter);
        var total = await matching.CountAsync(cancellationToken);

        var summaries = total == 0
            ? []
            : await Newest(matching)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(BatchRecordService.ToSummary)
                .ToListAsync(cancellationToken);

        return new BatchSearchPageDto
        {
            Items = await RowsAsync(summaries, cancellationToken),
            Page = page,
            PageSize = PageSize,
            TotalCount = total,
            TotalPages = (total + PageSize - 1) / PageSize,
            Message = total == 0 ? NoMatchMessage : null,
            Filters = Echo(filter),
        };
    }

    public async Task<BatchExport> ExportAsync(
        BatchSearchFilter filter, AuditRequester requester, CancellationToken cancellationToken = default)
    {
        var summaries = await Newest(Matching(filter))
            .Select(BatchRecordService.ToSummary)
            .ToListAsync(cancellationToken);

        var rows = new List<BatchSearchRowDto>(summaries.Count);
        foreach (var chunk in summaries.Chunk(DetailBatchSize))
        {
            rows.AddRange(await RowsAsync(chunk, cancellationToken));
        }

        var fileName = BatchSearchCsv.FileName(filter, _calendar.Today);
        var content = BatchSearchCsv.Write(rows);

        _db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = _time.GetUtcNow().UtcDateTime,
            Actor = Truncate(requester.UserId, 100),
            Action = ExportAction,
            EntityType = ExportEntityType,
            EntityId = Truncate(fileName, 100),
            Details = JsonSerializer.Serialize(new
            {
                fileName,
                rowCount = rows.Count,
                role = requester.Role,
                filters = Echo(filter),
            }, AuditJson),
            CorrelationId = requester.CorrelationId,
        });
        await _db.SaveChangesAsync(cancellationToken);

        return new BatchExport(fileName, content, rows.Count);
    }

    private IQueryable<BatchRecord> Matching(BatchSearchFilter filter)
    {
        var query = _db.BatchRecords.AsNoTracking();

        if (filter.Status is { } status)
        {
            query = query.Where(b => b.Status == status);
        }

        if (filter.ProductLine is { } productLine)
        {
            query = query.Where(b => b.ProductLine == productLine);
        }

        if (filter.Facility is { } facility)
        {
            query = query.Where(b => b.Facility == facility);
        }

        // The range is factory days, as on the dashboard: a batch is in it when its first event
        // (the start of its trace) falls on one of them in Colombo.
        if (filter.From is { } from)
        {
            var fromUtc = FactoryCalendar.StartOfDayUtc(from);
            query = query.Where(b => b.FirstEventAt >= fromUtc);
        }

        if (filter.To is { } to)
        {
            var toUtc = FactoryCalendar.StartOfDayUtc(to.AddDays(1));
            query = query.Where(b => b.FirstEventAt < toUtc);
        }

        if (filter.ReasonCode is { } reasonCode)
        {
            query = query.Where(b => b.Deviations.Any(d => d.ReasonCode == reasonCode));
        }

        if (filter.Society is { } society)
        {
            var batchIds = SocietyBatchIds(society);
            query = query.Where(b => batchIds.Contains(b.BatchId));
        }

        if (filter.Tank is { } tank)
        {
            var batchIds = TankBatchIds(tank);
            query = query.Where(b => batchIds.Contains(b.BatchId));
        }

        // Processing and the lab name who recorded each event; the MCC and intake users are
        // only in the snapshot.
        if (filter.User is { } user)
        {
            var batchIds = SnapshotUserBatchIds(user);
            query = query.Where(b => b.TimelineEvents.Any(e => e.RecordedBy == user) || batchIds.Contains(b.BatchId));
        }

        return query;
    }

    // Societies, tanks and the MCC and intake users live only in the snapshot's JSON, so these
    // read it with JSON_TABLE (MySQL 8). The columns are declared accent and case insensitive,
    // like the rest of the schema, so vs001 finds VS001. Each returns batch IDs for an IN
    // subquery; a batch listed twice still matches once.
    private IQueryable<string> SocietyBatchIds(string society) => _db.Database.SqlQuery<string>($"""
        SELECT s.BatchId AS Value
        FROM upstream_snapshots AS s,
             JSON_TABLE(s.Payload, '$.tanks[*].consignments[*].society' COLUMNS (
                 code VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci PATH '$.code',
                 name VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci PATH '$.name')) AS society
        WHERE s.ResolutionStatus = 'Resolved' AND (society.code = {society} OR society.name = {society})
        """);

    private IQueryable<string> TankBatchIds(string tank) => _db.Database.SqlQuery<string>($"""
        SELECT s.BatchId AS Value
        FROM upstream_snapshots AS s,
             JSON_TABLE(s.Payload, '$.tanks[*]' COLUMNS (
                 code VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci PATH '$.code')) AS tank
        WHERE s.ResolutionStatus = 'Resolved' AND tank.code = {tank}
        """);

    // A snapshot with no consignments still gives one row, with a null registeredBy.
    private IQueryable<string> SnapshotUserBatchIds(string user) => _db.Database.SqlQuery<string>($"""
        SELECT s.BatchId AS Value
        FROM upstream_snapshots AS s,
             JSON_TABLE(s.Payload, '$' COLUMNS (
                 dispatchedBy VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci PATH '$.dispatch.dispatchedBy',
                 screenedBy VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci PATH '$.intake.screenedBy',
                 NESTED PATH '$.tanks[*].consignments[*]' COLUMNS (
                     registeredBy VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci PATH '$.registeredBy'))) AS person
        WHERE s.ResolutionStatus = 'Resolved'
          AND (person.dispatchedBy = {user} OR person.screenedBy = {user} OR person.registeredBy = {user})
        """);

    // Newest first; BatchId breaks ties so paging is stable.
    private static IQueryable<BatchRecord> Newest(IQueryable<BatchRecord> query) =>
        query.OrderByDescending(b => b.FirstEventAt).ThenByDescending(b => b.BatchId);

    // The rest of each row, read for the given batches only: their snapshots, deviations,
    // current determinations and the users on their timelines.
    private async Task<List<BatchSearchRowDto>> RowsAsync(
        IReadOnlyCollection<BatchSummaryDto> summaries, CancellationToken cancellationToken)
    {
        if (summaries.Count == 0)
        {
            return [];
        }

        var ids = summaries.Select(s => s.BatchId).ToList();

        var snapshots = await _db.UpstreamSnapshots
            .AsNoTracking()
            .Where(s => ids.Contains(s.BatchId))
            .Select(s => new { s.BatchId, s.ResolutionStatus, s.Payload })
            .ToDictionaryAsync(s => s.BatchId, cancellationToken);

        var deviations = (await _db.DeviationRecords
                .AsNoTracking()
                .Where(d => ids.Contains(d.BatchId))
                .ToListAsync(cancellationToken))
            .ToLookup(d => d.BatchId);

        // The timeline row of each batch's current determination, chosen as the summary's lab
        // result is: the newest one not superseded. Its deviations judge the lab parameters.
        var currentDetermination = (await _db.LabDeterminations
                .AsNoTracking()
                .Where(d => ids.Contains(d.BatchId) && !d.IsSuperseded)
                .Select(d => new { d.BatchId, d.DeterminedAt, d.TimelineEventId })
                .ToListAsync(cancellationToken))
            .GroupBy(d => d.BatchId)
            .ToDictionary(g => g.Key, g => g.MaxBy(d => d.DeterminedAt)!.TimelineEventId);

        var recorders = (await _db.TimelineEvents
                .AsNoTracking()
                .Where(e => ids.Contains(e.BatchId) && e.RecordedBy != null)
                .Select(e => new { e.BatchId, e.RecordedBy })
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToLookup(e => e.BatchId, e => e.RecordedBy!);

        return [.. summaries.Select(summary =>
        {
            var snapshot = snapshots.GetValueOrDefault(summary.BatchId);
            var payload = snapshot is { ResolutionStatus: ResolutionStatus.Resolved, Payload: not null }
                ? UpstreamSnapshotPayload.Parse(snapshot.Payload)
                : null;
            var consignments = payload?.Tanks.SelectMany(t => t.Consignments).ToList() ?? [];

            var batchDeviations = deviations[summary.BatchId].ToList();
            var current = currentDetermination.TryGetValue(summary.BatchId, out var timelineEventId)
                ? batchDeviations.Where(d => d.TimelineEventId == timelineEventId).ToList()
                : [];

            return new BatchSearchRowDto
            {
                BatchId = summary.BatchId,
                DispatchRef = summary.DispatchRef,
                BatchReference = summary.BatchReference,
                ProductLine = summary.ProductLine,
                Facility = summary.Facility,
                Status = summary.Status,
                Completeness = summary.Completeness,
                FirstEventAt = summary.FirstEventAt,
                LastEventAt = summary.LastEventAt,
                LabResult = summary.LabResult,
                UpstreamStatus = snapshot?.ResolutionStatus,
                Tanks = [.. payload?.Tanks.Select(t => t.Code) ?? []],
                Societies = [.. consignments
                    .Select(c => c.Society)
                    .DistinctBy(s => s.Code)
                    .Select(s => new SocietyLinkDto { Code = s.Code, Name = s.Name })],
                Users = [.. recorders[summary.BatchId]
                    .Select(u => (string?)u)
                    .Append(payload?.Dispatch.DispatchedBy)
                    .Append(payload?.Intake.ScreenedBy)
                    .Concat(consignments.Select(c => c.RegisteredBy))
                    .OfType<string>()
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)],
                DeviationCount = batchDeviations.Count,
                ReasonCodes = [.. batchDeviations
                    .Select(d => d.ReasonCode)
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal)],
                LabParameters = LabParameters.Assess(summary.ProductLine, summary.LabResult, current),
            };
        })];
    }

    private static BatchSearchFiltersDto Echo(BatchSearchFilter filter) => new()
    {
        Status = filter.Status,
        ProductLine = filter.ProductLine,
        From = filter.From,
        To = filter.To,
        Society = filter.Society,
        Tank = filter.Tank,
        User = filter.User,
        ReasonCode = filter.ReasonCode,
        Facility = filter.Facility,
    };

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
