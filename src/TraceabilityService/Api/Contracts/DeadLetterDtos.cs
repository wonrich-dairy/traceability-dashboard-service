using System.Text.Json.Serialization;

namespace TraceabilityService.Api.Contracts;

/// <summary>
/// A failed message inspected from a dead-letter topic (SCRUM-140).
/// </summary>
public class DeadLetterMessageDto
{
    /// <summary>
    /// The consumer group that failed to process the message.
    /// </summary>
    public required string ConsumerGroup { get; set; }

    /// <summary>
    /// The dead-letter topic where the message is parked.
    /// </summary>
    public required string Topic { get; set; }

    /// <summary>
    /// Partition on the dead-letter topic.
    /// </summary>
    public int Partition { get; set; }

    /// <summary>
    /// Offset on the dead-letter topic.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// The message key (e.g. batchId).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Key { get; set; }

    /// <summary>
    /// Unique event identifier from event headers or body.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? EventId { get; set; }

    /// <summary>
    /// Type of the event (e.g. MilkAllocatedToMixingTankEvent, BatchFailed).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? EventType { get; set; }

    /// <summary>
    /// The correlation ID carried with the event.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// The reason why processing failed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? FailureReason { get; set; }

    /// <summary>
    /// When the message was parked on the dead-letter topic (UTC).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? FailureTimestamp { get; set; }

    /// <summary>
    /// How many attempts were made before dead-lettering.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// The source topic the message originated from.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? OriginalTopic { get; set; }

    /// <summary>
    /// Partition on the original source topic.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OriginalPartition { get; set; }

    /// <summary>
    /// Offset on the original source topic.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OriginalOffset { get; set; }

    /// <summary>
    /// Raw payload value.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Payload { get; set; }
}

/// <summary>
/// Request to replay a dead-letter message (SCRUM-140).
/// </summary>
public class ReplayDeadLetterRequest
{
    /// <summary>
    /// The consumer group whose dead-letter topic holds the message.
    /// </summary>
    public required string ConsumerGroup { get; set; }

    /// <summary>
    /// Partition on the dead-letter topic (default 0).
    /// </summary>
    public int Partition { get; set; } = 0;

    /// <summary>
    /// Offset on the dead-letter topic.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Optional event ID for validation.
    /// </summary>
    public string? EventId { get; set; }
}

/// <summary>
/// Outcome of replaying a dead-letter message (SCRUM-140).
/// </summary>
public class ReplayResultDto
{
    public required string ConsumerGroup { get; set; }
    public required string TargetTopic { get; set; }
    public string? EventId { get; set; }
    public string? CorrelationId { get; set; }
    public bool Handled { get; set; }
    public bool IsDuplicate { get; set; }
    public required string Message { get; set; }
}

