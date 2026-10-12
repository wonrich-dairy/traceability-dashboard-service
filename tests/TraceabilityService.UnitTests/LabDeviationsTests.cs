using TraceabilityService.Application;
using Wonrich.QualityLab.Contracts.Events;

namespace TraceabilityService.UnitTests;

/// <summary>
/// The deviation rows a BatchFailed produces (SCRUM-131): one per out-of-spec parameter,
/// plus one per reason code no parameter accounts for.
/// </summary>
public class LabDeviationsTests
{
    [Fact]
    public void Each_out_of_spec_parameter_is_a_deviation_with_its_reason_code()
    {
        var deviations = LabDeviations.From(Failed(
            ["LOW_FAT", "LOW_PH"],
            new OutOfSpecDetail("FatPercent", 3.20m, "Min", 3.50m),
            new OutOfSpecDetail("Ph", 6.40m, "Min", 6.60m)));

        Assert.Equal(
            [
                new LabDeviation("LOW_FAT", "FatPercent", 3.20m, 3.50m, "Below the minimum"),
                new LabDeviation("LOW_PH", "Ph", 6.40m, 6.60m, "Below the minimum"),
            ],
            deviations);
    }

    [Fact]
    public void A_reason_code_with_no_parameter_is_a_deviation_of_its_own()
    {
        var deviations = LabDeviations.From(Failed(
            ["HIGH_FAT", "SENSORY_TASTE"],
            new OutOfSpecDetail("FatPercent", 4.90m, "Max", 4.50m)));

        Assert.Equal(
            [
                new LabDeviation("HIGH_FAT", "FatPercent", 4.90m, 4.50m, "Above the maximum"),
                new LabDeviation("SENSORY_TASTE", null, null, null, null),
            ],
            deviations);
    }

    [Fact]
    public void A_parameter_keeps_its_own_reason_code_when_the_analyst_listed_a_general_one()
    {
        var deviations = LabDeviations.From(Failed(
            ["OUT_OF_SPEC"],
            new OutOfSpecDetail("SNF", 8.10m, "Min", 8.25m)));

        Assert.Equal(["LOW_SNF", "OUT_OF_SPEC"], deviations.Select(d => d.ReasonCode));
    }

    [Fact]
    public void An_unknown_reason_code_is_kept_as_sent()
    {
        var deviation = Assert.Single(LabDeviations.From(Failed(["XYZ"])));

        Assert.Equal("XYZ", deviation.ReasonCode);
        Assert.Equal("XYZ", deviation.AggregateReasonCode);
    }

    [Fact]
    public void An_unknown_parameter_has_no_reason_code_and_is_counted_as_unspecified()
    {
        var deviation = Assert.Single(LabDeviations.From(Failed(
            [],
            new OutOfSpecDetail("Lactose", 5.10m, "Max", 5.00m))));

        Assert.Null(deviation.ReasonCode);
        Assert.Equal("Lactose", deviation.Parameter);
        Assert.Equal("Unspecified", deviation.AggregateReasonCode);
    }

    [Fact]
    public void Parameter_names_and_reason_codes_match_without_regard_to_case()
    {
        var deviations = LabDeviations.From(Failed(
            ["low_clr"],
            new OutOfSpecDetail("correctedclr", 26.00m, "min", 27.00m)));

        var deviation = Assert.Single(deviations);
        Assert.Equal("LOW_CLR", deviation.ReasonCode);
    }

    private static BatchFailedEvent Failed(string[] reasonCodes, params OutOfSpecDetail[] details) => new()
    {
        BatchId = "262-FM-B",
        ReasonCodes = reasonCodes,
        OutOfSpecDetails = details,
    };
}
