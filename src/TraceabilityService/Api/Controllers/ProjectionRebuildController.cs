using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure;
using TraceabilityService.Application;

namespace TraceabilityService.Api.Controllers;

/// <summary>
/// Projection rebuild administration (SCRUM-140).
/// Reads retained topic history using a separate consumer group, re-resolves upstream snapshots,
/// builds prefixed shadow tables, atomically promotes them, and aligns live consumer offsets.
/// Restricted to System Administrators.
/// </summary>
[ApiController]
[Route("api/admin/rebuild")]
[Authorize(Policy = TraceabilityPolicies.AdminOnly)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class ProjectionRebuildController(IProjectionRebuildService rebuildService) : ControllerBase
{
    private const string ProblemJson = "application/problem+json";

    /// <summary>
    /// Rebuilds the Traceability projection store from event history.
    /// Reads retained topic history using a separate consumer group, re-resolves cached upstream
    /// snapshots, atomically promotes shadow tables, then aligns live consumer offsets.
    /// The entire operation is audited.
    /// </summary>
    /// <param name="request">Optional rebuild options (custom consumer group, clearStore, resolveUpstreamSnapshots).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Rebuild completed successfully.</response>
    /// <response code="400">Invalid parameters.</response>
    /// <response code="500">Rebuild failure.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ProjectionRebuildResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError, ProblemJson)]
    public async Task<ActionResult<ProjectionRebuildResultDto>> Rebuild(
        [FromBody] ProjectionRebuildRequest? request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await rebuildService.RebuildAsync(
                request ?? new ProjectionRebuildRequest(),
                this.AuditRequester(),
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Rebuild failed", Detail = ex.Message });
        }
    }
}

