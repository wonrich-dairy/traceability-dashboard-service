using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure;
using TraceabilityService.Application;

namespace TraceabilityService.Api.Controllers;

/// <summary>
/// Batch search and CSV export (SCRUM-139): filter batches by status, product line, date range,
/// society, tank, user and reason code, page through them, and export every match. Both take
/// the same filters (see <see cref="BatchSearchQuery"/>), answer 400 when they are invalid,
/// and treat a filter that matches nothing as an answer, not an error. Quality Control
/// Officers and System Administrators only, as for the batch record.
/// </summary>
[ApiController]
[Route("api/batches")]
[Authorize(Policy = TraceabilityPolicies.QualityControl)]
[ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest, ProblemJson)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class BatchSearchController(IBatchSearchService search) : ControllerBase
{
    /// <summary>How many batches an export holds, in a response header as well as the file.</summary>
    public const string BatchCountHeader = "X-Batch-Count";

    private const string ProblemJson = "application/problem+json";
    // Far beyond any real page: it only keeps the row offset from overflowing.
    private const int MaxPage = 1_000_000;

    private readonly IBatchSearchService _search = search;

    /// <summary>
    /// One page of the batches matching every filter given, 50 to a page, newest first, with
    /// the total count.
    /// </summary>
    /// <remarks>
    /// Each batch appears once, however many tanks, societies, users or reasons match. Each row
    /// carries its tanks, societies, users, reason codes and the five lab parameters; one its
    /// product line does not measure is NotApplicable. No match is a 200 with an empty page and
    /// a message.
    /// </remarks>
    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="page">Which page, from 1. Default 1.</param>
    /// <response code="200">The page. Possibly empty, with a message.</response>
    [HttpGet("search")]
    [ProducesResponseType(typeof(BatchSearchPageDto), StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<BatchSearchPageDto>> Search(
        [FromQuery] BatchSearchQuery query,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1)
    {
        if (page is < 1 or > MaxPage)
        {
            ModelState.AddModelError(nameof(page), $"page must be between 1 and {MaxPage}.");
        }

        var filter = Resolve(query);
        return filter is null || !ModelState.IsValid
            ? ValidationProblem(ModelState)
            : Ok(await _search.SearchAsync(filter, page, cancellationToken));
    }

    /// <summary>
    /// Every batch matching the filters as a CSV file, not only one page: one row per batch and
    /// every column the search shows.
    /// </summary>
    /// <remarks>
    /// No cell is left empty: a column the batch's product line does not measure reads N/A,
    /// and values not known yet read Pending, Not received or Not resolved. The file name
    /// carries the filters and the export date, e.g.
    /// batches_status-Failed_line-FM_exported-2026-10-13.csv. Every export is audited with who
    /// asked, the filters and the row count. No match is a file with the heading row only, and
    /// X-Batch-Count: 0.
    /// </remarks>
    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The CSV file.</response>
    [HttpGet("export")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, BatchSearchCsv.ContentType)]
    public async Task<IActionResult> Export(
        [FromQuery] BatchSearchQuery query,
        CancellationToken cancellationToken)
    {
        var filter = Resolve(query);
        if (filter is null)
        {
            return ValidationProblem(ModelState);
        }

        var export = await _search.ExportAsync(filter, this.AuditRequester(), cancellationToken);

        Response.Headers[BatchCountHeader] = export.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return File(export.Content, $"{BatchSearchCsv.ContentType}; charset=utf-8", export.FileName);
    }

    // The filter, or null with the reasons added to ModelState.
    private BatchSearchFilter? Resolve(BatchSearchQuery query)
    {
        var (filter, errors) = BatchSearchFilter.Resolve(query);
        foreach (var (parameter, message) in errors)
        {
            ModelState.AddModelError(parameter, message);
        }

        return filter;
    }
}
