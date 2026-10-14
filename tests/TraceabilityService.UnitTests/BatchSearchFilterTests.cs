using TraceabilityService.Api.Contracts;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.UnitTests;

/// <summary>
/// The batch search's query validation (SCRUM-139): every filter optional, status and product
/// line matched without regard to case, 400 for an unknown one, for from after to, or for a
/// text filter too long to be a real value. A filter that can match nothing is still valid.
/// </summary>
public class BatchSearchFilterTests
{
    [Fact]
    public void With_no_parameters_nothing_is_filtered()
    {
        var (filter, errors) = BatchSearchFilter.Resolve(new BatchSearchQuery());

        Assert.Empty(errors);
        Assert.Equal(new BatchSearchFilter(), filter);
    }

    [Theory]
    [InlineData("Failed", BatchStatus.Failed)]
    [InlineData("onhold", BatchStatus.OnHold)]
    [InlineData(" AWAITINGLAB ", BatchStatus.AwaitingLab)]
    public void Status_is_matched_by_name_without_regard_to_case(string given, BatchStatus expected)
    {
        var (filter, errors) = BatchSearchFilter.Resolve(new BatchSearchQuery { Status = given });

        Assert.Empty(errors);
        Assert.Equal(expected, filter?.Status);
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("5")]
    [InlineData("Failed,Cleared")]
    public void An_unknown_status_or_a_number_is_rejected(string status)
    {
        var (filter, errors) = BatchSearchFilter.Resolve(new BatchSearchQuery { Status = status });

        Assert.Null(filter);
        Assert.Contains("status", errors.Keys);
    }

    [Theory]
    [InlineData("flm", "FLM")]
    [InlineData(" sy ", "SY")]
    public void Product_line_is_normalised(string given, string stored)
    {
        var (filter, _) = BatchSearchFilter.Resolve(new BatchSearchQuery { ProductLine = given });

        Assert.Equal(stored, filter?.ProductLine);
    }

    [Fact]
    public void An_unknown_product_line_is_rejected()
    {
        var (filter, errors) = BatchSearchFilter.Resolve(new BatchSearchQuery { ProductLine = "MILK" });

        Assert.Null(filter);
        Assert.Contains("productLine", errors.Keys);
    }

    [Fact]
    public void From_after_to_is_rejected_but_either_alone_is_fine()
    {
        var (backwards, errors) = BatchSearchFilter.Resolve(
            new BatchSearchQuery { From = new DateOnly(2026, 10, 2), To = new DateOnly(2026, 10, 1) });
        var (fromOnly, _) = BatchSearchFilter.Resolve(new BatchSearchQuery { From = new DateOnly(2026, 10, 2) });
        var (sameDay, _) = BatchSearchFilter.Resolve(
            new BatchSearchQuery { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 1) });

        Assert.Null(backwards);
        Assert.Contains("from", errors.Keys);
        Assert.Equal(new DateOnly(2026, 10, 2), fromOnly?.From);
        Assert.Null(fromOnly?.To);
        Assert.NotNull(sameDay);
    }

    [Fact]
    public void Text_filters_are_trimmed_and_blank_ones_ignored()
    {
        var (filter, errors) = BatchSearchFilter.Resolve(new BatchSearchQuery
        {
            Society = " VS001 ",
            Tank = "T1",
            User = "  ",
            ReasonCode = "LOW_FAT",
            Facility = "",
        });

        Assert.Empty(errors);
        Assert.Equal(new BatchSearchFilter(Society: "VS001", Tank: "T1", ReasonCode: "LOW_FAT"), filter);
    }

    [Theory]
    [InlineData("society")]
    [InlineData("tank")]
    [InlineData("user")]
    [InlineData("reasonCode")]
    [InlineData("facility")]
    public void A_text_filter_longer_than_any_stored_value_is_rejected(string parameter)
    {
        var tooLong = new string('x', BatchSearchFilter.MaxTextLength + 1);
        var query = parameter switch
        {
            "society" => new BatchSearchQuery { Society = tooLong },
            "tank" => new BatchSearchQuery { Tank = tooLong },
            "user" => new BatchSearchQuery { User = tooLong },
            "reasonCode" => new BatchSearchQuery { ReasonCode = tooLong },
            _ => new BatchSearchQuery { Facility = tooLong },
        };

        var (filter, errors) = BatchSearchFilter.Resolve(query);

        Assert.Null(filter);
        Assert.Contains(parameter, errors.Keys);
    }

    [Fact]
    public void Every_invalid_parameter_is_reported_at_once()
    {
        var (_, errors) = BatchSearchFilter.Resolve(new BatchSearchQuery
        {
            Status = "Nope",
            ProductLine = "XX",
            From = new DateOnly(2026, 2, 1),
            To = new DateOnly(2026, 1, 1),
        });

        Assert.Equal(["from", "productLine", "status"], errors.Keys.Order());
    }
}
