namespace TraceabilityService.Application;

/// <summary>
/// The dashboard's fail rate: failed / (cleared + failed), as a fraction from 0 to 1.
/// Pending and on-hold batches are not decided, so they are in neither term.
/// </summary>
public static class FailRate
{
    // Null when nothing was decided: there is no rate, and 0 would read as "nothing failed".
    public static double? Of(int cleared, int failed) =>
        cleared + failed == 0 ? null : (double)failed / (cleared + failed);
}
