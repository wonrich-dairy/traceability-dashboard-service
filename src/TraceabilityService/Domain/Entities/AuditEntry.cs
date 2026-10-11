namespace TraceabilityService.Domain.Entities;

/// <summary>
/// Who did what to which record, and when. Not linked to the batch tables by a
/// foreign key, so entries outlive the rows they describe.
/// </summary>
public class AuditEntry
{
    public long Id{get; set;}

    public DateTime OccurredAt{get; set;}

    // The user ID from the token, or the consumer's name for changes made by
    // event processing.
    public required string Actor{get; set;}

    public required string Action{get; set;}

    public required string EntityType{get; set;}

    public required string EntityId{get; set;}

    // What changed, as JSON.
    public string? Details{get; set;}

    public string? CorrelationId{get; set;}
}
