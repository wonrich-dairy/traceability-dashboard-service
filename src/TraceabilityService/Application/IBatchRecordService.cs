using TraceabilityService.Api.Contracts;

namespace TraceabilityService.Application;

public interface IBatchRecordService
{
    // Returns null when no batch carries that BatchId, which the caller turns into a 404.
    Task<BatchRecordDto?> GetAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    // Every batch matching the filters that are given, oldest first. Empty when none
    // match. At least one filter must be given; the caller checks that.
    Task<IReadOnlyList<BatchSummaryDto>> FindAsync(
        string? dispatchRef,
        string? batchReference,
        CancellationToken cancellationToken = default);
}
