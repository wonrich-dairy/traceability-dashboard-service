using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Routing;

namespace TraceabilityService.Observability;

/// <summary>
/// Records request count, duration and server errors for Prometheus, and writes one structured log
/// line per request (SCRUM-127). Runs after CorrelationIdMiddleware, so the log line carries the ID.
/// </summary>
public sealed class RequestMetricsMiddleware(RequestDelegate next, ILogger<RequestMetricsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Prometheus scrapes /metrics every 15 s; counting those would make the scraper the
        // service's busiest client and drown the real request rate.
        if (context.Request.Path.StartsWithSegments("/metrics"))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var status = StatusCodes.Status500InternalServerError; // stays 500 if the pipeline throws
        try
        {
            await next(context);
            status = context.Response.StatusCode;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            var method = SanitizeForLog(context.Request.Method);
            var endpoint = EndpointLabel(context);
            var code = status.ToString(CultureInfo.InvariantCulture);

            TraceabilityMetrics.HttpRequests.WithLabels(method, endpoint, code).Inc();
            TraceabilityMetrics.HttpRequestDuration.WithLabels(method, endpoint, code).Observe(elapsed.TotalSeconds);
            if (status >= 500)
                TraceabilityMetrics.HttpRequestErrors.WithLabels(method, endpoint, code).Inc();

            logger.LogInformation("HTTP {Method} {Endpoint} responded {StatusCode} in {ElapsedMs} ms",
                method, endpoint, status, (long)elapsed.TotalMilliseconds);
        }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The route template (e.g. "/api/batches/{id}"), never the raw path: raw paths would create a
    /// new time series for every batch ID, and requests matching no route share one "unmatched" label
    /// so scanners probing random URLs cannot do the same.
    /// </summary>
    private static string EndpointLabel(HttpContext context) =>
        context.GetEndpoint() is RouteEndpoint route
            ? "/" + (route.RoutePattern.RawText ?? "").TrimStart('/')
            : "unmatched";
}

public static class RequestMetricsMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestMetrics(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestMetricsMiddleware>();
}
