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
