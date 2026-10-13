namespace TraceabilityService.Api.Contracts;

/// <summary>
/// Parameters for an admin projection rebuild (SCRUM-140).
/// </summary>
public record ProjectionRebuildRequest
{
    /// <summary>
    /// Custom consumer group name. If omitted, a unique group 'traceability-rebuild-{guid}' is used.
    /// </summary>
    public string? ConsumerGroup { get; set; }

    /// <summary>
    /// Whether to create a fresh projection store. Must be true; rebuilds always use an empty shadow table set.
    /// </summary>
    public bool ClearStore { get; set; } = true;

    /// <summary>
    /// Whether to re-resolve upstream snapshots from the MCC and Intake Service (SCRUM-12) during rebuild. Defaults to true.
    /// </summary>
    public bool ResolveUpstreamSnapshots { get; set; } = true;
}

/// <summary>
/// Result of an admin projection rebuild (SCRUM-140).
/// </summary>
public class ProjectionRebuildResultDto
{
    /// <summary>
    /// The consumer group used for the rebuild.
    /// </summary>
    public required string ConsumerGroup { get; set; }

    /// <summary>
    /// Total events replayed from Kafka topics across all partitions.
    /// </summary>
    public int EventsProcessed { get; set; }

    /// <summary>
    /// Upstream snapshots re-resolved from SCRUM-12.
    /// </summary>
    public int SnapshotsResolved { get; set; }

    /// <summary>
    /// Total batch records present in the rebuilt projection.
    /// </summary>
    public int BatchesCount { get; set; }

    /// <summary>
    /// Whether the live consumer groups were aligned to the captured rebuild offsets.
    /// </summary>
    public bool ConsumerOffsetsAligned { get; set; }

    /// <summary>
    /// Whether the rebuilt projection tables were atomically switched into service.
    /// </summary>
    public bool SwitchedOver { get; set; }

    /// <summary>
    /// Prefix of the previous projection tables retained for rollback after a successful cutover.
    /// </summary>
    public string? RetiredTablePrefix { get; set; }

    /// <summary>
    /// Total time taken in milliseconds.
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// Informational summary.
    /// </summary>
    public required string Message { get; set; }
}

