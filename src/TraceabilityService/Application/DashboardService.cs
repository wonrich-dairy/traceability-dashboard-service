using Microsoft.EntityFrameworkCore;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

public class DashboardService(TraceabilityDbContext db) : IDashboardService
{
    private readonly TraceabilityDbContext _db = db;

    // Everything that is neither Released (cleared the lab) nor Rejected.
    private static readonly Status[] InProgressStatuses =
        [Status.Chilling, Status.Processing, Status.AwaitingLab];

    private const int RecentFailureLimit = 5;

    // The window is inclusive of both ends, so this is 30 days, not 31.
    private const int DefaultWindowDays = 30;

    public async Task<DashboardSummaryDto> GetSummaryAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        // Anchored on the closing day, so 'to' alone means "the 30 days ending
        // there". Now, not UtcNow: ProductionDate is a plant calendar day, and
        // UTC would roll it over mid-shift.
        var windowTo = to ?? DateOnly.FromDateTime(DateTime.Now);
        var windowFrom = from ?? windowTo.AddDays(-(DefaultWindowDays - 1));

        // Shared by both queries below, so the counts and the failure list cannot
        // describe different windows. On ProductionDate rather than UpdatedAt,
        // which would move a batch between windows when the row is edited.
        var windowed = _db.BatchTraces
            .AsNoTracking()
            .Where(t => t.ProductionDate >= windowFrom && t.ProductionDate <= windowTo);

        // One GROUP BY rather than a count query per card, so the cards are all
        // read at the same moment.
        var counts = await windowed
            .GroupBy(t => t.CurrentStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        // A status no batch is in is absent from the GROUP BY, but still needs a
        // card, so fill the gaps with zeroes.
        var byStatus = Enum.GetValues<Status>()
            .ToDictionary(s => s, s => counts.GetValueOrDefault(s));

        var passed = byStatus[Status.Released];
        var failed = byStatus[Status.Rejected];

        // Only the batches the lab has ruled on: counting in-progress ones would
        // drag the rate down every time a shift started.
        var decided = passed + failed;

        // Ordered by UpdatedAt, the closest the row has to "when it failed";
        // ProductionDate is day-granular, so a day's rejections would all tie.
        // ThenBy(Id) keeps ties stable rather than whatever MySQL returns.
        var recentFailures = await windowed
            .Where(t => t.CurrentStatus == Status.Rejected)
            .OrderByDescending(t => t.UpdatedAt)
            .ThenByDescending(t => t.Id)
            .Take(RecentFailureLimit)
            .Select(t => new RecentFailureDto
            {
                BatchId = t.BatchId,
                ProductionDate = t.ProductionDate,
            })
            .ToListAsync(cancellationToken);

        return new DashboardSummaryDto
        {
            // Echoed back because both bounds are optional: the caller still
            // needs to know what period the numbers cover.
            From = windowFrom,
            To = windowTo,

            TotalBatches = byStatus.Values.Sum(),
            InProgress = InProgressStatuses.Sum(s => byStatus[s]),
            Passed = passed,
            Failed = failed,

            // Null rather than 0 / 0: no rate to report keeps the zero-batch case
            // off the 0%-pass-rate alarm, and a NaN out of the serialiser.
            PassRate = decided == 0
                ? null
                : Math.Round((double)passed / decided, 4),

            RecentFailures = recentFailures,
            ByStatus = byStatus,
        };
    }
}
