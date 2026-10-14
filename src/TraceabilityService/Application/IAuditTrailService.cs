using TraceabilityService.Api.Contracts;

namespace TraceabilityService.Application;

public interface IAuditTrailService
{
    // One page of the consolidated audit trail matching the filter, newest first, with the total.
    // A filter that matches nothing is an empty page with a message, not an error.
    Task<AuditTrailPageDto> SearchAsync(
        AuditTrailFilter filter,
        int page,
        CancellationToken cancellationToken = default);

    // Adds an audit entry saying who opened which batch, as BatchRecordOpened or BatchTraceOpened,
    // and saves it before returning, so the caller sees nothing that went unaudited. There is
    // deliberately no way to change or remove an entry.
    Task RecordBatchOpenedAsync(
        string action,
        string batchId,
        AuditRequester requester,
        CancellationToken cancellationToken = default);
}
