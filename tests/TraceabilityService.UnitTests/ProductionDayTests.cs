using TraceabilityService.Application;

namespace TraceabilityService.UnitTests;

/// <summary>
/// FactoryCalendar.ProductionDay: the production day a batch code refers to, which keys the daily aggregates.
/// </summary>
public class ProductionDayTests
{
    [Theory]
    // The sample data's rule, MAKEDATE(2026, day), for 2026 batches.
    [InlineData("262-FM-B", "2026-09-19T03:00:00Z", "2026-09-19")]
    [InlineData("285-SY-A", "2026-10-12T03:00:00Z", "2026-10-12")]
    // Across New Year, in both directions.
    [InlineData("001-FM-A", "2026-12-31T16:00:00Z", "2027-01-01")]
    [InlineData("365-FM-A", "2027-01-01T02:00:00Z", "2026-12-31")]
    // Day 366 only exists in a leap year.
    [InlineData("366-FM-A", "2028-12-31T03:00:00Z", "2028-12-31")]
    public void The_production_day_is_the_codes_day_in_the_year_nearest_the_batch(
        string batchId, string firstEventAt, string expected)
    {
        var day = FactoryCalendar.ProductionDay(batchId, DateTime.Parse(firstEventAt).ToUniversalTime());

        Assert.Equal(DateOnly.Parse(expected), day);
    }

    [Fact]
    public void The_year_is_taken_in_factory_time_not_utc()
    {
        // 2026-12-31T19:00Z is already 1 January 00:30 in Colombo.
        var day = FactoryCalendar.ProductionDay("001-FM-A", new DateTime(2026, 12, 31, 19, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateOnly(2027, 1, 1), day);
    }

    [Theory]
    // The batch was created from an event at the first time; a late, earlier event then moves
    // FirstEventAt back to the second. The production day must not change.
    [InlineData("262-FM-B", "2026-09-19T03:00:00Z", "2026-09-16T03:00:00Z")]
    [InlineData("001-FM-A", "2027-01-01T03:00:00Z", "2026-12-30T03:00:00Z")]
    [InlineData("365-FM-A", "2026-12-31T03:00:00Z", "2027-01-02T03:00:00Z")]
    public void A_late_earlier_event_does_not_move_the_batch_to_another_day(string batchId, string createdFrom, string movedTo)
    {
        var atCreation = FactoryCalendar.ProductionDay(batchId, DateTime.Parse(createdFrom).ToUniversalTime());
        var afterLateEvent = FactoryCalendar.ProductionDay(batchId, DateTime.Parse(movedTo).ToUniversalTime());

        Assert.Equal(atCreation, afterLateEvent);
    }

    [Theory]
    [InlineData("FM-262-B")]
    [InlineData("0-FM-A")]
    [InlineData("400-FM-A")]
    [InlineData("262")]
    public void A_code_without_a_day_of_the_year_is_rejected(string batchId)
    {
        Assert.Throws<FormatException>(() =>
            FactoryCalendar.ProductionDay(batchId, new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc)));
    }
}
