using TraceabilityService.Api.Contracts;

namespace TraceabilityService.Application;

public interface IBatchTraceService
{
    // Returns null when no batch carries that BatchId, which the caller turns into a 404.
    Task<BatchTraceResponse?> GetAsync(
        string batchId,
        CancellationToken cancellationToken = default);
}
