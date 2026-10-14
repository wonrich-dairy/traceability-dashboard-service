using TraceabilityService.Api.Contracts;

namespace TraceabilityService.Application;

public interface IBatchSearchService
{
    // One page of the batches matching the filter, newest first, with the total. A filter that
    // matches nothing is an empty page with a message, not an error.
    Task<BatchSearchPageDto> SearchAsync(
        BatchSearchFilter filter,
        int page,
        CancellationToken cancellationToken = default);

    // Every batch matching the filter as CSV, not only one page, and an audit entry saying who
    // exported what. The entry is saved before the file is returned, so no export goes unaudited.
    Task<BatchExport> ExportAsync(
        BatchSearchFilter filter,
        BatchExportRequester requester,
        CancellationToken cancellationToken = default);
}

/// <summary>The CSV file of an export, and how many batches are in it.</summary>
public sealed record BatchExport(string FileName, byte[] Content, int RowCount);

/// <summary>Who asked for an export, for its audit entry.</summary>
public sealed record BatchExportRequester(string UserId, string? Role, string? CorrelationId);
