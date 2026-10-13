// Response contracts of the MCC and Intake Service, copied from the wonrich-dairy/mcc-intake-service
// repository at commit c83508e4b110aa2abda543b98d6f0450c0c11630 (the files last changed in d2dc8bb):
//   src/MccIntakeService/Application/Traceability/BatchTraceService.cs   BatchTraceView, TracedTankView,
//                                                                          TracedConsignmentView,
//                                                                          TracedQualityTestView, SocietyRiskView
//   src/MccIntakeService/Application/Traceability/MarginToThreshold.cs   MeasureMargin, MarginToThreshold.Unknown
//   src/MccIntakeService/Application/Factory/FactoryIntakeService.cs     BatchView
//   src/MccIntakeService/Api/Contracts/ProblemDetailsContracts.cs        IntakeProblemDetails (Code, TraceId)
// Copy again from that repository when it changes; do not edit the shapes here on their own.
// The service writes enums as names and camelCase JSON, which these read with JsonSerializerDefaults.Web.

namespace TraceabilityService.Infrastructure.MccIntake;

/// <summary>The gate results recorded for a consignment.</summary>
public sealed record TracedQualityTestView(
    decimal FatPercent,
    decimal RawLactometerReading,
    decimal TemperatureCelsius,
    decimal WaterPercent,
    string KqColour,
    decimal CorrectedClr,
    decimal Snf,
    decimal TotalSolids,
    string StabilityGrade,
    string PassedAlcoholAt,
    string Verdict,
    string? TestedBy,
    DateTime TestedAtUtc,
    IReadOnlyList<MeasureMargin> Margins);

/// <summary>One consignment that contributed to the batch.</summary>
/// <param name="Missing">
/// What could not be resolved for this consignment. Empty when everything upstream was present.
/// </param>
public sealed record TracedConsignmentView(
    string Reference,
    string SocietyCode,
    string SocietyName,
    IReadOnlyList<string> CanLabels,
    decimal QuantityLitres,
    decimal QuantityKg,
    DateTime ArrivalAtLocal,
    string? RegisteredBy,
    DateTime RegisteredAtUtc,
    DateTime PouredAtUtc,
    string? PouredBy,
    TracedQualityTestView? QualityTest,
    decimal TightestMargin,
    IReadOnlyList<string> Missing);

/// <summary>One tank the batch drew from.</summary>
public sealed record TracedTankView(
    string TankCode,
    string TankName,
    decimal QuantityDrawnLitres,
    IReadOnlyList<TracedConsignmentView> Consignments,
    IReadOnlyList<string> Missing);

/// <summary>A society's standing across everything it contributed to this batch.</summary>
public sealed record SocietyRiskView(
    string SocietyCode,
    string SocietyName,
    int ConsignmentCount,
    decimal TightestMargin,
    string? TightestMeasure);

/// <summary>A batch resolved back to everything that went into it (SCRUM-12).</summary>
public sealed record BatchTraceView(
    string BatchReference,
    DateOnly BatchDate,
    DateTime CreatedAtUtc,
    DateTime ArrivedAtLocal,
    string? ScreenedBy,
    DateTime ScreenedAtUtc,
    string DispatchNoteReference,
    string BowserRegistration,
    string DriverName,
    DateTime DispatchedAtLocal,
    string? DispatchedBy,
    DateTime DispatchRecordedAtUtc,
    decimal TotalDispatchedLitres,
    IReadOnlyList<TracedTankView> Tanks,
    IReadOnlyList<SocietyRiskView> SocietiesByMargin,
    IReadOnlyList<string> Missing);

/// <summary>How close one measure sat to the limit that would have rejected it.</summary>
/// <param name="Measure">What was measured.</param>
/// <param name="Value">The recorded value.</param>
/// <param name="Threshold">The limit it was judged against.</param>
/// <param name="Margin">
/// How much room it had, as a fraction of the scale. Zero means it sat exactly on the limit;
/// negative means it was past it.
/// </param>
public sealed record MeasureMargin(string Measure, string Value, string Threshold, decimal Margin);

public static class MarginToThreshold
{
    /// <summary>Score used when a consignment has no gate results to judge.</summary>
    public const decimal Unknown = -1m;
}

/// <summary>A production batch as read back.</summary>
public sealed record BatchView(
    string Reference,
    string DispatchNoteReference,
    DateOnly BatchDate,
    DateTime ArrivedAtLocal,
    decimal ArrivalTemperatureCelsius,
    string? ScreenedBy,
    DateTime CreatedAtUtc);

/// <summary>
/// The problem+json body the service returns when a request breaks a domain rule. Only the
/// members this service reads; the RFC 9457 ones (title, status, detail) are ignored.
/// </summary>
public sealed class IntakeProblemDetails
{
    /// <summary>The 404 code for a batch or dispatch note that does not exist.</summary>
    public const string EntityNotFound = "entity_not_found";

    /// <summary>
    /// Stable, machine-readable identifier for the rule that was broken. Branch on this rather
    /// than on the detail, which is prose meant for a human.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Correlation id for the request, for matching against the service logs.</summary>
    public string? TraceId { get; set; }
}
