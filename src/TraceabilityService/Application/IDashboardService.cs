using TraceabilityService.Api.Contracts;

namespace TraceabilityService.Application;

public interface IDashboardService
{
    // Covers batches whose ProductionDate falls in [from, to], both ends
    // inclusive. Either bound may be omitted; the defaults are the last 30 days
    // and the resolved window comes back on the DTO.
    //
    // Always returns a summary. A window with no batches in it is a summary of
    // zeroes, not a 404: the dashboard asks about a period, not about one batch.
    Task<DashboardSummaryDto> GetSummaryAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default);
}
