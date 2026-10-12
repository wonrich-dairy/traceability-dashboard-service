namespace TraceabilityService.Domain.Entities;

/// <summary>
/// The six product line codes a batch code can carry.
/// </summary>
public static class ProductLines
{
    public static readonly IReadOnlyList<string> All = ["FM", "FLM", "SY", "SK", "DY", "CD"];

    // The code as stored, matched without regard to case; null if it is not one of the six.
    public static string? Normalize(string code) =>
        All.FirstOrDefault(p => string.Equals(p, code.Trim(), StringComparison.OrdinalIgnoreCase));
}
