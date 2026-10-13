using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure;
using TraceabilityService.Application;

namespace TraceabilityService.Api.Controllers;

/// <summary>
/// The consolidated audit trail (SCRUM-138): who recorded or did what, and when, across every
/// facility, and the list of what it covers. Read only: entries are append-only, so there is
/// deliberately no endpoint to add, change or remove one. System Administrators and Quality
/// Control Officers only: no token is 401, any other role is 403.
/// </summary>
[ApiController]
[Route("api/audit")]
[Authorize(Policy = TraceabilityPolicies.AuditTrail)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class AuditTrailController(IAuditTrailService audit) : ControllerBase
{
    private const string ProblemJson = "application/problem+json";
    // Far beyond any real page: it only keeps the row offset from overflowing.
    private const int MaxPage = 1_000_000;

    private readonly IAuditTrailService _audit = audit;

    /// <summary>
    /// One page of audit entries matching every filter given, 50 to a page, newest first, with
    /// the total count.
    /// </summary>
    /// <remarks>
    /// Each entry has its time, user, action, checkpoint and batch. Entries come from the events
    /// Traceability consumed, the records in each batch's upstream snapshot once resolved, and
    /// Traceability's own record of batches opened and exports. Actions done upstream without an
    /// event are not here: GET /api/audit/coverage lists what is and is not. No match is a 200
    /// with an empty page and a message.
    /// </remarks>
    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="page">Which page, from 1. Default 1.</param>
    /// <response code="200">The page. Possibly empty, with a message.</response>
    /// <response code="400">A filter is invalid: from after to, or text too long.</response>
    [HttpGet]
    [ProducesResponseType(typeof(AuditTrailPageDto), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest, ProblemJson)]
    public async Task<ActionResult<AuditTrailPageDto>> Search(
        [FromQuery] AuditTrailQuery query,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1)
    {
        if (page is < 1 or > MaxPage)
        {
            ModelState.AddModelError(nameof(page), $"page must be between 1 and {MaxPage}.");
        }

        var (filter, errors) = AuditTrailFilter.Resolve(query);
        foreach (var (parameter, message) in errors)
        {
            ModelState.AddModelError(parameter, message);
        }

        return filter is null || !ModelState.IsValid
            ? ValidationProblem(ModelState)
            : Ok(await _audit.SearchAsync(filter, page, cancellationToken));
    }

    /// <summary>
    /// Which actions the audit trail covers, with their checkpoint and source, and which it does
    /// not, with the reason.
    /// </summary>
    /// <remarks>
    /// The trail is built from the events Traceability consumes and the upstream snapshot it
    /// caches, so the actions covered are the actions that have events. The same list is in
    /// docs/audit-trail.md.
    /// </remarks>
    /// <response code="200">The coverage list.</response>
    [HttpGet("coverage")]
    [ProducesResponseType(typeof(AuditCoverageDto), StatusCodes.Status200OK, "application/json")]
    public ActionResult<AuditCoverageDto> Coverage() => Ok(AuditCoverage.Dto());
}
