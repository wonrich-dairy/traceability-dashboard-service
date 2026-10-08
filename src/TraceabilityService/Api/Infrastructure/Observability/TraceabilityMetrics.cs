using System.Diagnostics.Metrics;

namespace TraceabilityService.Api.Infrastructure.Observability;

/// <summary>
/// Custom Prometheus metrics for Traceability Service, copied from Processing Service (SCRUM-90).
/// - Request count/duration/error per endpoint (via RequestMetricsMiddleware)
/// Exposed via /metrics in Prometheus exposition format.
/// </summary>
public sealed class TraceabilityMetrics
{
    public const string MeterName = "Wonrich.TraceabilityService";
    private readonly Meter _meter;

    // Request metrics
    public readonly Counter<long> RequestsTotal;
    public readonly Histogram<double> RequestDurationSeconds;
    public readonly Counter<long> RequestErrorsTotal;

    public TraceabilityMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        RequestsTotal = _meter.CreateCounter<long>("traceability_http_requests_total", "requests", "Total HTTP requests per endpoint");
        RequestDurationSeconds = _meter.CreateHistogram<double>("traceability_http_request_duration_seconds", "s", "HTTP request duration per endpoint");
        RequestErrorsTotal = _meter.CreateCounter<long>("traceability_http_request_errors_total", "errors", "Total HTTP errors per endpoint");
    }
}
