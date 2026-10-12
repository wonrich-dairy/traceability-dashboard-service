namespace TraceabilityService.Application;

/// <summary>
/// An event that cannot be applied yet but may succeed later. The consumer retries it and,
/// when the retries run out, sends it to its dead-letter topic. Any other exception from a
/// handler means the event itself cannot be applied.
/// </summary>
public class RetryableEventException(string message) : Exception(message);

/// <summary>
/// The event is for a batch that is not in batch_records yet: SCRUM-130 creates batches
/// from Processing's events, and a lab event can get here first.
/// </summary>
public sealed class BatchNotFoundException(string batchId)
    : RetryableEventException($"Batch '{batchId}' is not in batch_records yet.")
{
    public string BatchId { get; } = batchId;
}
