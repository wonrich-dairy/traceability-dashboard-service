using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;
using static TraceabilityService.Domain.Entities.UpstreamSnapshotPayload;

namespace TraceabilityService.UnitTests;

/// <summary>
/// The marginal societies panel's ranking (SCRUM-135, TR-12 descoped): from the snapshots
/// of the batches in range, tightest margin first and unscored societies last.
/// </summary>
public class SocietyRankingTests
{
    [Fact]
    public void Societies_are_ranked_tightest_margin_first_with_unscored_ones_last()
    {
        var ranked = SocietyRanking.Rank(
            [
                ("262-FM-A", Payload(
                    Consignment("C1", "VS001", 0.0500m, "Snf"),
                    Consignment("C2", "VS002", 0.0100m, "FatPercent"),
                    Consignment("C3", "VS003", margin: null, measure: null))),
            ],
            limit: 10);

        Assert.Equal(["VS002", "VS001", "VS003"], ranked.Select(s => s.SocietyCode));
        Assert.Equal([0.0100m, 0.0500m, null], ranked.Select(s => s.TightestMargin));
        Assert.Equal(["FatPercent", "Snf", null], ranked.Select(s => s.TightestMeasure));
    }

    [Fact]
    public void A_consignment_in_two_batches_counts_once_but_both_batches_count()
    {
        // Processing split one dispatch into two batches, so both snapshots carry C1.
        var ranked = SocietyRanking.Rank(
            [
                ("262-FM-A", Payload(Consignment("C1", "VS001", 0.0500m, "Snf"))),
                ("262-FM-D", Payload(Consignment("C1", "VS001", 0.0500m, "Snf"))),
            ],
            limit: 10);

        var society = Assert.Single(ranked);
        Assert.Equal(1, society.ConsignmentCount);
        Assert.Equal(2, society.BatchCount);
    }

    [Fact]
    public void A_society_scores_its_tightest_consignment_and_names_its_measure()
    {
        var ranked = SocietyRanking.Rank(
            [
                ("262-FM-A", Payload(
                    Consignment("C1", "VS001", 0.0800m, "CorrectedClr"),
                    Consignment("C2", "VS001", 0.0200m, "Snf"),
                    Consignment("C3", "VS001", margin: null, measure: null))),
            ],
            limit: 10);

        var society = Assert.Single(ranked);
        Assert.Equal(3, society.ConsignmentCount);
        Assert.Equal(0.0200m, society.TightestMargin);
        Assert.Equal("Snf", society.TightestMeasure);
        Assert.Equal("Society VS001", society.SocietyName);
    }

    [Fact]
    public void Equal_margins_are_ordered_by_society_code_and_the_limit_applies()
    {
        var ranked = SocietyRanking.Rank(
            [
                ("262-FM-A", Payload(
                    Consignment("C1", "VS020", 0.0300m, "Snf"),
                    Consignment("C2", "VS010", 0.0300m, "Snf"),
                    Consignment("C3", "VS030", 0.0300m, "Snf"))),
            ],
            limit: 2);

        Assert.Equal(["VS010", "VS020"], ranked.Select(s => s.SocietyCode));
    }

    [Fact]
    public void No_snapshots_means_no_societies()
    {
        Assert.Empty(SocietyRanking.Rank([], limit: 10));
    }

    private static UpstreamSnapshotPayload Payload(params ConsignmentPart[] consignments) => new()
    {
        BatchReference = "WR-20260919-01",
        Dispatch = new DispatchPart { Reference = "DN-20260919-01", DispatchedAt = DateTime.UtcNow, TotalLitres = 900m },
        Intake = new IntakePart { ArrivedAt = DateTime.UtcNow, ScreenedAt = DateTime.UtcNow },
        Tanks = [new TankPart { Code = "T1", QuantityDrawnLitres = 900m, Consignments = [.. consignments] }]
    };

    private static ConsignmentPart Consignment(string reference, string society, decimal? margin, string? measure) => new()
    {
        Reference = reference,
        Society = new SocietyPart { Code = society, Name = $"Society {society}" },
        VolumeLitres = 300m,
        RegisteredAt = DateTime.UtcNow,
        Panel = new PanelPart
        {
            FatPercentage = 4.00m,
            SnfPercentage = 8.60m,
            ClrReading = 28.00m,
            TemperatureC = 5.00m,
            Verdict = "Accepted",
            TightestMargin = margin,
            TightestMeasure = measure
        }
    };
}
