namespace TraceabilityService.Application;

/// <summary>
/// Dates as the factory sees them: "today" is the date in Asia/Colombo, not in UTC, so a
/// QCO's dashboard rolls over at local midnight.
/// </summary>
public sealed class FactoryCalendar(TimeProvider time)
{
    public static TimeZoneInfo Zone { get; } = LoadZone();

    public DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, Zone));

    // The UTC instant a factory day starts at, e.g. 2026-10-11 -> 2026-10-10T18:30Z.
    public static DateTime StartOfDayUtc(DateOnly day) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), Zone);

    /// <summary>
    /// The production day a batch code refers to: its leading day of the year, e.g. 262 in
    /// 262-FM-B is 2026-09-19. The code has no year, so it is the year that puts that day
    /// nearest the given instant (use the batch's FirstEventAt, so every event for a batch
    /// agrees). That is the sample data's MAKEDATE(2026, day) for 2026 batches, and still
    /// right across New Year: 001 near 31 December is the next 1 January.
    /// </summary>
    public static DateOnly ProductionDay(string batchId, DateTime nearUtc)
    {
        var dash = batchId.IndexOf('-');
        if (dash <= 0 || !int.TryParse(batchId.AsSpan(0, dash), out var dayOfYear) || dayOfYear is < 1 or > 366)
        {
            throw new FormatException($"Batch code '{batchId}' does not start with a day of the year.");
        }

        var near = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nearUtc, Zone));

        // Day 366 only exists in leap years.
        return new[] { near.Year - 1, near.Year, near.Year + 1 }
            .Where(year => dayOfYear <= (DateTime.IsLeapYear(year) ? 366 : 365))
            .Select(year => new DateOnly(year, 1, 1).AddDays(dayOfYear - 1))
            .MinBy(day => Math.Abs(day.DayNumber - near.DayNumber));
    }

    // Sri Lanka has no daylight saving, so a fixed +05:30 is exact if the host has no
    // time zone data (a slimmed-down container, for example).
    private static TimeZoneInfo LoadZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Colombo");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("Asia/Colombo", TimeSpan.FromMinutes(330), "Asia/Colombo", "Asia/Colombo");
        }
    }
}
