using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure;
using TraceabilityService.Application;

namespace TraceabilityService.Api.Controllers;

/// <summary>
/// The QC dashboard (SCRUM-135). Every endpoint takes facility, productLine, from and to
/// (see <see cref="DashboardQuery"/>), answers 400 when they are invalid, and treats an
/// empty range as zero counts, not an error. Quality Control Officers and System
/// Administrators only, as for the batch record.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = TraceabilityPolicies.QualityControl)]
[ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest, ProblemJson)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class DashboardController(IDashboardService dashboard, FactoryCalendar calendar) : ControllerBase
{
    private const string ProblemJson = "application/problem+json";
    // How many recent failures or societies to list.
    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;

    private readonly IDashboardService _dashboard = dashboard;
    private readonly FactoryCalendar _calendar = calendar;

    /// <summary>Pending, cleared, failed and on-hold counts for today and for the range, and deviations per checkpoint.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(DashboardSummaryDto), StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<DashboardSummaryDto>> Summary(
        [FromQuery] DashboardQuery query, CancellationToken cancellationToken)
    {
        var filter = Resolve(query);
        return filter is null ? ValidationProblem(ModelState) : Ok(await _dashboard.GetSummaryAsync(filter, cancellationToken));
    }

    /// <summary>Deviations over the range per checkpoint, always four in stage order: the summary's figures on their own.</summary>
    [HttpGet("deviations")]
    [ProducesResponseType(typeof(DashboardDeviationsDto), StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<DashboardDeviationsDto>> Deviations(
        [FromQuery] DashboardQuery query, CancellationToken cancellationToken)
    {
        var filter = Resolve(query);
        return filter is null ? ValidationProblem(ModelState) : Ok(await _dashboard.GetDeviationsAsync(filter, cancellationToken));
    }

    /// <summary>Fail rate for each day and product line: failed / every batch of the day, pending and on hold included.</summary>
    [HttpGet("trend")]
    [ProducesResponseType(typeof(DashboardTrendDto), StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<DashboardTrendDto>> Trend(
        [FromQuery] DashboardQuery query, CancellationToken cancellationToken)
    {
        var filter = Resolve(query);
        return filter is null ? ValidationProblem(ModelState) : Ok(await _dashboard.GetTrendAsync(filter, cancellationToken));
    }

    /// <summary>Deviation reason codes over the range, most frequent first.</summary>
    [HttpGet("failure-reasons")]
    [ProducesResponseType(typeof(DashboardFailureReasonsDto), StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<DashboardFailureReasonsDto>> FailureReasons(
        [FromQuery] DashboardQuery query, CancellationToken cancellationToken)
    {
        var filter = Resolve(query);
        return filter is null ? ValidationProblem(ModelState) : Ok(await _dashboard.GetFailureReasonsAsync(filter, cancellationToken));
    }

    /// <summary>The latest failed batches, newest first, each with its batch record as the trace link.</summary>
    /// <param name="query">The common dashboard filters.</param>
    /// <param name="limit">How many, 1 to 50. Default 10.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("recent-failures")]
    [ProducesResponseType(typeof(DashboardRecentFailuresDto), StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<DashboardRecentFailuresDto>> RecentFailures(
        [FromQuery] DashboardQuery query,
        CancellationToken cancellationToken,
        [FromQuery] int limit = DefaultLimit)
    {
        CheckLimit(limit);
        var filter = Resolve(query);
        return filter is null || !ModelState.IsValid
            ? ValidationProblem(ModelState)
            : Ok(await _dashboard.GetRecentFailuresAsync(filter, limit, cancellationToken));
    }

    /// <summary>
    /// The most marginal societies behind the batches in range: whose milk came closest to
    /// being rejected at the MCC gate. Read from the batches' cached upstream snapshots
    /// (TR-12 is descoped), which source and coverage in the response say. Also served as
    /// marginal-societies, the path the frontend's dashboard asks for.
    /// </summary>
    /// <param name="query">The common dashboard filters.</param>
    /// <param name="limit">How many, 1 to 50. Default 10.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("societies")]
    [HttpGet("marginal-societies")]
    [ProducesResponseType(typeof(DashboardSocietiesDto), StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<DashboardSocietiesDto>> Societies(
        [FromQuery] DashboardQuery query,
        CancellationToken cancellationToken,
        [FromQuery] int limit = DefaultLimit)
    {
        CheckLimit(limit);
        var filter = Resolve(query);
        return filter is null || !ModelState.IsValid
            ? ValidationProblem(ModelState)
            : Ok(await _dashboard.GetSocietiesAsync(filter, limit, cancellationToken));
    }

    private void CheckLimit(int limit)
    {
        if (limit is < 1 or > MaxLimit)
        {
            ModelState.AddModelError(nameof(limit), $"limit must be between 1 and {MaxLimit}.");
        }
    }

    // The filter with its defaults, or null with the reasons added to ModelState.
    private DashboardFilter? Resolve(DashboardQuery query)
    {
        var (filter, errors) = DashboardFilter.Resolve(query, _calendar.Today);
        foreach (var (parameter, message) in errors)
        {
            ModelState.AddModelError(parameter, message);
        }

        return filter;
    }
}
