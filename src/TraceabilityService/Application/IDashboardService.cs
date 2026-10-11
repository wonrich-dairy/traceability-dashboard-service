using TraceabilityService.Api.Contracts;

namespace TraceabilityService.Application;

public interface IDashboardService
{
    Task<DashboardSummaryDto> GetSummaryAsync(DashboardFilter filter, CancellationToken cancellationToken = default);

    Task<DashboardTrendDto> GetTrendAsync(DashboardFilter filter, CancellationToken cancellationToken = default);

    Task<DashboardFailureReasonsDto> GetFailureReasonsAsync(DashboardFilter filter, CancellationToken cancellationToken = default);

    // The latest failed batches, at most limit of them.
    Task<DashboardRecentFailuresDto> GetRecentFailuresAsync(
        DashboardFilter filter, int limit, CancellationToken cancellationToken = default);

    // The most marginal societies behind the batches in range, at most limit of them.
    Task<DashboardSocietiesDto> GetSocietiesAsync(
        DashboardFilter filter, int limit, CancellationToken cancellationToken = default);
}
