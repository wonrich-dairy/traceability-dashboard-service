using TraceabilityService.Api.Contracts;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// A <see cref="DashboardQuery"/> with its defaults filled in and checked. Pure, so the
/// rules are unit-tested without a database or a clock.
/// </summary>
public sealed record DashboardFilter(string? Facility, string? ProductLine, DateOnly From, DateOnly To)
{
    // The default window: today and the 29 days before it.
    public const int DefaultDays = 30;

    // Returns the filter, or the reasons it is invalid keyed by query parameter, for a
    // 400. An empty range, with nothing in it, is valid.
    public static (DashboardFilter? Filter, Dictionary<string, string> Errors) Resolve(DashboardQuery query, DateOnly today)
    {
        var errors = new Dictionary<string, string>();

        string? productLine = null;
        if (!string.IsNullOrWhiteSpace(query.ProductLine))
        {
            productLine = ProductLines.Normalize(query.ProductLine);
            if (productLine is null)
            {
                errors["productLine"] = $"'{query.ProductLine}' is not a product line. Use one of {string.Join(", ", ProductLines.All)}.";
            }
        }

        var to = query.To ?? today;
        var from = query.From ?? to.AddDays(-(DefaultDays - 1));

        if (from > to)
        {
            errors["from"] = $"from ({from:yyyy-MM-dd}) is after to ({to:yyyy-MM-dd}).";
        }
        else if (to > from.AddYears(1))
        {
            errors["to"] = $"The range {from:yyyy-MM-dd} to {to:yyyy-MM-dd} is longer than one year.";
        }

        var facility = string.IsNullOrWhiteSpace(query.Facility) ? null : query.Facility.Trim();

        return errors.Count > 0
            ? (null, errors)
            : (new DashboardFilter(facility, productLine, from, to), errors);
    }
}
