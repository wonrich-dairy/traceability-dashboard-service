using TraceabilityService.Api.Contracts;

namespace TraceabilityService.Application;

/// <summary>
/// An <see cref="AuditTrailQuery"/> trimmed and checked (SCRUM-138). Pure, so the rules, and what
/// each filter keeps, are unit-tested without a database. Null means the filter was not given.
/// </summary>
public sealed record AuditTrailFilter(
    string? User = null,
    string? Action = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? BatchId = null)
{
    // The longest text filter accepted: no user ID, action or batch ID stored here is longer.
    public const int MaxTextLength = 100;

    // Returns the filter, or the reasons it is invalid keyed by query parameter, for a 400.
    // A filter that matches nothing is valid.
    public static (AuditTrailFilter? Filter, Dictionary<string, string> Errors) Resolve(AuditTrailQuery query)
    {
        var errors = new Dictionary<string, string>();

        if (query.From is { } from && query.To is { } to && from > to)
        {
            errors["from"] = $"from ({from:yyyy-MM-dd}) is after to ({to:yyyy-MM-dd}).";
        }

        var filter = new AuditTrailFilter(
            Text(query.User, "user", errors),
            Text(query.Action, "action", errors),
            query.From,
            query.To,
            Text(query.BatchId, "batchId", errors));

        return errors.Count > 0 ? (null, errors) : (filter, errors);
    }

    // The entries this filter keeps. The range is factory days, as on the dashboard and the batch
    // search: an entry is in it when it happened on one of them in Colombo. Text is compared as
    // the source compares it; in MySQL the columns are case insensitive.
    public IQueryable<AuditTrailRow> Apply(IQueryable<AuditTrailRow> rows)
    {
        if (User is { } user)
        {
            rows = rows.Where(r => r.UserId == user);
        }

        if (Action is { } action)
        {
            rows = rows.Where(r => r.Action == action);
        }

        if (From is { } from)
        {
            var fromUtc = FactoryCalendar.StartOfDayUtc(from);
            rows = rows.Where(r => r.OccurredAt >= fromUtc);
        }

        if (To is { } to)
        {
            var toUtc = FactoryCalendar.StartOfDayUtc(to.AddDays(1));
            rows = rows.Where(r => r.OccurredAt < toUtc);
        }

        if (BatchId is { } batchId)
        {
            rows = rows.Where(r => r.BatchId == batchId);
        }

        return rows;
    }

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

/// <summary>
/// One row of the consolidated audit trail as the query reads it, before it becomes an
/// <see cref="AuditTrailEntryDto"/>. Strings rather than enums, because the rows come from raw SQL.
/// </summary>
public sealed class AuditTrailRow
{
    public required string Source{get; set;}
    public required string EntryId{get; set;}
    public DateTime OccurredAt{get; set;}
    public string? UserId{get; set;}
    public required string Action{get; set;}
    public string? Checkpoint{get; set;}
    public string? BatchId{get; set;}
    public string? Facility{get; set;}
    public string? Reference{get; set;}
    public string? Details{get; set;}
}
