using TraceabilityService.Api.Contracts;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// A batch's lab result as the API reports it (SCRUM-131): the result of its active
/// determination, or Pending when it has none. A superseded determination never counts.
/// </summary>
public static class LabResults
{
    public static LabResult From(DeterminationResult? active) => active switch
    {
        DeterminationResult.Cleared => LabResult.Cleared,
        DeterminationResult.Failed => LabResult.Failed,
        _ => LabResult.Pending,
    };

    // Newest first, in case earlier data left two active rows.
    public static LabResult Of(IEnumerable<LabDetermination> determinations) => From(
        determinations
            .Where(d => !d.IsSuperseded)
            .OrderByDescending(d => d.DeterminedAt)
            .Select(d => (DeterminationResult?)d.Result)
            .FirstOrDefault());
}
