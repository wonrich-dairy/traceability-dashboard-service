namespace TraceabilityService.Domain.Entities;

public enum ResolutionStatus
{
    // Not fetched yet, or the last attempt failed and will be retried.
    PendingRetry,

    Resolved,

    // Given up on: the upstream service has no such dispatch, or the retries ran
    // out. FailureReason says which.
    Unavailable
}

/// <summary>
/// What the MCC and Intake Service knows about a batch's dispatch, copied so a trace
/// can be answered without calling that service. One row per batch: the row is
/// created before the consignments are known, because the lookup is what reveals them.
/// </summary>
public class UpstreamSnapshot
{
    public int Id{get; set;}

    public required string BatchId{get; set;}

    public ResolutionStatus ResolutionStatus{get; set;}

    // The whole upstream tree as JSON: the dispatch note, the tanks it drew from
    // with the quantity drawn from each, and each tank's consignments with their
    // society and quality panel values. Null until resolved.
    public string? Payload{get; set;}

    public int AttemptCount{get; set;}

    public DateTime? LastAttemptAt{get; set;}

    public DateTime? ResolvedAt{get; set;}

    // Why the last attempt did not resolve, e.g. that upstream has no such dispatch
    // or returned an error. Kept for PendingRetry and Unavailable.
    public string? FailureReason{get; set;}
}
