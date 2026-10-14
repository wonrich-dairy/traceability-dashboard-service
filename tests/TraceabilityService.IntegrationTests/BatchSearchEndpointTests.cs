using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using SRC.Authorization;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// GET /api/batches/search and GET /api/batches/export (SCRUM-139). The export date is pinned
/// to 2026-10-13 in Colombo. Four batches at facility TEST-SEARCH, newest last:
///   1  FM Failed     2026-09-19  T1 (VS001 Galle, VS002) and T2 (VS001 again, VS003); two Processing
///                                events by proc.tech-1; failed by analyst-1 for LOW_FAT and LOW_PH
///   2  FM Cleared    2026-09-20  T1 (VS001 Galle); cleared by analyst-2
///   3  SY OnHold     2026-09-25  snapshot still being looked up; a Processing event by proc.tech-1
///   4  SY AwaitingLab 2026-10-01 no snapshot yet
/// and one more, 5, at TEST-SEARCH-OTHER. Every query names a facility, so other test classes'
/// batches never show up.
/// </summary>
[Collection(ApiCollection.Name)]
public class BatchSearchEndpointTests(TraceabilityApiFactory factory, ITestOutputHelper output) : IAsyncLifetime
{
    private const string Facility = "TEST-SEARCH";
    private const string OtherFacility = "TEST-SEARCH-OTHER";
    private const string PagingFacility = "TEST-SEARCH-PAGE";
    private const string PerfFacility = "TEST-SEARCH-PERF";

    private static readonly DateTime Day19 = new(2026, 9, 19, 5, 0, 0, DateTimeKind.Utc);

    private WebApplicationFactory<Program>? _app;

    // The shared API, with the factory calendar pinned to 06:00 UTC on 2026-10-13.
    private WebApplicationFactory<Program> App => _app ??= factory.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
            services.AddSingleton(new FactoryCalendar(new FixedTime(new DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero))))));

    public async Task InitializeAsync()
    {
        await RemoveSeededDataAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

        db.BatchRecords.AddRange(
            Batch("TEST-SEARCH-1", Facility, "FM", BatchStatus.Failed, Day19),
            Batch("TEST-SEARCH-2", Facility, "FM", BatchStatus.Cleared, Day19.AddDays(1)),
            Batch("TEST-SEARCH-3", Facility, "SY", BatchStatus.OnHold, Day19.AddDays(6)),
            Batch("TEST-SEARCH-4", Facility, "SY", BatchStatus.AwaitingLab, Day19.AddDays(12)),
            Batch("TEST-SEARCH-5", OtherFacility, "FM", BatchStatus.Failed, Day19));

        db.UpstreamSnapshots.AddRange(
            Resolved("TEST-SEARCH-1",
                Tank("T1", Consignment("VS001", "Galle", "mcc.officer-1"), Consignment("VS002", null, "mcc.officer-2")),
                Tank("T2", Consignment("VS001", "Galle", "mcc.officer-2"), Consignment("VS003", null, "mcc.officer-2"))),
            Resolved("TEST-SEARCH-2", Tank("T1", Consignment("VS001", "Galle", "mcc.officer-2"))),
            new UpstreamSnapshot { BatchId = "TEST-SEARCH-3", ResolutionStatus = ResolutionStatus.PendingRetry },
            Resolved("TEST-SEARCH-5", Tank("T9", Consignment("VS009", null, "mcc.officer-9"))));

        var failedLab = Event("TEST-SEARCH-1", Checkpoint.Lab, "BatchFailed", Day19.AddHours(8), "analyst-1");
        var clearedLab = Event("TEST-SEARCH-2", Checkpoint.Lab, "BatchCleared", Day19.AddDays(1).AddHours(8), "analyst-2");
        db.TimelineEvents.AddRange(
            Event("TEST-SEARCH-1", Checkpoint.Processing, "ProcessingStageRecorded", Day19.AddHours(2), "proc.tech-1"),
            Event("TEST-SEARCH-1", Checkpoint.Processing, "ProcessingCompleted", Day19.AddHours(4), "proc.tech-1"),
            failedLab,
            clearedLab,
            Event("TEST-SEARCH-3", Checkpoint.Processing, "ProcessingHoldRaised", Day19.AddDays(6).AddHours(2), "proc.tech-1"));
        await db.SaveChangesAsync();

        db.LabDeterminations.AddRange(
            Determination("TEST-SEARCH-1", DeterminationResult.Failed, failedLab),
            Determination("TEST-SEARCH-2", DeterminationResult.Cleared, clearedLab));
        db.DeviationRecords.AddRange(
            LabDeviation(failedLab, "LOW_FAT", "FatPercent", 3.20m, 3.50m),
            LabDeviation(failedLab, "LOW_PH", "Ph", 6.40m, 6.50m));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await RemoveSeededDataAsync();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Search_lists_the_matching_batches_newest_first_with_the_total()
    {
        var json = await SearchAsync($"facility={Facility}");

        Assert.Equal(["TEST-SEARCH-4", "TEST-SEARCH-3", "TEST-SEARCH-2", "TEST-SEARCH-1"], Ids(json));
        Assert.Equal(4, json.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, json.GetProperty("totalPages").GetInt32());
        Assert.Equal(1, json.GetProperty("page").GetInt32());
        Assert.Equal(50, json.GetProperty("pageSize").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("message").ValueKind);
        Assert.Equal(Facility, json.GetProperty("filters").GetProperty("facility").GetString());
    }

    // Each filter on its own, then some together, against the four batches.
    [Theory]
    [InlineData("status=Failed", "1")]
    [InlineData("status=onhold", "3")]
    [InlineData("productLine=sy", "4,3")]
    [InlineData("from=2026-09-20&to=2026-09-25", "3,2")]
    [InlineData("from=2026-09-25", "4,3")]
    [InlineData("to=2026-09-19", "1")]
    [InlineData("society=VS001", "2,1")]
    [InlineData("society=vs003", "1")]
    [InlineData("society=galle", "2,1")]
    [InlineData("tank=T2", "1")]
    [InlineData("tank=t1", "2,1")]
    [InlineData("user=proc.tech-1", "3,1")]      // Processing events
    [InlineData("user=analyst-2", "2")]          // the lab's determination
    [InlineData("user=mcc.manager-1", "2,1")]    // the snapshot's dispatch
    [InlineData("user=intake.officer-1", "2,1")] // the snapshot's screening
    [InlineData("user=MCC.OFFICER-1", "1")]      // a consignment in the snapshot
    [InlineData("reasonCode=LOW_PH", "1")]
    [InlineData("status=Failed&society=VS001", "1")]
    [InlineData("productLine=FM&tank=T1&user=analyst-2", "2")]
    [InlineData("productLine=SY&user=proc.tech-1&from=2026-09-01&to=2026-09-30", "3")]
    public async Task Each_filter_narrows_the_search_and_filters_combine(string query, string expected)
    {
        var json = await SearchAsync($"facility={Facility}&{query}");

        Assert.Equal(expected.Split(',').Select(n => $"TEST-SEARCH-{n}"), Ids(json));
        Assert.Equal(expected.Split(',').Length, json.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Facility_filters_too()
    {
        Assert.Equal(["TEST-SEARCH-5"], Ids(await SearchAsync($"facility={OtherFacility}")));
    }

    [Fact]
    public async Task A_batch_matched_several_times_over_appears_once()
    {
        // Batch 1 has VS001 in two consignments, two reasons, and two events by proc.tech-1.
        var json = await SearchAsync($"facility={Facility}&society=VS001&user=proc.tech-1&status=Failed");

        Assert.Equal(["TEST-SEARCH-1"], Ids(json));
        Assert.Equal(1, json.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_row_carries_its_upstream_users_reasons_and_lab_parameters()
    {
        var json = await SearchAsync($"facility={Facility}&status=Failed");
        var row = json.GetProperty("items")[0];

        Assert.Equal("Failed", row.GetProperty("labResult").GetString());
        Assert.Equal("Resolved", row.GetProperty("upstreamStatus").GetString());
        Assert.Equal(["T1", "T2"], Strings(row.GetProperty("tanks")));
        Assert.Equal(["VS001", "VS002", "VS003"], row.GetProperty("societies").EnumerateArray().Select(s => s.GetProperty("code").GetString()));
        Assert.Equal(
            ["analyst-1", "intake.officer-1", "mcc.manager-1", "mcc.officer-1", "mcc.officer-2", "proc.tech-1"],
            Strings(row.GetProperty("users")));
        Assert.Equal(2, row.GetProperty("deviationCount").GetInt32());
        Assert.Equal(["LOW_FAT", "LOW_PH"], Strings(row.GetProperty("reasonCodes")));

        var lab = row.GetProperty("labParameters").EnumerateArray().ToList();
        Assert.Equal(
            ["FatPercent:OutOfSpec", "Ph:OutOfSpec", "SNF:NoDeviation", "CorrectedCLR:NoDeviation", "Texture:NotApplicable"],
            lab.Select(p => $"{p.GetProperty("parameter").GetString()}:{p.GetProperty("state").GetString()}"));
        Assert.Equal(3.20m, lab[0].GetProperty("observedValue").GetDecimal());
        Assert.Equal(3.50m, lab[0].GetProperty("limit").GetDecimal());
    }

    [Fact]
    public async Task A_fermented_batch_awaiting_the_lab_reads_pending_and_not_applicable_never_blank()
    {
        var json = await SearchAsync($"facility={Facility}&status=AwaitingLab");
        var row = json.GetProperty("items")[0];

        Assert.Equal(JsonValueKind.Null, row.GetProperty("upstreamStatus").ValueKind);
        Assert.Empty(row.GetProperty("tanks").EnumerateArray());
        Assert.Equal(
            ["Pending", "Pending", "NotApplicable", "NotApplicable", "Pending"],
            row.GetProperty("labParameters").EnumerateArray().Select(p => p.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task Results_are_paged_at_50_newest_first_with_the_total_count()
    {
        await SeedManyAsync(PagingFacility, "TEST-SEARCH-P", 55);

        var first = await SearchAsync($"facility={PagingFacility}");
        var second = await SearchAsync($"facility={PagingFacility}&page=2");
        var beyond = await SearchAsync($"facility={PagingFacility}&page=3");

        Assert.Equal(50, first.GetProperty("items").GetArrayLength());
        Assert.Equal(5, second.GetProperty("items").GetArrayLength());
        Assert.Empty(beyond.GetProperty("items").EnumerateArray());
        Assert.All([first, second, beyond], page =>
        {
            Assert.Equal(55, page.GetProperty("totalCount").GetInt32());
            Assert.Equal(2, page.GetProperty("totalPages").GetInt32());
        });

        // Newest first across both pages, none twice: P0054 was created last.
        var all = Ids(first).Concat(Ids(second)).ToList();
        Assert.Equal(Enumerable.Range(0, 55).Reverse().Select(n => $"TEST-SEARCH-P{n:0000}"), all);
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_is_an_empty_page_with_a_message_not_an_error()
    {
        var json = await SearchAsync($"facility={Facility}&status=Failed&productLine=SY");

        Assert.Empty(json.GetProperty("items").EnumerateArray());
        Assert.Equal(0, json.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, json.GetProperty("totalPages").GetInt32());
        Assert.Equal(BatchSearchService.NoMatchMessage, json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Export_is_every_matching_row_and_column_with_markers_for_not_applicable_and_unknown()
    {
        var response = await Client(WonrichRoles.QualityControlOfficer).GetAsync($"/api/batches/export?facility={Facility}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("4", response.Headers.GetValues("X-Batch-Count").Single());

        var rows = Csv(await response.Content.ReadAsByteArrayAsync());
        var heading = rows[0];
        Assert.Equal(["Batch ID", "Facility", "Product line"], heading.Take(3));
        Assert.Equal(["Fat %", "pH", "SNF %", "Corrected CLR", "Texture"], heading.TakeLast(5));

        var batches = rows.Skip(1).ToList();
        Assert.Equal(["TEST-SEARCH-4", "TEST-SEARCH-3", "TEST-SEARCH-2", "TEST-SEARCH-1"], batches.Select(r => r[0]));
        Assert.All(batches, r => Assert.Equal(heading.Count, r.Count));
        Assert.All(batches, r => Assert.DoesNotContain(r, string.IsNullOrWhiteSpace));

        var cell = (string batchId, string column) => batches.Single(r => r[0] == batchId)[heading.IndexOf(column)];
        Assert.Equal("N/A", cell("TEST-SEARCH-3", "SNF %"));
        Assert.Equal("Pending", cell("TEST-SEARCH-3", "pH"));
        Assert.Equal("Not resolved", cell("TEST-SEARCH-3", "Tanks"));
        Assert.Equal("Not received", cell("TEST-SEARCH-4", "Upstream snapshot"));
        Assert.Equal("N/A", cell("TEST-SEARCH-1", "Texture"));
        Assert.Equal("Out of spec: 3.20 against limit 3.50", cell("TEST-SEARCH-1", "Fat %"));
        Assert.Equal("No deviation", cell("TEST-SEARCH-2", "Fat %"));
        Assert.Equal("VS001 (Galle); VS002; VS003", cell("TEST-SEARCH-1", "Societies"));
        Assert.Equal("LOW_FAT; LOW_PH", cell("TEST-SEARCH-1", "Reason codes"));
        Assert.Equal("None", cell("TEST-SEARCH-2", "Reason codes"));
    }

    [Fact]
    public async Task Export_is_not_limited_to_one_page()
    {
        await SeedManyAsync(PagingFacility, "TEST-SEARCH-P", 55);

        var response = await Client(WonrichRoles.QualityControlOfficer).GetAsync($"/api/batches/export?facility={PagingFacility}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(56, Csv(await response.Content.ReadAsByteArrayAsync()).Count); // heading and 55 batches
    }

    [Fact]
    public async Task The_export_file_name_carries_the_filters_and_the_export_date()
    {
        var response = await Client(WonrichRoles.QualityControlOfficer)
            .GetAsync($"/api/batches/export?facility={Facility}&status=failed&productLine=fm&from=2026-09-01&to=2026-09-30");

        Assert.Equal(
            "batches_status-Failed_line-FM_from-2026-09-01_to-2026-09-30_facility-TEST-SEARCH_exported-2026-10-13.csv",
            response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact]
    public async Task An_export_is_audited_with_who_asked_the_filters_and_the_row_count()
    {
        var client = Client(WonrichRoles.QualityControlOfficer);
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "test-search-export-1");

        var response = await client.GetAsync($"/api/batches/export?facility={Facility}&society=VS001");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        var entry = await db.AuditEntries.SingleAsync(a => a.CorrelationId == "test-search-export-1");

        Assert.Equal(TestTokens.UserId, entry.Actor);
        Assert.Equal(BatchSearchService.ExportAction, entry.Action);
        Assert.Equal(BatchSearchService.ExportEntityType, entry.EntityType);
        Assert.Equal("batches_society-VS001_facility-TEST-SEARCH_exported-2026-10-13.csv", entry.EntityId);

        using var details = JsonDocument.Parse(entry.Details!);
        Assert.Equal(2, details.RootElement.GetProperty("rowCount").GetInt32());
        Assert.Equal(WonrichRoles.QualityControlOfficer, details.RootElement.GetProperty("role").GetString());
        Assert.Equal("VS001", details.RootElement.GetProperty("filters").GetProperty("society").GetString());
    }

    [Fact]
    public async Task Exporting_a_filter_that_matches_nothing_is_the_heading_row_and_a_zero_count_not_an_error()
    {
        var response = await Client(WonrichRoles.QualityControlOfficer)
            .GetAsync($"/api/batches/export?facility={Facility}&tank=NO-SUCH-TANK");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("X-Batch-Count").Single());
        Assert.Single(Csv(await response.Content.ReadAsByteArrayAsync()));
    }

    [Theory]
    [InlineData("search?status=Rejected", "status")]
    [InlineData("search?productLine=XX", "productLine")]
    [InlineData("search?from=2026-10-02&to=2026-10-01", "from")]
    [InlineData("search?page=0", "page")]
    [InlineData("export?status=Rejected", "status")]
    [InlineData("export?productLine=XX", "productLine")]
    public async Task Invalid_parameters_are_a_400_problem_naming_the_parameter(string pathAndQuery, string parameter)
    {
        var response = await Client(WonrichRoles.QualityControlOfficer).GetAsync($"/api/batches/{pathAndQuery}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(parameter, out _), json.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("search")]
    [InlineData("export")]
    public async Task Only_a_qco_or_an_admin_may_search_or_export(string endpoint)
    {
        var path = $"/api/batches/{endpoint}?facility={Facility}";

        Assert.Equal(HttpStatusCode.OK, (await Client(WonrichRoles.QualityControlOfficer).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(WonrichRoles.SystemAdministrator).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(WonrichRoles.QualityAnalyst).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(WonrichRoles.ProductionManager).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await App.CreateClient().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task A_refused_export_is_not_audited()
    {
        var client = Client(WonrichRoles.QualityAnalyst);
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "test-search-export-refused");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/batches/export?facility={Facility}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        Assert.False(await db.AuditEntries.AnyAsync(a => a.CorrelationId == "test-search-export-refused"));
    }

    // The definition of done: p95 under 2 seconds against at least 1,000 batches. Every query
    // here is timed after a warm-up; the JMeter plan measures the same queries under load.
    [Fact]
    public async Task Search_and_export_answer_within_two_seconds_over_a_thousand_batches()
    {
        await SeedManyAsync(PerfFacility, "TEST-SEARCH-L", 1000, withSnapshots: true);
        var client = Client(WonrichRoles.QualityControlOfficer);

        string[] paths =
        [
            $"/api/batches/search?facility={PerfFacility}",
            $"/api/batches/search?facility={PerfFacility}&page=20",
            $"/api/batches/search?facility={PerfFacility}&society=VS007&productLine=FM",
            $"/api/batches/search?facility={PerfFacility}&tank=T2&from=2026-08-01&to=2026-08-31",
            $"/api/batches/search?facility={PerfFacility}&user=mcc.officer-3&status=Cleared",
            $"/api/batches/search?society=VS007",
            $"/api/batches/export?facility={PerfFacility}",
        ];

        foreach (var path in paths)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode); // warm-up

            var timer = Stopwatch.StartNew();
            var response = await client.GetAsync(path);
            await response.Content.ReadAsByteArrayAsync();
            timer.Stop();

            output.WriteLine($"{timer.ElapsedMilliseconds,5} ms  {path}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"{path} took {timer.ElapsedMilliseconds} ms");
        }
    }

    private async Task<JsonElement> SearchAsync(string query)
    {
        var response = await Client(WonrichRoles.QualityControlOfficer).GetAsync($"/api/batches/search?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private HttpClient Client(string role)
    {
        var client = App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(role: role));
        return client;
    }

    private static List<string?> Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("batchId").GetString())];

    private static List<string?> Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString())];

    // Batches 0 to count - 1, an hour apart in August 2026, the six product lines in turn. With
    // snapshots, each has two tanks and three consignments from 50 societies, as perf/seed-batches.sql.
    private async Task SeedManyAsync(string facility, string prefix, int count, bool withSnapshots = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var n = 0; n < count; n++)
        {
            var batchId = $"{prefix}{n:0000}";
            db.BatchRecords.Add(Batch(batchId, facility, ProductLines.All[n % 6],
                n % 10 == 0 ? BatchStatus.Failed : BatchStatus.Cleared, start.AddHours(n)));

            if (withSnapshots)
            {
                db.UpstreamSnapshots.Add(Resolved(batchId,
                    Tank("T1", Consignment($"VS{n % 50:000}", null, $"mcc.officer-{n % 5}"), Consignment($"VS{(n + 1) % 50:000}", null, "mcc.officer-1")),
                    Tank("T2", Consignment($"VS{(n + 2) % 50:000}", null, "mcc.officer-2"))));
                db.TimelineEvents.Add(Event(batchId, Checkpoint.Processing, "ProcessingStageRecorded", start.AddHours(n + 1), $"proc.tech-{n % 3}"));
            }
        }

        await db.SaveChangesAsync();
    }

    private async Task RemoveSeededDataAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        // Cascades to the snapshots, events, determinations and deviations.
        await db.BatchRecords.Where(b => b.Facility.StartsWith("TEST-SEARCH")).ExecuteDeleteAsync();
        await db.AuditEntries
            .Where(a => a.Action == BatchSearchService.ExportAction && (a.EntityId.Contains("TEST-SEARCH") || a.CorrelationId!.StartsWith("test-search-")))
            .ExecuteDeleteAsync();
    }

    // A minimal RFC 4180 reader: quoted cells, doubled quotes, CRLF rows. Skips the BOM.
    private static List<List<string>> Csv(byte[] content)
    {
        Assert.Equal(Encoding.UTF8.GetPreamble(), content[..3]);
        var text = Encoding.UTF8.GetString(content[3..]);

        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') { quoted = false; }
                else { cell.Append(c); }
            }
            else if (c == '"') { quoted = true; }
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
                i++;
            }
            else { cell.Append(c); }
        }

        Assert.Empty(row); // the file ends with CRLF
        return rows;
    }

    private static BatchRecord Batch(string batchId, string facility, string productLine, BatchStatus status, DateTime firstEventAt) => new()
    {
        BatchId = batchId,
        DispatchRef = $"DN-{batchId}",
        ProductLine = productLine,
        Facility = facility,
        Status = status,
        Completeness = TraceCompleteness.Partial,
        FirstEventAt = firstEventAt,
        LastEventAt = firstEventAt.AddHours(8),
    };

    private static TimelineEvent Event(string batchId, Checkpoint checkpoint, string eventType, DateTime at, string recordedBy) => new()
    {
        BatchId = batchId,
        Checkpoint = checkpoint,
        EventType = eventType,
        OccurredAt = at,
        RecordedBy = recordedBy,
        Payload = "{}",
        SourceEventId = Guid.NewGuid(),
        ContractVersion = "v1",
    };

    private static LabDetermination Determination(string batchId, DeterminationResult result, TimelineEvent lab) => new()
    {
        DeterminationId = Guid.NewGuid(),
        BatchId = batchId,
        Result = result,
        DeterminedAt = lab.OccurredAt,
        DeterminedBy = lab.RecordedBy!,
        TimelineEventId = lab.Id,
    };

    private static DeviationRecord LabDeviation(TimelineEvent lab, string reasonCode, string parameter, decimal observed, decimal limit) => new()
    {
        BatchId = lab.BatchId,
        TimelineEventId = lab.Id,
        Checkpoint = Checkpoint.Lab,
        OccurredAt = lab.OccurredAt,
        ReasonCode = reasonCode,
        Parameter = parameter,
        ObservedValue = observed,
        Limit = limit,
    };

    private static UpstreamSnapshot Resolved(string batchId, params UpstreamSnapshotPayload.TankPart[] tanks) => new()
    {
        BatchId = batchId,
        ResolutionStatus = ResolutionStatus.Resolved,
        AttemptCount = 1,
        ResolvedAt = Day19,
        Payload = UpstreamSnapshotPayload.Serialize(new UpstreamSnapshotPayload
        {
            BatchReference = $"WR-{batchId}",
            Dispatch = new() { Reference = $"DN-{batchId}", DispatchedAt = Day19, DispatchedBy = "mcc.manager-1", TotalLitres = 1000m },
            Intake = new() { ArrivedAt = Day19, ScreenedAt = Day19, ScreenedBy = "intake.officer-1" },
            Tanks = [.. tanks],
        }),
    };

    private static UpstreamSnapshotPayload.TankPart Tank(string code, params UpstreamSnapshotPayload.ConsignmentPart[] consignments) => new()
    {
        Code = code,
        QuantityDrawnLitres = 500m,
        Consignments = [.. consignments],
    };

    private static UpstreamSnapshotPayload.ConsignmentPart Consignment(string society, string? name, string registeredBy) => new()
    {
        Reference = $"CON-{society}-{Guid.NewGuid():N}"[..30],
        Society = new() { Code = society, Name = name },
        VolumeLitres = 250m,
        RegisteredAt = Day19,
        RegisteredBy = registeredBy,
    };

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
