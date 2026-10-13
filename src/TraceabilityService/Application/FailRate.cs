namespace TraceabilityService.Application;

/// <summary>
/// The dashboard's fail rate: failed / every batch of the day, as a fraction from 0 to 1.
/// Pending and on-hold batches are in the denominator, so a day the lab is still deciding
/// reads low and rises as its batches fail. It matches the "failed of batches" count the
/// dashboard shows beside it.
/// </summary>
public static class FailRate
{
    // Null when there are no batches: there is no rate, and 0 would read as "nothing failed".
    public static double? Of(int batches, int failed) =>
        batches == 0 ? null : (double)failed / batches;
}
