using System.Text;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.UnitTests;

/// <summary>
/// The batch search export (SCRUM-139): every column, no empty cell, each missing value with
/// its own marker, safe against spreadsheet formulas, and a file name carrying the filters and
/// the export date.
/// </summary>
public class BatchSearchCsvTests
{
    private static readonly DateOnly ExportDate = new(2026, 10, 13);

    [Fact]
    public void A_set_yoghurt_with_nothing_resolved_yet_has_a_marker_in_every_cell()
    {
        var row = Row("262-SY-A", "SY", LabResult.Pending);
        row.DispatchRef = null;
        row.BatchReference = null;
        row.UpstreamStatus = ResolutionStatus.PendingRetry;
        row.LabParameters = LabParameters.Assess("SY", LabResult.Pending, []);

        var cells = BatchSearchCsv.Cells(row);

        Assert.DoesNotContain(cells, string.IsNullOrWhiteSpace);
        Assert.Equal(Headings().Length, cells.Count);
        Assert.Equal(
            ["Not received", "Not resolved", "PendingRetry", "Not resolved", "Not resolved", "None", "0", "None",
             "Pending", "Pending", "N/A", "N/A", "Pending"],
            cells.Skip(6).Where((_, i) => i is not 2 and not 3)); // first and last event times left out
    }

    [Fact]
    public void A_failed_fresh_milk_batch_shows_its_upstream_users_reasons_and_out_of_spec_readings()
    {
        var row = Row("262-FM-B", "FM", LabResult.Failed);
        row.UpstreamStatus = ResolutionStatus.Resolved;
        row.Tanks = ["T1", "T2"];
        row.Societies = [new SocietyLinkDto { Code = "VS001", Name = "Galle" }, new SocietyLinkDto { Code = "VS002" }];
        row.Users = ["analyst-1", "mcc.manager"];
        row.DeviationCount = 2;
        row.ReasonCodes = ["LOW_FAT", "SENSORY_TASTE"];
        row.LabParameters = LabParameters.Assess("FM", LabResult.Failed,
            [new DeviationRecord { BatchId = row.BatchId, ReasonCode = "LOW_FAT", Parameter = "FatPercent", ObservedValue = 3.2m, Limit = 3.5m }]);

        var cells = BatchSearchCsv.Cells(row);

        Assert.Equal(
            ["262-FM-B", "FACTORY-01", "FM", "Failed", "Failed", "Complete", "DN-1", "WR-1",
             "2026-09-19T05:40:00Z", "2026-09-19T11:40:00Z", "Resolved",
             "T1; T2", "VS001 (Galle); VS002", "analyst-1; mcc.manager", "2", "LOW_FAT; SENSORY_TASTE",
             "Out of spec: 3.20 against limit 3.50", "No deviation", "No deviation", "No deviation", "N/A"],
            cells);
    }

    [Fact]
    public void The_file_has_a_heading_row_then_one_crlf_terminated_row_per_batch_in_utf8_with_a_bom()
    {
        var content = BatchSearchCsv.Write([Row("A", "FM", LabResult.Pending), Row("B", "SY", LabResult.Pending)]);

        Assert.Equal(Encoding.UTF8.GetPreamble(), content[..3]);
        var lines = Encoding.UTF8.GetString(content[3..]).Split("\r\n");
        Assert.Equal(4, lines.Length); // heading, two rows, and the empty string after the last CRLF
        Assert.Equal(string.Join(",", Headings()), lines[0]);
        Assert.StartsWith("A,", lines[1]);
        Assert.StartsWith("B,", lines[2]);
        Assert.Equal("", lines[3]);
    }

    [Fact]
    public void No_rows_is_the_heading_row_alone()
    {
        var text = Encoding.UTF8.GetString(BatchSearchCsv.Write([])[3..]);

        Assert.Equal(string.Join(",", Headings()) + "\r\n", text);
    }

    [Fact]
    public void Commas_quotes_and_line_breaks_are_quoted_and_formulas_are_defused()
    {
        var row = Row("262-FM-C", "FM", LabResult.Pending);
        row.UpstreamStatus = ResolutionStatus.Resolved;
        row.Societies = [new SocietyLinkDto { Code = "VS009", Name = "Kandy, \"North\"" }];
        row.Users = ["=HYPERLINK(\"http://x\")"];

        var line = Encoding.UTF8.GetString(BatchSearchCsv.Write([row])[3..]).Split("\r\n")[1];

        Assert.Contains(",\"VS009 (Kandy, \"\"North\"\")\",", line);
        Assert.Contains(",\"'=HYPERLINK(\"\"http://x\"\")\",", line);
    }

    [Fact]
    public void The_file_name_carries_every_filter_and_the_export_date()
    {
        var name = BatchSearchCsv.FileName(new BatchSearchFilter(
            BatchStatus.Failed, "FM", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            "VS001", "T1", "qa.officer", "LOW_FAT", "FACTORY-01"), ExportDate);

        Assert.Equal(
            "batches_status-Failed_line-FM_from-2026-09-01_to-2026-09-30_society-VS001_tank-T1"
            + "_user-qa.officer_reason-LOW_FAT_facility-FACTORY-01_exported-2026-10-13.csv",
            name);
    }

    [Fact]
    public void With_no_filter_the_file_name_says_all()
    {
        Assert.Equal("batches_all_exported-2026-10-13.csv", BatchSearchCsv.FileName(new BatchSearchFilter(), ExportDate));
    }

    [Fact]
    public void Filter_values_are_made_safe_for_a_file_name()
    {
        var name = BatchSearchCsv.FileName(new BatchSearchFilter(Society: "Kandy North/../\"x\""), ExportDate);

        Assert.Equal("batches_society-Kandy-North-..-x_exported-2026-10-13.csv", name);
    }

    private static string[] Headings() => Encoding.UTF8.GetString(BatchSearchCsv.Write([])[3..]).TrimEnd('\r', '\n').Split(',');

    private static BatchSearchRowDto Row(string batchId, string productLine, LabResult labResult) => new()
    {
        BatchId = batchId,
        DispatchRef = "DN-1",
        BatchReference = "WR-1",
        ProductLine = productLine,
        Facility = "FACTORY-01",
        Status = labResult == LabResult.Failed ? BatchStatus.Failed : BatchStatus.AwaitingLab,
        Completeness = labResult == LabResult.Failed ? TraceCompleteness.Complete : TraceCompleteness.Partial,
        FirstEventAt = new DateTime(2026, 9, 19, 5, 40, 0, DateTimeKind.Utc),
        LastEventAt = new DateTime(2026, 9, 19, 11, 40, 0, DateTimeKind.Utc),
        LabResult = labResult,
        LabParameters = LabParameters.Assess(productLine, labResult, []),
    };
}
