using TraceabilityService.Api.Contracts;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// A <see cref="BatchSearchQuery"/> normalised and checked (SCRUM-139). Pure, so the rules are
/// unit-tested without a database. Null means the filter was not given.
/// </summary>
public sealed record BatchSearchFilter(
    BatchStatus? Status = null,
    string? ProductLine = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Society = null,
    string? Tank = null,
    string? User = null,
    string? ReasonCode = null,
    string? Facility = null)
{
    // The longest text filter accepted: no code, name or user ID stored here is longer.
    public const int MaxTextLength = 100;

    // Returns the filter, or the reasons it is invalid keyed by query parameter, for a 400.
    // A filter that matches nothing is valid.
    public static (BatchSearchFilter? Filter, Dictionary<string, string> Errors) Resolve(BatchSearchQuery query)
    {
        var errors = new Dictionary<string, string>();

        BatchStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            status = ParseStatus(query.Status);
            if (status is null)
            {
                errors["status"] = $"'{query.Status}' is not a batch status. Use one of {string.Join(", ", Enum.GetNames<BatchStatus>())}.";
            }
        }

        string? productLine = null;
        if (!string.IsNullOrWhiteSpace(query.ProductLine))
        {
            productLine = ProductLines.Normalize(query.ProductLine);
            if (productLine is null)
            {
                errors["productLine"] = $"'{query.ProductLine}' is not a product line. Use one of {string.Join(", ", ProductLines.All)}.";
            }
        }

        if (query.From is { } from && query.To is { } to && from > to)
        {
            errors["from"] = $"from ({from:yyyy-MM-dd}) is after to ({to:yyyy-MM-dd}).";
        }

        var filter = new BatchSearchFilter(
            status,
            productLine,
            query.From,
            query.To,
            Text(query.Society, "society", errors),
            Text(query.Tank, "tank", errors),
            Text(query.User, "user", errors),
            Text(query.ReasonCode, "reasonCode", errors),
            Text(query.Facility, "facility", errors));

        return errors.Count > 0 ? (null, errors) : (filter, errors);
    }

    // Names only: Enum.TryParse would also take "5" or "1,2".
    private static BatchStatus? ParseStatus(string value) =>
        Enum.GetValues<BatchStatus>()
            .Select(s => (BatchStatus?)s)
            .FirstOrDefault(s => string.Equals(s.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string? Text(string? value, string parameter, Dictionary<string, string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxTextLength)
        {
            errors[parameter] = $"{parameter} is longer than {MaxTextLength} characters.";
        }

        return trimmed;
    }
}
