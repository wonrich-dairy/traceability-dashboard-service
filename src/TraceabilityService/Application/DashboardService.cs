using Microsoft.EntityFrameworkCore;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// The QC dashboard's figures, read from the pre-counted daily tables and batch_records
/// rather than counted from the event rows on each request.
/// </summary>
public class DashboardService(TraceabilityDbContext db, FactoryCalendar calendar) : IDashboardService
{
    private readonly TraceabilityDbContext _db = db;
    private readonly FactoryCalendar _calendar = calendar;

    public async Task<DashboardSummaryDto> GetSummaryAsync(DashboardFilter filter, CancellationToken cancellationToken = default)
    {
        var today = _calendar.Today;

        // At most a year of days times six product lines per facility, so these are summed
        // here rather than with a GROUP BY on a constant, which MySQL reads as a column number.
        var todayRows = await Counts(Aggregates(filter).Where(a => a.Date == today), cancellationToken);
        var rangeRows = await Counts(
            Aggregates(filter).Where(a => a.Date >= filter.From && a.Date <= filter.To), cancellationToken);

        return await WithFreshness(new DashboardSummaryDto
        {
            Today = new TodayCountsDto
            {
                Date = today,
                Pending = todayRows.Sum(r => r.Pending),
                Cleared = todayRows.Sum(r => r.Cleared),
                Failed = todayRows.Sum(r => r.Failed),
                OnHold = todayRows.Sum(r => r.OnHold),
            },
            Range = new StatusCountsDto
            {
                Pending = rangeRows.Sum(r => r.Pending),
                Cleared = rangeRows.Sum(r => r.Cleared),
                Failed = rangeRows.Sum(r => r.Failed),
                OnHold = rangeRows.Sum(r => r.OnHold),
            },
            Deviations = DeviationsByCheckpoint(rangeRows),
        }, filter, cancellationToken);
    }

    public async Task<DashboardDeviationsDto> GetDeviationsAsync(DashboardFilter filter, CancellationToken cancellationToken = default)
    {
        var rangeRows = await Counts(
            Aggregates(filter).Where(a => a.Date >= filter.From && a.Date <= filter.To), cancellationToken);

        return await WithFreshness(
            new DashboardDeviationsDto { Checkpoints = DeviationsByCheckpoint(rangeRows) }, filter, cancellationToken);
    }

    // Always four, in stage order, so a checkpoint with none shows 0 rather than going missing.
    private static List<CheckpointDeviationDto> DeviationsByCheckpoint(List<CountsRow> rows) =>
    [
        new() { Checkpoint = Checkpoint.Mcc, Count = rows.Sum(r => r.Mcc) },
        new() { Checkpoint = Checkpoint.Intake, Count = rows.Sum(r => r.Intake) },
        new() { Checkpoint = Checkpoint.Processing, Count = rows.Sum(r => r.Processing) },
        new() { Checkpoint = Checkpoint.Lab, Count = rows.Sum(r => r.Lab) },
    ];

    public async Task<DashboardTrendDto> GetTrendAsync(DashboardFilter filter, CancellationToken cancellationToken = default)
    {
        // Summed over facilities when the filter does not name one.
        var days = await Aggregates(filter)
            .Where(a => a.Date >= filter.From && a.Date <= filter.To)
            .GroupBy(a => new { a.Date, a.ProductLine })
            .Select(g => new
            {
                g.Key.Date,
                g.Key.ProductLine,
                Batches = g.Sum(a => a.BatchCount),
                Cleared = g.Sum(a => a.ClearedCount),
                Failed = g.Sum(a => a.FailedCount),
            })
            .OrderBy(d => d.Date)
            .ThenBy(d => d.ProductLine)
            .ToListAsync(cancellationToken);

        return await WithFreshness(new DashboardTrendDto
        {
            Points = [.. days.Select(d => new TrendPointDto
            {
                Date = d.Date,
                ProductLine = d.ProductLine,
                BatchCount = d.Batches,
                ClearedCount = d.Cleared,
                FailedCount = d.Failed,
                FailRate = FailRate.Of(d.Batches, d.Failed),
            })],
        }, filter, cancellationToken);
    }

    public async Task<DashboardFailureReasonsDto> GetFailureReasonsAsync(
        DashboardFilter filter, CancellationToken cancellationToken = default)
    {
        var query = _db.DailyReasonCodeAggregates
            .AsNoTracking()
            .Where(a => a.Date >= filter.From && a.Date <= filter.To);

        if (filter.Facility is not null)
        {
            query = query.Where(a => a.Facility == filter.Facility);
        }

        if (filter.ProductLine is not null)
        {
            query = query.Where(a => a.ProductLine == filter.ProductLine);
        }

        var reasons = await query
            .GroupBy(a => a.ReasonCode)
            .Select(g => new FailureReasonDto { ReasonCode = g.Key, Count = g.Sum(a => a.DeviationCount) })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.ReasonCode)
            .ToListAsync(cancellationToken);

        return await WithFreshness(new DashboardFailureReasonsDto { Reasons = reasons }, filter, cancellationToken);
    }

    public async Task<DashboardRecentFailuresDto> GetRecentFailuresAsync(
        DashboardFilter filter, int limit, CancellationToken cancellationToken = default)
    {
        // batch_records has no production day, so "in the range" means the failure was
        // recorded (the batch's last event) on one of the range's factory days.
        var fromUtc = FactoryCalendar.StartOfDayUtc(filter.From);
        var toUtc = FactoryCalendar.StartOfDayUtc(filter.To.AddDays(1));

        var query = _db.BatchRecords
            .AsNoTracking()
            .Where(b => b.Status == BatchStatus.Failed && b.LastEventAt >= fromUtc && b.LastEventAt < toUtc);

        // Facility, Status, LastEventAt is indexed for exactly this.
        if (filter.Facility is not null)
        {
            query = query.Where(b => b.Facility == filter.Facility);
        }

        if (filter.ProductLine is not null)
        {
            query = query.Where(b => b.ProductLine == filter.ProductLine);
        }

        var batches = await query
            .OrderByDescending(b => b.LastEventAt)
            .ThenBy(b => b.BatchId)
            .Take(limit)
            .Select(b => new { b.BatchId, b.BatchReference, b.ProductLine, b.Facility, b.LastEventAt })
            .ToListAsync(cancellationToken);

        var batchIds = batches.Select(b => b.BatchId).ToList();
        var reasons = (await _db.DeviationRecords
                .AsNoTracking()
                .Where(d => batchIds.Contains(d.BatchId) && d.ReasonCode != null)
                .Select(d => new { d.BatchId, d.ReasonCode })
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToLookup(d => d.BatchId, d => d.ReasonCode!);

        return await WithFreshness(new DashboardRecentFailuresDto
        {
            Batches = [.. batches.Select(b => new RecentFailureDto
            {
                BatchId = b.BatchId,
                BatchReference = b.BatchReference,
                ProductLine = b.ProductLine,
                Facility = b.Facility,
                FailedAt = b.LastEventAt,
                ReasonCodes = [.. reasons[b.BatchId].Order()],
                TracePath = $"/api/batches/{Uri.EscapeDataString(b.BatchId)}",
            })],
        }, filter, cancellationToken);
    }

    public async Task<DashboardSocietiesDto> GetSocietiesAsync(
        DashboardFilter filter, int limit, CancellationToken cancellationToken = default)
    {
        // TR-12 is descoped, so there is no per-society table: the societies come from the
        // resolved snapshots of the batches in range, parsed on each request. A batch is in
        // range when its first event (the start of its trace) falls on one of the range's
        // factory days.
        var fromUtc = FactoryCalendar.StartOfDayUtc(filter.From);
        var toUtc = FactoryCalendar.StartOfDayUtc(filter.To.AddDays(1));

        var batches = _db.BatchRecords
            .AsNoTracking()
            .Where(b => b.FirstEventAt >= fromUtc && b.FirstEventAt < toUtc);

        if (filter.Facility is not null)
        {
            batches = batches.Where(b => b.Facility == filter.Facility);
        }

        if (filter.ProductLine is not null)
        {
            batches = batches.Where(b => b.ProductLine == filter.ProductLine);
        }

        var payloads = await batches
            .Join(
                _db.UpstreamSnapshots.Where(s => s.ResolutionStatus == ResolutionStatus.Resolved && s.Payload != null),
                b => b.BatchId,
                s => s.BatchId,
                (b, s) => new { b.BatchId, s.Payload })
            .ToListAsync(cancellationToken);

        var societies = SocietyRanking.Rank(
            payloads.Select(p => (p.BatchId, UpstreamSnapshotPayload.Parse(p.Payload!))),
            limit);

        return await WithFreshness(new DashboardSocietiesDto { Societies = societies }, filter, cancellationToken);
    }

    private IQueryable<DailyQualityAggregate> Aggregates(DashboardFilter filter)
    {
        var query = _db.DailyQualityAggregates.AsNoTracking();

        if (filter.Facility is not null)
        {
            query = query.Where(a => a.Facility == filter.Facility);
        }

        if (filter.ProductLine is not null)
        {
            query = query.Where(a => a.ProductLine == filter.ProductLine);
        }

        return query;
    }

    private static Task<List<CountsRow>> Counts(IQueryable<DailyQualityAggregate> query, CancellationToken cancellationToken) =>
        query
            .Select(a => new CountsRow(
                a.PendingCount, a.ClearedCount, a.FailedCount, a.OnHoldCount,
                a.MccDeviationCount, a.IntakeDeviationCount, a.ProcessingDeviationCount, a.LabDeviationCount))
            .ToListAsync(cancellationToken);

    // Echoes the filter and adds when the read model last took in an event.
    private async Task<T> WithFreshness<T>(T response, DashboardFilter filter, CancellationToken cancellationToken)
        where T : DashboardResponse
    {
        response.Facility = filter.Facility;
        response.ProductLine = filter.ProductLine;
        response.From = filter.From;
        response.To = filter.To;
        response.DataAsOf = await _db.ProcessedMessages.MaxAsync(m => (DateTime?)m.ProcessedAt, cancellationToken);
        return response;
    }

    private sealed record CountsRow(
        int Pending, int Cleared, int Failed, int OnHold, int Mcc, int Intake, int Processing, int Lab);
}
