using TraceabilityService.Api.Contracts;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// Ranks the societies behind a set of batches by how close their milk came to being
/// rejected at the MCC gate, from the batches' upstream snapshots. Pure, so the ranking is
/// unit-tested without a database. The order matches the MCC and Intake Service's own:
/// tightest margin first, societies with no score last.
/// </summary>
public static class SocietyRanking
{
    public static List<MarginalSocietyDto> Rank(
        IEnumerable<(string BatchId, UpstreamSnapshotPayload Payload)> snapshots,
        int limit)
    {
        var consignments = snapshots.SelectMany(s => s.Payload.Tanks
            .SelectMany(t => t.Consignments)
            .Select(c => (s.BatchId, Consignment: c)));

        return [.. consignments
            .GroupBy(x => x.Consignment.Society.Code)
            .Select(society =>
            {
                // A consignment in two batches (a split dispatch) is the same milk: count it
                // once, and score it once.
                var distinct = society
                    .GroupBy(x => x.Consignment.Reference)
                    .Select(g => g.First().Consignment)
                    .ToList();

                var tightest = distinct
                    .Where(c => c.Panel?.TightestMargin is not null)
                    .OrderBy(c => c.Panel!.TightestMargin)
                    .ThenBy(c => c.Reference)
                    .FirstOrDefault();

                return new MarginalSocietyDto
                {
                    SocietyCode = society.Key,
                    SocietyName = distinct.Select(c => c.Society.Name).FirstOrDefault(n => n is not null),
                    ConsignmentCount = distinct.Count,
                    BatchCount = society.Select(x => x.BatchId).Distinct().Count(),
                    TightestMargin = tightest?.Panel!.TightestMargin,
                    TightestMeasure = tightest?.Panel!.TightestMeasure,
                };
            })
            .OrderBy(s => s.TightestMargin is null)
            .ThenBy(s => s.TightestMargin)
            .ThenBy(s => s.SocietyCode, StringComparer.Ordinal)
            .Take(limit)];
    }
}
