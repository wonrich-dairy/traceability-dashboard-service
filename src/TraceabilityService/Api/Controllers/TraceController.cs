using Microsoft.AspNetCore.Mvc;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Application;

namespace TraceabilityService.Api.Controllers;

[ApiController]
[Route("api/trace")]
public class TraceController(ITraceService trace) : ControllerBase
{
    private const string ProblemJson = "application/problem+json";
    private readonly ITraceService _trace = trace;

    [HttpGet("{batchId}")]
    [ProducesResponseType(typeof(BatchTraceResponseDto), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemJson)]
    public async Task<ActionResult<BatchTraceResponseDto>> GetBatchTrace(
        string batchId,
        CancellationToken cancellationToken)
    {
        var trace = await _trace.GetBatchTraceAsync(batchId, cancellationToken);

        // Null from the service means no batch carries that BatchId, which is a
        // 404 rather than an empty 200: the caller asked for a specific batch.
        if (trace is null)
        {
            return Problem(
                title: "Batch not found",
                detail: $"No batch trace exists for batch ID '{batchId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(trace);
    }
}
