namespace TraceabilityService.Domain.Entities;

public enum Status
{
    Chilling,
    Processing,
    AwaitingLab,
    Released, 
    Rejected
}
public enum Result
{
    Pass,
    Fail
}
public class BatchTrace
{
    public int Id{get; set;}

    public required string BatchId{get; set;}

    public DateOnly ProductionDate{get; set;}

    // Every batch has a status from the moment it is created, so this is never null.
    public Status CurrentStatus{get; set;}

    // Null until the lab has tested the batch and returned a verdict.
    public Result? FinalLabOutcome{get; set;}

    public DateTime CreatedAt{get; set;}

    public DateTime UpdatedAt{get; set;}

    // Navigations off the BatchId alternate key, so a trace can be pulled in one
    // query. Both are owned by the batch and cascade-delete with it.
    public ICollection<CheckpointRecord> Checkpoints{get; set;} = [];

    public ICollection<BatchSource> Sources{get; set;} = [];
}
