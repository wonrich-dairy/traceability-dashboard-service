using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure;
using TraceabilityService.Application;

namespace TraceabilityService.Api.Controllers;

/// <summary>
/// The unified batch record (SCRUM-133) and its backward trace (SCRUM-134). Quality
/// Control Officers and System Administrators only: no token is 401, any other role is 403.
/// </summary>
[ApiController]
[Route("api/batches")]
[Authorize(Policy = TraceabilityPolicies.QualityControl)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class BatchesController(IBatchRecordService batches, IBatchTraceService traces) : ControllerBase
{
    private const string ProblemJson = "application/problem+json";
    private readonly IBatchRecordService _batches = batches;
    private readonly IBatchTraceService _traces = traces;

    /// <summary>
    /// The full record for one batch: its timeline with each event's deviations, and
    /// its upstream snapshot.
    /// </summary>
    /// <param name="batchId">Processing's batch code, e.g. 262-FM-B.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The batch.</response>
    /// <response code="404">No batch carries that ID.</response>
    [HttpGet("{batchId}")]
    [ProducesResponseType(typeof(BatchRecordDto), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemJson)]
    public async Task<ActionResult<BatchRecordDto>> GetBatch(
        string batchId,
        CancellationToken cancellationToken)
    {
        var batch = await _batches.GetAsync(batchId, cancellationToken);

        // Null from the service means no batch carries that BatchId, which is a 404
        // rather than an empty 200: the caller asked for a specific batch.
        if (batch is null)
        {
            return Problem(
                title: "Batch not found",
                detail: $"No batch exists with batch ID '{batchId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(batch);
    }

    /// <summary>
    /// The backward trace for one batch: its four checkpoints, MCC to Quality Lab, each
    /// within spec, deviated or missing data, the checkpoint the first deviation came
    /// from, its holds, and the tanks and societies its milk came from.
    /// </summary>
    /// <remarks>
    /// The origin is the earliest checkpoint in stage order that deviated, not the
    /// deviation with the earliest time. The same data always gives the same trace.
    /// </remarks>
    /// <param name="batchId">Processing's batch code, e.g. 262-FM-B.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The trace.</response>
    /// <response code="404">No batch carries that ID.</response>
    [HttpGet("{batchId}/trace")]
    [ProducesResponseType(typeof(BatchTraceResponse), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemJson)]
    public async Task<ActionResult<BatchTraceResponse>> GetTrace(
        string batchId,
        CancellationToken cancellationToken)
    {
        var trace = await _traces.GetAsync(batchId, cancellationToken);

        if (trace is null)
        {
            return Problem(
                title: "Batch not found",
                detail: $"No batch exists with batch ID '{batchId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(trace);
    }

    /// <summary>
    /// The batches from one dispatch note, or with one factory batch reference.
    /// </summary>
    /// <remarks>
    /// A list, not a single batch: Processing can split one dispatch across several
    /// batches, and they all share its dispatch note and batch reference. No match is
    /// an empty list, not a 404. Give either filter or both; with both, a batch must
    /// match both.
    /// </remarks>
    /// <param name="dispatchRef">MCC dispatch number, e.g. DN-20260919-02.</param>
    /// <param name="batchReference">Factory intake batch reference, e.g. WR-20260919-02.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The matching batches, oldest first. Possibly empty.</response>
    /// <response code="400">Neither filter was given.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<BatchSummaryDto>), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest, ProblemJson)]
    public async Task<ActionResult<IReadOnlyList<BatchSummaryDto>>> FindBatches(
        [FromQuery] string? dispatchRef,
        [FromQuery] string? batchReference,
        CancellationToken cancellationToken)
    {
        // Without a filter this would list every batch; that is the dashboard's job, with
        // its own paging, not this lookup's.
        if (string.IsNullOrWhiteSpace(dispatchRef) && string.IsNullOrWhiteSpace(batchReference))
        {
            ModelState.AddModelError(
                nameof(dispatchRef),
                "Give dispatchRef or batchReference (or both) to look batches up by.");
            return ValidationProblem(ModelState);
        }

        return Ok(await _batches.FindAsync(dispatchRef, batchReference, cancellationToken));
    }
}
