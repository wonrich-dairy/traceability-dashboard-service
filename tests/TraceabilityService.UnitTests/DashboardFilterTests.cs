using TraceabilityService.Api.Contracts;
using TraceabilityService.Application;

namespace TraceabilityService.UnitTests;

/// <summary>
/// The dashboard's query validation and defaults (SCRUM-135): 400 when from is after to,
/// when the range is longer than a year, or for an unknown product line; an empty range
/// is valid.
/// </summary>
public class DashboardFilterTests
{
    private static readonly DateOnly Today = new(2026, 10, 11);

    [Fact]
    public void With_no_parameters_the_range_is_the_30_days_ending_today_for_every_facility_and_product_line()
    {
        var (filter, errors) = DashboardFilter.Resolve(new DashboardQuery(), Today);

        Assert.Empty(errors);
        Assert.NotNull(filter);
        Assert.Equal(new DateOnly(2026, 9, 12), filter.From);
        Assert.Equal(Today, filter.To);
        Assert.Null(filter.Facility);
        Assert.Null(filter.ProductLine);
    }

    [Fact]
    public void A_to_date_alone_ends_a_30_day_range()
    {
        var (filter, _) = DashboardFilter.Resolve(new DashboardQuery { To = new DateOnly(2026, 9, 20) }, Today);

        Assert.NotNull(filter);
        Assert.Equal(new DateOnly(2026, 8, 22), filter.From);
    }

    [Theory]
    [InlineData("FM", "FM")]
    [InlineData("flm", "FLM")]
    [InlineData(" cd ", "CD")]
    public void Product_line_is_matched_without_regard_to_case(string given, string stored)
    {
        var (filter, errors) = DashboardFilter.Resolve(new DashboardQuery { ProductLine = given }, Today);

        Assert.Empty(errors);
        Assert.Equal(stored, filter?.ProductLine);
    }

    [Theory]
    [InlineData("XX")]
    [InlineData("MILK")]
    public void An_unknown_product_line_is_rejected(string productLine)
    {
        var (filter, errors) = DashboardFilter.Resolve(new DashboardQuery { ProductLine = productLine }, Today);

        Assert.Null(filter);
        Assert.Contains("productLine", errors.Keys);
    }

    [Fact]
    public void From_after_to_is_rejected()
    {
        var query = new DashboardQuery { From = new DateOnly(2026, 9, 20), To = new DateOnly(2026, 9, 19) };

        var (filter, errors) = DashboardFilter.Resolve(query, Today);

        Assert.Null(filter);
        Assert.Contains("from", errors.Keys);
    }

    [Fact]
    public void A_from_date_in_the_future_with_no_to_is_after_today_and_rejected()
    {
        var (filter, errors) = DashboardFilter.Resolve(new DashboardQuery { From = Today.AddDays(1) }, Today);

        Assert.Null(filter);
        Assert.Contains("from", errors.Keys);
    }

    [Fact]
    public void Exactly_one_year_is_allowed_and_a_day_more_is_rejected()
    {
        var oneYear = new DashboardQuery { From = new DateOnly(2025, 10, 11), To = new DateOnly(2026, 10, 11) };
        var longer = new DashboardQuery { From = new DateOnly(2025, 10, 10), To = new DateOnly(2026, 10, 11) };

        Assert.NotNull(DashboardFilter.Resolve(oneYear, Today).Filter);
        Assert.Contains("to", DashboardFilter.Resolve(longer, Today).Errors.Keys);
    }

    [Fact]
    public void A_single_day_range_is_valid()
    {
        var day = new DateOnly(2026, 9, 19);

        var (filter, errors) = DashboardFilter.Resolve(new DashboardQuery { From = day, To = day }, Today);

        Assert.Empty(errors);
        Assert.Equal(day, filter?.From);
    }

    [Fact]
    public void Facility_is_trimmed_and_blank_means_every_facility()
    {
        Assert.Equal("FACTORY-01", DashboardFilter.Resolve(new DashboardQuery { Facility = " FACTORY-01 " }, Today).Filter?.Facility);
        Assert.Null(DashboardFilter.Resolve(new DashboardQuery { Facility = "  " }, Today).Filter?.Facility);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var query = new DashboardQuery { ProductLine = "XX", From = new DateOnly(2026, 9, 20), To = new DateOnly(2026, 9, 1) };

        var (_, errors) = DashboardFilter.Resolve(query, Today);

        Assert.Equal(["from", "productLine"], errors.Keys.Order());
    }
}

public class FailRateTests
{
    [Theory]
    [InlineData(3, 1, 0.25)]
    [InlineData(0, 2, 1.0)]     // everything decided failed
    [InlineData(5, 0, 0.0)]     // decided, none failed: a real 0
    [InlineData(1, 2, 2.0 / 3)]
    public void Fail_rate_is_failed_over_cleared_plus_failed(int cleared, int failed, double expected)
    {
        Assert.Equal(expected, FailRate.Of(cleared, failed)!.Value, precision: 10);
    }

    [Fact]
    public void Nothing_decided_has_no_fail_rate_rather_than_zero()
    {
        Assert.Null(FailRate.Of(cleared: 0, failed: 0));
    }
}

public class FactoryCalendarTests
{
    [Theory]
    // Colombo is UTC+05:30: its day starts at 18:30 UTC the evening before.
    [InlineData("2026-10-10T18:29:59Z", "2026-10-10")]
    [InlineData("2026-10-10T18:30:00Z", "2026-10-11")]
    [InlineData("2026-10-11T12:00:00Z", "2026-10-11")]
    public void Today_is_the_date_in_Colombo_not_UTC(string utcNow, string expected)
    {
        var calendar = new FactoryCalendar(new FixedTime(DateTimeOffset.Parse(utcNow)));

        Assert.Equal(DateOnly.Parse(expected), calendar.Today);
    }

    [Fact]
    public void A_factory_day_starts_at_18_30_utc_the_day_before()
    {
        Assert.Equal(
            new DateTime(2026, 10, 10, 18, 30, 0, DateTimeKind.Utc),
            FactoryCalendar.StartOfDayUtc(new DateOnly(2026, 10, 11)));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
