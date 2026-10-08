using OpenTelemetry.Metrics;

namespace TraceabilityService.Api.Infrastructure.Observability;

/// <summary>
/// Observability wiring for Traceability Service, copied from Processing Service (SCRUM-90).
/// - JSON logs with correlation ID across HTTP (console formatter + scopes set in appsettings.json Logging:Console)
/// - Log level per env (Information in Dev, Warning in Prod via appsettings)
/// - Prometheus /metrics endpoint exporting the real Wonrich.TraceabilityService meter values
/// - Request count/duration/error per endpoint
/// - No PII/connection strings in logs
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddTraceabilityObservability(this IServiceCollection services, IConfiguration configuration)
    {
        // Metrics
        services.AddMetrics();
        services.AddSingleton<TraceabilityMetrics>();

        // Processing's /metrics returned hardcoded zeros (known gap in processing-service docs/observability.md).
        // Exporting the same meter through OpenTelemetry fixes that without touching the instrumentation.
        services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddMeter(TraceabilityMetrics.MeterName)
                // OTel's default buckets (0-10000) are sized for milliseconds; this histogram records seconds,
                // so without this every request lands in le="5" and the p50/p95/p99 panels are meaningless
                .AddView("traceability_http_request_duration_seconds", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
                })
                .AddPrometheusExporter());

        // HttpContextAccessor needed for CorrelationIdHandler
        services.AddHttpContextAccessor();
        services.AddTransient<CorrelationIdHandler>();

        // Logging: JSON console formatter with IncludeScopes, so the CorrelationId scope pushed by
        // CorrelationIdMiddleware lands on every line (configured in appsettings.json Logging:Console, same as Quality Lab)
        // Log level per env: appsettings.json Logging:LogLevel:Default = Information, Production overrides to Warning
        services.AddLogging(logging =>
        {
            // Ensure no PII: filter out ConnectionStrings and Auth:SigningKey from logs
            logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Connection", LogLevel.Warning);
            logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        });

        return services;
    }

    public static IApplicationBuilder UseTraceabilityObservability(this IApplicationBuilder app)
    {
        app.UseCorrelationId();
        app.UseRequestMetrics();
        return app;
    }

    public static IEndpointRouteBuilder MapTraceabilityMetrics(this IEndpointRouteBuilder endpoints)
    {
        // Prometheus exposition format - /metrics endpoint (SCRUM-90 AC)
        endpoints.MapPrometheusScrapingEndpoint("/metrics")
            .AllowAnonymous()
            .WithDisplayName("Prometheus Metrics");

        return endpoints;
    }
}
