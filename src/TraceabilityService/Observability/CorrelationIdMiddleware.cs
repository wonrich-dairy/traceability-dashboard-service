using System.Text.RegularExpressions;

namespace TraceabilityService.Observability;

/// <summary>
/// Correlation ID for every request (SCRUM-127, same as Quality Lab).
///
/// Reuses the caller's X-Correlation-ID when it is present and well-formed, otherwise generates one.
/// The ID goes back in the response header and into the logging scope, so every log line written
/// while handling the request carries "CorrelationId" and can be found in Loki.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();

        // Anything else is replaced rather than logged: a header value ends up verbatim in every log
        // line, so accepting arbitrary text would let a caller forge or break log entries.
        var correlationId = (incoming is not null && SafeId().IsMatch(incoming)
            ? incoming
            : Guid.NewGuid().ToString("N"))
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeId();
}

public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();
}
