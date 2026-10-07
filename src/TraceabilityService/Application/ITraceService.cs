using TraceabilityService.Api.Contracts;

namespace TraceabilityService.Application;

public interface ITraceService
{
    // Returns null when no batch carries that BatchId, which the caller turns into a 404.
    Task<BatchTraceResponseDto?> GetBatchTraceAsync(
        string batchId,
        CancellationToken cancellationToken = default);
}
