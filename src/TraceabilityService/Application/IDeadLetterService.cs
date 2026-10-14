using TraceabilityService.Api.Contracts;

namespace TraceabilityService.Application;

/// <summary>
/// Service to inspect and replay dead-letter messages (SCRUM-140).
/// </summary>
public interface IDeadLetterService
{
    /// <summary>
    /// Lists dead-letter messages for a consumer group (or all groups if omitted),
    /// with failure reason, attempt count, and correlation ID.
    /// </summary>
    Task<IReadOnlyList<DeadLetterMessageDto>> ListDeadLettersAsync(
        string? consumerGroup = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replays a dead-letter message back to its source topic once the root cause is resolved.
    /// If the message has already been processed by the group, no duplicate is created.
    /// The replay operation is audited.
    /// </summary>
    Task<ReplayResultDto> ReplayAsync(
        ReplayDeadLetterRequest request,
        AuditRequester requester,
        CancellationToken cancellationToken = default);
}

