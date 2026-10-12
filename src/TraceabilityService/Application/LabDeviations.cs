using Wonrich.QualityLab.Contracts.Events;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// The deviation_records rows a BatchFailed produces (SCRUM-131): one per out-of-spec
/// parameter, then one per reason code that no parameter accounts for, e.g. a sensory
/// failure. Pure, so the pairing is unit-tested without a database.
/// </summary>
public static class LabDeviations
{
    // Quality Lab's out-of-spec parameters (Wonrich.QualityPanel's SpecEvaluator) and the
    // reason code for each bound, from its determination reason code list.
    private static readonly Dictionary<(string Parameter, string Bound), string> ReasonFor =
        new(new ParameterBoundComparer())
        {
            [("FatPercent", "Min")] = "LOW_FAT",
            [("FatPercent", "Max")] = "HIGH_FAT",
            [("Ph", "Min")] = "LOW_PH",
            [("Ph", "Max")] = "HIGH_PH",
            [("SNF", "Min")] = "LOW_SNF",
            [("CorrectedCLR", "Min")] = "LOW_CLR",
        };

    public static IReadOnlyList<LabDeviation> From(BatchFailedEvent failed)
    {
        var deviations = new List<LabDeviation>();

        // A parameter's reason code comes from the parameter, whether or not the analyst
        // listed it: it is what went out of range. Null for a parameter this table does not
        // know, which the aggregates count as Unspecified.
        foreach (var detail in failed.OutOfSpecDetails)
        {
            deviations.Add(new LabDeviation(
                ReasonFor.GetValueOrDefault((detail.Parameter, detail.Limit)),
                detail.Parameter,
                detail.MeasuredValue,
                detail.LimitValue,
                Describe(detail.Limit)));
        }

        // Codes are kept as Quality Lab sent them, so an unfamiliar one is stored under its own name.
        var accountedFor = deviations
            .Select(d => d.ReasonCode)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var code in failed.ReasonCodes.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            if (accountedFor.Add(code))
            {
                deviations.Add(new LabDeviation(code, Parameter: null, ObservedValue: null, Limit: null, Description: null));
            }
        }

        return deviations;
    }

    private static string? Describe(string bound) => bound switch
    {
        _ when string.Equals(bound, "Min", StringComparison.OrdinalIgnoreCase) => "Below the minimum",
        _ when string.Equals(bound, "Max", StringComparison.OrdinalIgnoreCase) => "Above the maximum",
        _ => null,
    };

    private sealed class ParameterBoundComparer : IEqualityComparer<(string Parameter, string Bound)>
    {
        public bool Equals((string Parameter, string Bound) x, (string Parameter, string Bound) y) =>
            string.Equals(x.Parameter, y.Parameter, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Bound, y.Bound, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Parameter, string Bound) key) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.Parameter),
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.Bound));
    }
}

/// <summary>One lab deviation, as it is written to deviation_records.</summary>
public sealed record LabDeviation(
    string? ReasonCode,
    string? Parameter,
    decimal? ObservedValue,
    decimal? Limit,
    string? Description)
{
    // The code it is counted under in daily_reason_code_aggregates, where the code is part of the key.
    public string AggregateReasonCode => ReasonCode ?? DailyReasonCodeAggregate.Unspecified;
}
