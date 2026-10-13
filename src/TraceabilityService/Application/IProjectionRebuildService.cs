using Confluent.Kafka;
using TraceabilityService.Api.Contracts;

namespace TraceabilityService.Application;

/// <summary>
/// Service to rebuild the Traceability projection store from event history (SCRUM-140).
/// </summary>
public interface IProjectionRebuildService
{
    /// <summary>
    /// Reads retained topic history using a separate consumer group, builds a shadow projection,
    /// atomically promotes it, aligns live consumer offsets, re-resolves upstream snapshots, and audits.
    /// </summary>
    Task<ProjectionRebuildResultDto> RebuildAsync(
        ProjectionRebuildRequest request,
        AuditRequester requester,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds a shadow projection from a given list of events, atomically promotes it,
    /// re-resolves upstream snapshots from SCRUM-12, and audits the rebuild.
    /// Useful for testing against seeded datasets without an active broker.
    /// </summary>
    Task<ProjectionRebuildResultDto> RebuildFromEventsAsync(
        IReadOnlyList<ConsumeResult<string, string>> events,
        ProjectionRebuildRequest request,
        AuditRequester requester,
        CancellationToken cancellationToken = default);
}

