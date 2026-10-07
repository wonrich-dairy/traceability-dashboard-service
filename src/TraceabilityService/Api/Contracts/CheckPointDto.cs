namespace TraceabilityService.Api.Contracts;
using TraceabilityService.Domain.Entities;

public class CheckpointDto
{
    public CheckpointName CheckpointName{get; set;}
    public DateTime RecordedAtTimestamp{get; set;}

    public decimal? FatPercentage{get; set;}
    public decimal? SnfPercentage{get; set;}
    public decimal? ClrReading{get; set;}
    public decimal? TemperatureCelsius{get; set;}
    public decimal? PH{get; set;}

    public SensoryGrade? Appearance{get; set;}
    public SensoryGrade? Texture{get; set;}
    public SensoryGrade? Taste{get; set;}
    public SensoryGrade? Colour{get; set;}
    public SensoryGrade? Smell{get; set;}
}
