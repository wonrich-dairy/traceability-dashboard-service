using Microsoft.AspNetCore.Mvc;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Application;

namespace TraceabilityService.Api.Controllers;

[ApiController]
[Route("api/dashboard/summary")]
public class DashboardController(IDashboardService dashboard) : ControllerBase
{
    private const string ProblemJson = "application/problem+json";
    private readonly IDashboardService _dashboard = dashboard;

    // Both bounds are optional and inclusive, as yyyy-MM-dd. Omitting them gives
    // the last 30 days; the window that was actually used comes back on the DTO.
    [HttpGet]
    [ProducesResponseType(typeof(DashboardSummaryDto), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemJson)]
    public async Task<ActionResult<DashboardSummaryDto>> GetSummary(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        // An inverted window is a caller mistake, not an empty period: answering
        // it with a summary of zeroes would look like a quiet month.
        if (from is not null && to is not null && from > to)
        {
            return Problem(
                title: "Invalid date window",
                detail: $"'from' ({from:yyyy-MM-dd}) is after 'to' ({to:yyyy-MM-dd}).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // No 404 branch here: a window with no batches is a summary of zeroes.
        return Ok(await _dashboard.GetSummaryAsync(from, to, cancellationToken));
    }
}
