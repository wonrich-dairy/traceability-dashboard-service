namespace ProcessingService.Domain.Events;

/// <summary>
/// Shared event contracts versioned - SCRUM-68, SCRUM-88
/// Schemas live in shared library and are versioned. Field renamed breaks Traceability in Sprint 4.
/// All events carry batchId, dispatchNumber, timestamp, deviation flag per AC.
/// Naming: wonrich.[owning service].[event in plural].v[schema version]
/// Version: v1 - initial
/// </summary>

/// <summary>
/// Base for all processing events - carries common fields per AC: batch ID, dispatch number, timestamp
/// </summary>
public abstract record ProcessingEventBase
{
    /// <summary>Unique event ID for consumer-side deduplication - relay is at-least-once (crash between publish and outbox commit can republish), consumers keep a seen-EventId window and skip duplicates</summary>
    public Guid EventId { get; init; } = Guid.NewGuid();

    /// <summary>Batch code [day]-[product]-[letter] e.g. 258-DY-A - business identifier, message key for ordering per key</summary>
    public string BatchId { get; init; } = string.Empty;

    /// <summary>MCC dispatch number e.g. DN-20260910-01 - traceability query filters on this</summary>
    public string DispatchNumber { get; init; } = string.Empty;

    /// <summary>UTC timestamp of event</summary>
    public DateTime TimestampUtc { get; init; }

    /// <summary>Deviation flag - true if temperature out of range or other deviation</summary>
    public bool IsDeviation { get; init; }

    /// <summary>Schema version - v1</summary>
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>Correlation ID for tracing across hop in Loki - Kafka header x-correlation-id (single canonical name), also this payload field</summary>
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// MilkAllocatedToMixingTank published on allocation, carrying both tank identifiers, quantity and product line
/// Topic: wonrich.processing.stage-events.v1 (per docs/kafka.md, processing stage events including allocation)
/// Key: batchId for ordering per batch
/// </summary>
public sealed record MilkAllocatedToMixingTankEvent : ProcessingEventBase
{
    public string SourceStoringTankId { get; init; } = string.Empty;
    public string SourceStoringTankCode { get; init; } = string.Empty;
    public string DestinationMixingTankId { get; init; } = string.Empty;
    public string DestinationMixingTankCode { get; init; } = string.Empty;
    public decimal QuantityKg { get; init; }
    public string ProductType { get; init; } = string.Empty; // SY,SK,FM,FLM,DY
    public string BatchCode { get; init; } = string.Empty; // same as BatchId
    public int BatchNumber { get; init; } // DayOfYear 1-365
    public string BatchLetter { get; init; } = string.Empty; // A-Z
    public DateTime AllocatedAtUtc { get; init; }
    public string AllocatedBy { get; init; } = string.Empty;
    public string? OverrideReason { get; init; }
}

/// <summary>
/// ProcessingStageRecorded published per stage, carrying stage type, temperature, timings and deviation flag
/// Topic: wonrich.processing.stage-events.v1
/// Key: batchId
/// </summary>
public sealed record ProcessingStageRecordedEvent : ProcessingEventBase
{
    public string MixingTankId { get; init; } = string.Empty;
    public string MixingTankCode { get; init; } = string.Empty;
    public string StageType { get; init; } = string.Empty; // Heating, Homogeniser, Pasteuriser, Cooling
    public DateTime StartTimeUtc { get; init; }
    public DateTime? EndTimeUtc { get; init; }
    public decimal EndTemperatureC { get; init; }
    public int? DurationMinutes { get; init; }
    public bool CultureAdded { get; init; }
    public DateTime? CultureAddedAtUtc { get; init; }
    public string RecordedBy { get; init; } = string.Empty;
}

/// <summary>
/// ProcessingCompleted published when a run closes (Cooling ended)
/// Topic: wonrich.processing.stage-events.v1
/// Key: batchId
/// </summary>
public sealed record ProcessingCompletedEvent : ProcessingEventBase
{
    public string MixingTankId { get; init; } = string.Empty;
    public string MixingTankCode { get; init; } = string.Empty;
    public DateTime CompletedAtUtc { get; init; }
    public int TotalDurationMinutes { get; init; }
    public bool HasDeviation { get; init; } // true if any stage had deviation
    public string CompletedBy { get; init; } = string.Empty;
}

/// <summary>
/// ProcessingHoldRaised published with reason
/// Topic: wonrich.processing.hold-events.v1
/// Key: batchId or dispatchNumber (using dispatchNumber if batch not yet allocated)
/// </summary>
public sealed record ProcessingHoldRaisedEvent : ProcessingEventBase
{
    public string HoldId { get; init; } = string.Empty; // ProcessingRun Id or QualityPanel Id
    public string Reason { get; init; } = string.Empty;
    public DateTime RaisedAtUtc { get; init; }
    public string RaisedBy { get; init; } = string.Empty;
    public string? FailedParameter { get; init; }
    public string? FailedValue { get; init; }
}

/// <summary>
/// ProcessingHoldResolved published with reason and resolution
/// Topic: wonrich.processing.hold-events.v1
/// Key: batchId or dispatchNumber
/// </summary>
public sealed record ProcessingHoldResolvedEvent : ProcessingEventBase
{
    public string HoldId { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Resolution { get; init; } = string.Empty;
    public DateTime RaisedAtUtc { get; init; }
    public DateTime ResolvedAtUtc { get; init; }
    public string ResolvedBy { get; init; } = string.Empty;
}
