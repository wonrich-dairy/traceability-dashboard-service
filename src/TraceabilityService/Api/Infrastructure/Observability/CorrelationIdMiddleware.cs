using System.Diagnostics;

namespace TraceabilityService.Api.Infrastructure.Observability;

/// <summary>
/// Propagates X-Correlation-ID across HTTP + logs, copied from Processing Service (SCRUM-90).
/// Generates GUID if missing, adds to response header, HttpContext.Items, Activity, and logger scope.
/// No PII logged.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetBaggage(HeaderName, correlationId);

        // Push to logger scope so JSON logs contain correlationId
        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId, ["TraceId"] = Activity.Current?.TraceId.ToString() ?? "" }))
        {
            await _next(context);
        }
    }
}

public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.UseMiddleware<CorrelationIdMiddleware>();
}
