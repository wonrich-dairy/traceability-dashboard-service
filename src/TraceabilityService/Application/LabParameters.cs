using TraceabilityService.Api.Contracts;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// The Quality Lab parameters a batch search row and its export show (SCRUM-139), and which
/// product lines measure each. The panel shape is Quality Lab's: every line measures fat and
/// pH; only the liquid lines (FM, FLM) measure SNF and corrected CLR; only the fermented lines
/// (SY, SK, DY, CD) grade texture. Pure, so the rules are unit-tested without a database.
/// </summary>
public static class LabParameters
{
    public const string Fat = "FatPercent";
    public const string Ph = "Ph";
    public const string Snf = "SNF";
    public const string Clr = "CorrectedCLR";
    public const string Texture = "Texture";

    private static readonly string[] LiquidLines = ["FM", "FLM"];
    private static readonly string[] FermentedLines = ["SY", "SK", "DY", "CD"];

    // In column order. Each parameter with the lines that measure it, the names Quality Lab's
    // out-of-spec details give it (LabDeviations), and the reason codes that point at it.
    private static readonly Definition[] Definitions =
    [
        new(Fat, "Fat %", [.. LiquidLines, .. FermentedLines], ["LOW_FAT", "HIGH_FAT"]),
        new(Ph, "pH", [.. LiquidLines, .. FermentedLines], ["LOW_PH", "HIGH_PH"]),
        new(Snf, "SNF %", LiquidLines, ["LOW_SNF"]),
        new(Clr, "Corrected CLR", LiquidLines, ["LOW_CLR"]),
        new(Texture, "Texture", FermentedLines, ["SENSORY_TEXTURE"]),
    ];

    // The parameters in column order, with the export's heading for each.
    public static IReadOnlyList<(string Parameter, string Heading)> Columns { get; } =
        [.. Definitions.Select(d => (d.Parameter, d.Heading))];

    /// <summary>
    /// Every parameter for one batch, in column order. <paramref name="current"/> is the
    /// deviations of its current determination only; a superseded one's never count.
    /// </summary>
    public static List<LabParameterDto> Assess(string productLine, LabResult labResult, IReadOnlyCollection<DeviationRecord> current) =>
        [.. Definitions.Select(definition => Assess(definition, productLine, labResult, current))];

    private static LabParameterDto Assess(
        Definition definition, string productLine, LabResult labResult, IReadOnlyCollection<DeviationRecord> current)
    {
        if (!definition.Lines.Contains(productLine, StringComparer.OrdinalIgnoreCase))
        {
            return new LabParameterDto { Parameter = definition.Parameter, State = LabParameterState.NotApplicable };
        }

        if (labResult == LabResult.Pending)
        {
            return new LabParameterDto { Parameter = definition.Parameter, State = LabParameterState.Pending };
        }

        // The deviation naming the parameter is preferred, since it carries the reading; a
        // reason code alone (e.g. a texture fault) still marks it.
        var deviation = current.FirstOrDefault(d => string.Equals(d.Parameter, definition.Parameter, StringComparison.OrdinalIgnoreCase))
            ?? current.FirstOrDefault(d => d.ReasonCode is not null && definition.ReasonCodes.Contains(d.ReasonCode, StringComparer.OrdinalIgnoreCase));

        return deviation is null
            ? new LabParameterDto { Parameter = definition.Parameter, State = LabParameterState.NoDeviation }
            : new LabParameterDto
            {
                Parameter = definition.Parameter,
                State = LabParameterState.OutOfSpec,
                ObservedValue = deviation.ObservedValue,
                Limit = deviation.Limit,
            };
    }

    private sealed record Definition(string Parameter, string Heading, string[] Lines, string[] ReasonCodes);
}
