using TraceabilityService.Api.Contracts;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.UnitTests;

/// <summary>
/// The lab parameter columns of a batch search row (SCRUM-139): always the same five, in the
/// same order; one the product line does not measure is NotApplicable whatever the lab said.
/// </summary>
public class LabParametersTests
{
    [Fact]
    public void Every_row_has_the_same_five_parameters_in_column_order()
    {
        var parameters = LabParameters.Assess("FM", LabResult.Cleared, []);

        Assert.Equal(["FatPercent", "Ph", "SNF", "CorrectedCLR", "Texture"], parameters.Select(p => p.Parameter));
        Assert.Equal(["Fat %", "pH", "SNF %", "Corrected CLR", "Texture"], LabParameters.Columns.Select(c => c.Heading));
    }

    [Theory]
    // Liquid lines: no texture grade.
    [InlineData("FM", "NoDeviation,NoDeviation,NoDeviation,NoDeviation,NotApplicable")]
    [InlineData("FLM", "NoDeviation,NoDeviation,NoDeviation,NoDeviation,NotApplicable")]
    // Fermented lines: no SNF or CLR.
    [InlineData("SY", "NoDeviation,NoDeviation,NotApplicable,NotApplicable,NoDeviation")]
    [InlineData("SK", "NoDeviation,NoDeviation,NotApplicable,NotApplicable,NoDeviation")]
    [InlineData("DY", "NoDeviation,NoDeviation,NotApplicable,NotApplicable,NoDeviation")]
    [InlineData("cd", "NoDeviation,NoDeviation,NotApplicable,NotApplicable,NoDeviation")]
    public void A_parameter_the_product_line_does_not_measure_is_not_applicable(string productLine, string expected)
    {
        var parameters = LabParameters.Assess(productLine, LabResult.Cleared, []);

        Assert.Equal(expected, string.Join(",", parameters.Select(p => p.State)));
    }

    [Fact]
    public void Before_a_determination_measured_parameters_are_pending_and_the_rest_still_not_applicable()
    {
        var parameters = LabParameters.Assess("SY", LabResult.Pending, []);

        Assert.Equal(
            [LabParameterState.Pending, LabParameterState.Pending, LabParameterState.NotApplicable,
             LabParameterState.NotApplicable, LabParameterState.Pending],
            parameters.Select(p => p.State));
    }

    [Fact]
    public void An_out_of_spec_parameter_carries_its_reading_and_limit()
    {
        var parameters = LabParameters.Assess("FM", LabResult.Failed,
        [
            Deviation("LOW_FAT", "FatPercent", 3.20m, 3.50m),
            Deviation("LOW_SNF", "snf", 8.10m, 8.50m),
        ]);

        var fat = parameters.Single(p => p.Parameter == LabParameters.Fat);
        Assert.Equal(LabParameterState.OutOfSpec, fat.State);
        Assert.Equal(3.20m, fat.ObservedValue);
        Assert.Equal(3.50m, fat.Limit);

        // Matched without regard to case.
        Assert.Equal(LabParameterState.OutOfSpec, parameters.Single(p => p.Parameter == LabParameters.Snf).State);
        Assert.Equal(LabParameterState.NoDeviation, parameters.Single(p => p.Parameter == LabParameters.Ph).State);
    }

    [Fact]
    public void A_reason_code_alone_marks_its_parameter()
    {
        var parameters = LabParameters.Assess("SY", LabResult.Failed, [Deviation("SENSORY_TEXTURE", null, null, null)]);

        var texture = parameters.Single(p => p.Parameter == LabParameters.Texture);
        Assert.Equal(LabParameterState.OutOfSpec, texture.State);
        Assert.Null(texture.ObservedValue);
    }

    [Fact]
    public void A_deviation_on_a_parameter_the_line_does_not_measure_stays_not_applicable()
    {
        var parameters = LabParameters.Assess("SY", LabResult.Failed, [Deviation("LOW_SNF", "SNF", 8.10m, 8.50m)]);

        Assert.Equal(LabParameterState.NotApplicable, parameters.Single(p => p.Parameter == LabParameters.Snf).State);
    }

    private static DeviationRecord Deviation(string? reasonCode, string? parameter, decimal? observed, decimal? limit) => new()
    {
        BatchId = "262-FM-A",
        Checkpoint = Checkpoint.Lab,
        ReasonCode = reasonCode,
        Parameter = parameter,
        ObservedValue = observed,
        Limit = limit,
    };
}
