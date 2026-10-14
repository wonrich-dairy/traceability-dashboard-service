namespace TraceabilityService.Domain.Events;

/// <summary>
/// Consumer-side v1 shape for the MCC ConsignmentTested event. The shared publisher contract
/// can replace this DTO without changing the projection handler when it is available here.
/// </summary>
public sealed class ConsignmentTestedEvent
{
    public Guid EventId { get; set; }
    public string SchemaVersion { get; set; } = "v1";
    public string? ConsignmentReference { get; set; }
    public SocietyDetails? Society { get; set; }
    public List<CanDetails> Cans { get; set; } = [];
    public decimal QuantityLitres { get; set; }
    public QualityPanelDetails? Panel { get; set; }
    public string? Verdict { get; set; }
    public string? FailedParameter { get; set; }
    public string? FailedValue { get; set; }
    public bool IsMarginal { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

public sealed class SocietyDetails
{
    public string? Code { get; set; }
    public string? Name { get; set; }
}

public sealed class CanDetails
{
    public string? Label { get; set; }
    public decimal QuantityLitres { get; set; }
}

public sealed class QualityPanelDetails
{
    public decimal FatPercent { get; set; }
    public decimal RawLactometerReading { get; set; }
    public decimal TemperatureCelsius { get; set; }
    public decimal WaterPercent { get; set; }
    public string? KqColour { get; set; }
    public bool SmellOk { get; set; }
    public bool ColourOk { get; set; }
    public bool TasteOk { get; set; }
    public decimal CorrectedClr { get; set; }
    public decimal Snf { get; set; }
    public decimal TotalSolids { get; set; }
    public string? StabilityGrade { get; set; }
    public string? PassedAlcoholAt { get; set; }
}
