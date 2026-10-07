namespace TraceabilityService.Domain.Entities;

public enum CheckpointName
{
    Mcc,
    Intake,
    Processing,
    Lab
}

public enum SensoryGrade
{
    Normal,
    Abnormal
}

public class CheckpointRecord
{
    public int Id{get; set;}

    public required string BatchId{get; set;}

    public CheckpointName CheckpointName{get; set;}

    public DateTime RecordedAtTimestamp{get; set;}

    // Measurements. Nullable because no single checkpoint records all of them:
    // MCC and Intake take fat/SNF/CLR/temperature, Processing takes temperature,
    // Lab takes pH and the sensory grades below.
    public decimal? FatPercentage{get; set;}

    public decimal? SnfPercentage{get; set;}

    public decimal? ClrReading{get; set;}

    public decimal? TemperatureCelsius{get; set;}

    public decimal? PH{get; set;}

    // Sensory checks, recorded at the Lab checkpoint. Null means not assessed,
    // which is distinct from Normal.
    public SensoryGrade? Appearance{get; set;}

    public SensoryGrade? Texture{get; set;}

    public SensoryGrade? Taste{get; set;}

    public SensoryGrade? Colour{get; set;}

    public SensoryGrade? Smell{get; set;}
}
