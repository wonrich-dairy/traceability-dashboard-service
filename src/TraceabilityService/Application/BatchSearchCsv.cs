using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TraceabilityService.Api.Contracts;

namespace TraceabilityService.Application;

/// <summary>
/// The batch search export (SCRUM-139): one row per batch, every column, and no empty cell. A
/// blank cell in an audit export cannot be told apart from a test somebody forgot to run, so
/// each kind of missing value has its own marker. Pure, so it is unit-tested without a database.
/// </summary>
public static partial class BatchSearchCsv
{
    public const string ContentType = "text/csv";

    // A column the batch's product line does not measure.
    public const string NotApplicable = "N/A";

    // The lab has not made a determination yet.
    public const string Pending = "Pending";

    // The current determination records nothing against the parameter.
    public const string NoDeviation = "No deviation";

    // No event has carried the value yet.
    public const string NotReceived = "Not received";

    // The upstream snapshot has not resolved, so the value is not known yet.
    public const string NotResolved = "Not resolved";

    // Known, and there are none.
    public const string None = "None";

    private const string Separator = "; ";

    private static readonly string[] Headings =
    [
        "Batch ID", "Facility", "Product line", "Status", "Lab result", "Trace completeness",
        "Dispatch ref", "Batch reference", "First event (UTC)", "Last event (UTC)", "Upstream snapshot",
        "Tanks", "Societies", "Users", "Deviation count", "Reason codes",
        .. LabParameters.Columns.Select(c => c.Heading),
    ];

    /// <summary>
    /// The heading row, then one row per batch, as UTF-8 with a byte order mark so Excel reads
    /// society names in Sinhala or Tamil correctly. Lines end in CRLF (RFC 4180).
    /// </summary>
    public static byte[] Write(IEnumerable<BatchSearchRowDto> rows)
    {
        var csv = new StringBuilder();
        AppendLine(csv, Headings);
        foreach (var row in rows)
        {
            AppendLine(csv, Cells(row));
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    public static IReadOnlyList<string> Cells(BatchSearchRowDto row)
    {
        var resolved = row.UpstreamStatus == Domain.Entities.ResolutionStatus.Resolved;

        return
        [
            row.BatchId,
            row.Facility,
            row.ProductLine,
            row.Status.ToString(),
            row.LabResult.ToString(),
            row.Completeness.ToString(),
            row.DispatchRef ?? NotReceived,
            row.BatchReference ?? NotResolved,
            Timestamp(row.FirstEventAt),
            Timestamp(row.LastEventAt),
            row.UpstreamStatus?.ToString() ?? NotReceived,
            resolved ? List(row.Tanks) : NotResolved,
            resolved ? List(row.Societies.Select(s => s.Name is null ? s.Code : $"{s.Code} ({s.Name})")) : NotResolved,
            List(row.Users),
            row.DeviationCount.ToString(CultureInfo.InvariantCulture),
            List(row.ReasonCodes),
            .. row.LabParameters.Select(Lab),
        ];
    }

    /// <summary>
    /// batches_[filters]_exported-[date].csv, e.g.
    /// batches_status-Failed_line-FM_from-2026-09-01_to-2026-09-30_exported-2026-10-13.csv, or
    /// batches_all_exported-… with no filter. Filter values are reduced to letters, digits, dot,
    /// dash and underscore, so the name is safe in a header and on any file system.
    /// </summary>
    public static string FileName(BatchSearchFilter filter, DateOnly exportDate)
    {
        var parts = new List<string>();
        Add(parts, "status", filter.Status?.ToString());
        Add(parts, "line", filter.ProductLine);
        Add(parts, "from", filter.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(parts, "to", filter.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(parts, "society", filter.Society);
        Add(parts, "tank", filter.Tank);
        Add(parts, "user", filter.User);
        Add(parts, "reason", filter.ReasonCode);
        Add(parts, "facility", filter.Facility);

        var filters = parts.Count == 0 ? "all" : string.Join("_", parts);
        return $"batches_{filters}_exported-{exportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv";
    }

    private static void Add(List<string> parts, string name, string? value)
    {
        if (value is null)
        {
            return;
        }

        var safe = UnsafeFileNameChars().Replace(value, "-").Trim('-');
        parts.Add($"{name}-{(safe.Length == 0 ? "x" : safe[..Math.Min(safe.Length, 40)])}");
    }

    private static string Lab(LabParameterDto parameter) => parameter.State switch
    {
        LabParameterState.NotApplicable => NotApplicable,
        LabParameterState.Pending => Pending,
        LabParameterState.NoDeviation => NoDeviation,
        _ => (parameter.ObservedValue, parameter.Limit) switch
        {
            ({ } observed, { } limit) => $"Out of spec: {Number(observed)} against limit {Number(limit)}",
            ({ } observed, null) => $"Out of spec: {Number(observed)}",
            _ => "Out of spec",
        },
    };

    private static string List(IEnumerable<string> values)
    {
        var joined = string.Join(Separator, values);
        return joined.Length == 0 ? None : joined;
    }

    private static string Number(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static void AppendLine(StringBuilder csv, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                csv.Append(',');
            }

            csv.Append(Escape(cells[i]));
        }

        csv.Append("\r\n");
    }

    // A cell a spreadsheet would run as a formula is prefixed with ' (CSV injection), then
    // quoted when it holds a comma, quote or line break.
    private static string Escape(string cell)
    {
        if (cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            cell = "'" + cell;
        }

        return cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 || cell != cell.Trim()
            ? $"\"{cell.Replace("\"", "\"\"")}\""
            : cell;
    }

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeFileNameChars();
}
