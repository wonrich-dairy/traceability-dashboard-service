using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure;
using TraceabilityService.Application;

namespace TraceabilityService.Api.Controllers;

/// <summary>
/// Dead-letter message inspection and replay (SCRUM-140).
/// Lists failed messages per consumer group with the failure reason and correlation ID,
/// and replays dead-letter messages to their source topic once the root cause is resolved.
/// Restricted to System Administrators.
/// </summary>
[ApiController]
[Route("api/admin/dead-letters")]
[Authorize(Policy = TraceabilityPolicies.AdminOnly)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class DeadLettersController(IDeadLetterService deadLetters) : ControllerBase
{
    private const string ProblemJson = "application/problem+json";

    /// <summary>
    /// Lists dead-letter messages for consumer groups with failure reason and correlation ID.
    /// </summary>
    /// <param name="consumerGroup">Optional consumer group filter (e.g. traceability-stage-events).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The dead-letter messages.</response>
    /// <response code="400">Unknown consumer group specified.</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<DeadLetterMessageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemJson)]
    public async Task<ActionResult<List<DeadLetterMessageDto>>> List(
        [FromQuery] string? consumerGroup, CancellationToken cancellationToken)
    {
        try
        {
            var results = await deadLetters.ListDeadLettersAsync(consumerGroup, cancellationToken);
            return Ok(results);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid consumer group", Detail = ex.Message });
        }
    }

    /// <summary>
    /// Lists dead-letter messages for a specific consumer group.
    /// </summary>
    /// <param name="consumerGroup">The consumer group name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The dead-letter messages for the group.</response>
    /// <response code="400">Unknown consumer group.</response>
    [HttpGet("{consumerGroup}")]
    [ProducesResponseType(typeof(List<DeadLetterMessageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemJson)]
    public async Task<ActionResult<List<DeadLetterMessageDto>>> ListByGroup(
        string consumerGroup, CancellationToken cancellationToken)
    {
        try
        {
            var results = await deadLetters.ListDeadLettersAsync(consumerGroup, cancellationToken);
            return Ok(results);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid consumer group", Detail = ex.Message });
        }
    }

    /// <summary>
    /// Replays a dead-letter message to its source topic.
    /// If the message has already been processed by the group, no duplicate is created.
    /// Both handled and duplicate replays are audited.
    /// </summary>
    /// <param name="request">Replay parameters (consumer group, partition, offset).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Replay result.</response>
    /// <response code="400">Invalid parameters.</response>
    /// <response code="404">Message not found on the dead-letter topic.</response>
    [HttpPost("replay")]
    [ProducesResponseType(typeof(ReplayResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemJson)]
    public async Task<ActionResult<ReplayResultDto>> Replay(
        [FromBody] ReplayDeadLetterRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await deadLetters.ReplayAsync(request, this.AuditRequester(), cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid replay request", Detail = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails { Title = "Dead letter not found", Detail = ex.Message });
        }
    }

    /// <summary>
    /// Replays a dead-letter message for the specified consumer group.
    /// </summary>
    [HttpPost("{consumerGroup}/replay")]
    [ProducesResponseType(typeof(ReplayResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemJson)]
    public async Task<ActionResult<ReplayResultDto>> ReplayForGroup(
        string consumerGroup, [FromBody] ReplayDeadLetterRequest request, CancellationToken cancellationToken)
    {
        request.ConsumerGroup = consumerGroup;
        return await Replay(request, cancellationToken);
    }
}

