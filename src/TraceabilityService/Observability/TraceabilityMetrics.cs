using Prometheus;

namespace TraceabilityService.Observability;

/// <summary>
/// Prometheus metrics for Traceability Service, served on GET /metrics (SCRUM-127).
///
/// Request metrics follow the Wonrich convention &lt;service&gt;_http_* with the labels method, endpoint and
/// status, so the shared dashboard and the HighErrorRate alert pick the service up by metric-name pattern.
/// The pipeline metrics below are for the event consumers: call them from the consumer code.
/// The service and environment labels are added by Prometheus from the scrape job, not here.
/// </summary>
public static class TraceabilityMetrics
{
    private static readonly string[] RequestLabels = { "method", "endpoint", "status" };

    public static readonly Counter HttpRequests = Metrics.CreateCounter(
        "traceability_http_requests_total",
        "HTTP requests handled, by endpoint and status code.",
        new CounterConfiguration { LabelNames = RequestLabels });

    /// <summary>Server errors (5xx) only. A 4xx is the caller's mistake, not the service failing.</summary>
    public static readonly Counter HttpRequestErrors = Metrics.CreateCounter(
        "traceability_http_request_errors_total",
        "HTTP requests that ended in a server error (5xx), by endpoint.",
        new CounterConfiguration { LabelNames = RequestLabels });

    public static readonly Histogram HttpRequestDuration = Metrics.CreateHistogram(
        "traceability_http_request_duration_seconds",
        "HTTP request duration in seconds, by endpoint and status code.",
        new HistogramConfiguration
        {
            LabelNames = RequestLabels,
            Buckets = Histogram.ExponentialBuckets(start: 0.005, factor: 2, count: 12)
        });

    // ── Event pipeline (consumers) ───────────────────────────────────────────

    /// <summary>Events consumed and projected, by source topic.</summary>
    public static readonly Counter EventsConsumed = Metrics.CreateCounter(
        "traceability_events_consumed_total",
        "Events consumed from Kafka, by topic.",
        new CounterConfiguration { LabelNames = new[] { "topic" } });

    /// <summary>Events that could not be processed and were parked on a dead-letter topic.</summary>
    public static readonly Counter EventsDeadLettered = Metrics.CreateCounter(
        "traceability_events_dead_lettered_total",
        "Events sent to a dead-letter topic, by source topic.",
        new CounterConfiguration { LabelNames = new[] { "topic" } });

    /// <summary>Unix time (seconds) of the newest event visible in the store; alert when it goes stale.</summary>
    public static readonly Gauge NewestProjectedEventTimestamp = Metrics.CreateGauge(
        "traceability_newest_projected_event_timestamp_seconds",
        "Event time of the newest event projected into the store, as Unix seconds.");

    /// <summary>Call after an event is projected. Records the count and the newest event time.</summary>
    public static void RecordEventProjected(string topic, DateTimeOffset eventTime)
    {
        EventsConsumed.WithLabels(topic).Inc();
        if (eventTime.ToUnixTimeSeconds() > NewestProjectedEventTimestamp.Value)
            NewestProjectedEventTimestamp.Set(eventTime.ToUnixTimeSeconds());
    }

    /// <summary>Call when an event is parked on a dead-letter topic.</summary>
    public static void RecordDeadLetter(string topic) => EventsDeadLettered.WithLabels(topic).Inc();
}
