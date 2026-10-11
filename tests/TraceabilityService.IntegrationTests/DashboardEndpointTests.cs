using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SRC.Authorization;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// The QC dashboard endpoints (SCRUM-135). "Today" is pinned to 2026-10-11 in Colombo, and
/// the data lives at facility TEST-DASH, so the figures are known exactly whatever else is
/// in the database. The default range is therefore 2026-09-12 to 2026-10-11.
/// </summary>
[Collection(ApiCollection.Name)]
public class DashboardEndpointTests(TraceabilityApiFactory factory) : IAsyncLifetime
{
    private const string Facility = "TEST-DASH";
    private const string OtherFacility = "TEST-DASH-OTHER";
    private const string ConsumerGroup = "test-dashboard";

    private static readonly DateOnly Today = new(2026, 10, 11);
    private static readonly DateOnly Yesterday = new(2026, 10, 10);
    private static readonly DateTime ProcessedAt = new(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string> Endpoints =>
        ["summary", "trend", "failure-reasons", "recent-failures", "societies"];

    private WebApplicationFactory<Program>? _app;

    // The shared API, with the factory calendar pinned to 06:00 UTC on 2026-10-11.
    private WebApplicationFactory<Program> App => _app ??= factory.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
            services.AddSingleton(new FactoryCalendar(new FixedTime(new DateTimeOffset(2026, 10, 11, 6, 0, 0, TimeSpan.Zero))))));

    public async Task InitializeAsync()
    {
        await RemoveSeededDataAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

        db.DailyQualityAggregates.AddRange(
            //                      date         line  batches pend clear fail hold  mcc intake proc lab
            Aggregate(Facility, Today,              "FM", 3,      1,   1,    1,   0,    1,  0,     0,   1),
            Aggregate(Facility, Yesterday,          "FM", 4,      0,   3,    1,   0,    0,  1,     0,   2),
            // Nothing decided: its fail rate must be null, not 0.
            Aggregate(Facility, Yesterday,          "SY", 2,      1,   0,    0,   1,    0,  0,     1,   0),
            // Outside the default range.
            Aggregate(Facility, new(2026, 9, 1),    "FM", 5,      0,   5,    0,   0,    0,  0,     0,   0),
            // Another facility.
            Aggregate(OtherFacility, Yesterday,     "FM", 9,      0,   0,    9,   0,    0,  0,     0,   9));

        db.DailyReasonCodeAggregates.AddRange(
            Reason(Facility, Today, "FM", "TemperatureHigh", 1),
            Reason(Facility, Today, "FM", "PhLow", 1),
            Reason(Facility, Yesterday, "FM", "PhLow", 2),
            Reason(Facility, Yesterday, "FM", "TemperatureHigh", 1),
            Reason(Facility, Yesterday, "SY", "TemperatureLow", 1),
            Reason(Facility, new(2026, 9, 1), "FM", "PhLow", 10),
            Reason(OtherFacility, Yesterday, "FM", "SensoryAbnormal", 9));

        db.BatchRecords.AddRange(
            Batch("TEST-DASH-1", Facility, "FM", BatchStatus.Failed, Utc(2026, 10, 11, 3, 0)),
            // 18:45 UTC on the 10th is 00:15 on the 11th in Colombo: inside the range.
            Batch("TEST-DASH-6", Facility, "FM", BatchStatus.Failed, Utc(2026, 10, 10, 18, 45)),
            Batch("TEST-DASH-2", Facility, "FM", BatchStatus.Failed, Utc(2026, 10, 10, 5, 0)),
            Batch("TEST-DASH-3", Facility, "SY", BatchStatus.Failed, Utc(2026, 10, 9, 5, 0)),
            Batch("TEST-DASH-4", Facility, "FM", BatchStatus.Cleared, Utc(2026, 10, 11, 4, 0)),
            Batch("TEST-DASH-5", Facility, "FM", BatchStatus.Failed, Utc(2026, 8, 1, 5, 0)),
            // 18:31 UTC on the 11th is the 12th in Colombo: after today, outside the range.
            Batch("TEST-DASH-7", Facility, "FM", BatchStatus.Failed, Utc(2026, 10, 11, 18, 31)),
            Batch("TEST-DASH-8", OtherFacility, "FM", BatchStatus.Failed, Utc(2026, 10, 11, 4, 0)));

        var lab = new TimelineEvent
        {
            BatchId = "TEST-DASH-1",
            Checkpoint = Checkpoint.Lab,
            EventType = "BatchFailed",
            OccurredAt = Utc(2026, 10, 11, 3, 0),
            Payload = "{}",
            SourceEventId = Guid.NewGuid(),
            ContractVersion = "v1"
        };
        db.TimelineEvents.Add(lab);

        // Snapshots for the societies panel. DASH-1 and DASH-2 share consignment C1 (a split
        // dispatch); DASH-3 is still being looked up; DASH-5 is outside the range and DASH-8
        // at another facility, so their very tight margins must not appear.
        db.UpstreamSnapshots.AddRange(
            Snapshot("TEST-DASH-1",
                SocietyConsignment("CON-DASH-C1", "VS001", 0.0500m, "Snf"),
                SocietyConsignment("CON-DASH-C2", "VS002", 0.0100m, "FatPercent")),
            Snapshot("TEST-DASH-2",
                SocietyConsignment("CON-DASH-C1", "VS001", 0.0500m, "Snf"),
                SocietyConsignment("CON-DASH-C3", "VS003", margin: null, measure: null)),
            new UpstreamSnapshot { BatchId = "TEST-DASH-3", ResolutionStatus = ResolutionStatus.PendingRetry },
            Snapshot("TEST-DASH-5", SocietyConsignment("CON-DASH-C4", "VS004", 0.0010m, "Snf")),
            Snapshot("TEST-DASH-8", SocietyConsignment("CON-DASH-C5", "VS005", 0.0001m, "Snf")));

        db.ProcessedMessages.Add(new ProcessedMessage
        {
            ConsumerGroup = ConsumerGroup,
            SourceEventId = Guid.NewGuid(),
            Topic = "wonrich.quality-lab.batch-determinations.v1",
            ProcessedAt = ProcessedAt
        });
        await db.SaveChangesAsync();

        // Two reasons on one batch, one of them twice: reported once each, in order.
        db.DeviationRecords.AddRange(
            Deviation(lab, "SensoryAbnormal"),
            Deviation(lab, "PhLow"),
            Deviation(lab, "PhLow"));
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
    public async Task Summary_counts_today_and_the_range_and_deviations_per_checkpoint()
    {
        var json = await GetOkAsync($"/api/dashboard/summary?facility={Facility}");

        var today = json.GetProperty("today");
        Assert.Equal("2026-10-11", today.GetProperty("date").GetString());
        Assert.Equal([1, 1, 1, 0], Counts(today));

        // Today and yesterday; 2026-09-01 is outside the default range, and the other facility is excluded.
        Assert.Equal([2, 4, 2, 1], Counts(json.GetProperty("range")));
        Assert.Equal(
            [("Mcc", 1), ("Intake", 1), ("Processing", 1), ("Lab", 3)],
            json.GetProperty("deviations").EnumerateArray()
                .Select(d => (d.GetProperty("checkpoint").GetString(), d.GetProperty("count").GetInt32())));

        Assert.Equal("2026-09-12", json.GetProperty("from").GetString());
        Assert.Equal("2026-10-11", json.GetProperty("to").GetString());
        Assert.Equal("2026-10-11T05:00:00Z", json.GetProperty("dataAsOf").GetString());
    }

    [Fact]
    public async Task Summary_filters_by_product_line()
    {
        var json = await GetOkAsync($"/api/dashboard/summary?facility={Facility}&productLine=fm");

        Assert.Equal("FM", json.GetProperty("productLine").GetString());
        Assert.Equal([1, 4, 2, 0], Counts(json.GetProperty("range")));
    }

    [Fact]
    public async Task Trend_gives_each_day_and_product_line_with_a_null_fail_rate_when_nothing_was_decided()
    {
        var json = await GetOkAsync($"/api/dashboard/trend?facility={Facility}");

        var points = json.GetProperty("points").EnumerateArray()
            .Select(p => (
                p.GetProperty("date").GetString(),
                p.GetProperty("productLine").GetString(),
                p.GetProperty("failedCount").GetInt32(),
                p.GetProperty("failRate").ValueKind == JsonValueKind.Null ? (double?)null : p.GetProperty("failRate").GetDouble()))
            .ToList();

        Assert.Equal(
            [("2026-10-10", "FM", 1, 0.25), ("2026-10-10", "SY", 0, null), ("2026-10-11", "FM", 1, 0.5)],
            points);
    }

    [Fact]
    public async Task Failure_reasons_are_ranked_by_count()
    {
        var json = await GetOkAsync($"/api/dashboard/failure-reasons?facility={Facility}");

        Assert.Equal(
            [("PhLow", 3), ("TemperatureHigh", 2), ("TemperatureLow", 1)],
            json.GetProperty("reasons").EnumerateArray()
                .Select(r => (r.GetProperty("reasonCode").GetString(), r.GetProperty("count").GetInt32())));
    }

    [Fact]
    public async Task Recent_failures_are_newest_first_within_the_factory_days_of_the_range_with_a_trace_link()
    {
        var json = await GetOkAsync($"/api/dashboard/recent-failures?facility={Facility}");

        var batches = json.GetProperty("batches").EnumerateArray().ToList();
        Assert.Equal(
            ["TEST-DASH-1", "TEST-DASH-6", "TEST-DASH-2", "TEST-DASH-3"],
            batches.Select(b => b.GetProperty("batchId").GetString()));

        var first = batches[0];
        Assert.Equal("/api/batches/TEST-DASH-1", first.GetProperty("tracePath").GetString());
        Assert.Equal(["PhLow", "SensoryAbnormal"], first.GetProperty("reasonCodes").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("2026-10-11T03:00:00Z", first.GetProperty("failedAt").GetString());
    }

    [Theory]
    [InlineData("&limit=2", new[] { "TEST-DASH-1", "TEST-DASH-6" })]
    [InlineData("&productLine=SY", new[] { "TEST-DASH-3" })]
    [InlineData("&from=2026-10-01&to=2026-10-10", new[] { "TEST-DASH-2", "TEST-DASH-3" })]
    public async Task Recent_failures_honour_the_limit_and_filters(string query, string[] expected)
    {
        var json = await GetOkAsync($"/api/dashboard/recent-failures?facility={Facility}{query}");

        Assert.Equal(expected, json.GetProperty("batches").EnumerateArray().Select(b => b.GetProperty("batchId").GetString()));
    }

    [Fact]
    public async Task Societies_are_ranked_from_the_snapshots_of_batches_in_range_and_say_so()
    {
        var json = await GetOkAsync($"/api/dashboard/societies?facility={Facility}");

        Assert.Equal("upstream snapshots", json.GetProperty("source").GetString());
        Assert.Equal("societies that contributed to a batch in range", json.GetProperty("coverage").GetString());

        var societies = json.GetProperty("societies").EnumerateArray().ToList();
        Assert.Equal(["VS002", "VS001", "VS003"], societies.Select(s => s.GetProperty("societyCode").GetString()));

        var vs002 = societies[0];
        Assert.Equal(0.0100m, vs002.GetProperty("tightestMargin").GetDecimal());
        Assert.Equal("FatPercent", vs002.GetProperty("tightestMeasure").GetString());

        // C1 is in both DASH-1 and DASH-2: one consignment, two batches.
        var vs001 = societies[1];
        Assert.Equal(1, vs001.GetProperty("consignmentCount").GetInt32());
        Assert.Equal(2, vs001.GetProperty("batchCount").GetInt32());

        Assert.Equal(JsonValueKind.Null, societies[2].GetProperty("tightestMargin").ValueKind);
    }

    // Each filter on each endpoint, against the seeded rows. Expected values are reduced to
    // a short string per endpoint: summary "pending,cleared,failed,onHold" for the range,
    // trend "date line failRate", failure-reasons "code:count", recent failures and
    // societies their IDs, in order.
    [Theory]
    // facility: the other facility's rows only
    [InlineData("summary", "facility=" + OtherFacility, "0,0,9,0")]
    [InlineData("trend", "facility=" + OtherFacility, "2026-10-10 FM 1")]
    [InlineData("failure-reasons", "facility=" + OtherFacility, "SensoryAbnormal:9")]
    [InlineData("recent-failures", "facility=" + OtherFacility, "TEST-DASH-8")]
    [InlineData("societies", "facility=" + OtherFacility, "VS005")]
    // productLine: SY only
    [InlineData("summary", "facility=" + Facility + "&productLine=SY", "1,0,0,1")]
    [InlineData("trend", "facility=" + Facility + "&productLine=SY", "2026-10-10 SY null")]
    [InlineData("failure-reasons", "facility=" + Facility + "&productLine=SY", "TemperatureLow:1")]
    [InlineData("recent-failures", "facility=" + Facility + "&productLine=SY", "TEST-DASH-3")]
    [InlineData("societies", "facility=" + Facility + "&productLine=SY", "")]   // its only SY batch is unresolved
    // from/to: yesterday only
    [InlineData("summary", "facility=" + Facility + "&from=2026-10-10&to=2026-10-10", "1,3,1,1")]
    [InlineData("trend", "facility=" + Facility + "&from=2026-10-10&to=2026-10-10", "2026-10-10 FM 0.25|2026-10-10 SY null")]
    [InlineData("failure-reasons", "facility=" + Facility + "&from=2026-10-10&to=2026-10-10", "PhLow:2|TemperatureHigh:1|TemperatureLow:1")]
    [InlineData("recent-failures", "facility=" + Facility + "&from=2026-10-10&to=2026-10-10", "TEST-DASH-2")]
    [InlineData("societies", "facility=" + Facility + "&from=2026-10-10&to=2026-10-10", "VS001|VS003")]   // DASH-2 only
    public async Task Each_filter_narrows_each_endpoint(string endpoint, string query, string expected)
    {
        var json = await GetOkAsync($"/api/dashboard/{endpoint}?{query}");

        var actual = endpoint switch
        {
            "summary" => string.Join(",", Counts(json.GetProperty("range"))),
            "trend" => string.Join("|", json.GetProperty("points").EnumerateArray().Select(p =>
                $"{p.GetProperty("date").GetString()} {p.GetProperty("productLine").GetString()} " +
                (p.GetProperty("failRate").ValueKind == JsonValueKind.Null ? "null" : p.GetProperty("failRate").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)))),
            "failure-reasons" => string.Join("|", json.GetProperty("reasons").EnumerateArray().Select(r =>
                $"{r.GetProperty("reasonCode").GetString()}:{r.GetProperty("count").GetInt32()}")),
            "recent-failures" => string.Join("|", json.GetProperty("batches").EnumerateArray().Select(b => b.GetProperty("batchId").GetString())),
            _ => string.Join("|", json.GetProperty("societies").EnumerateArray().Select(s => s.GetProperty("societyCode").GetString())),
        };

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task An_empty_range_is_zero_counts_not_an_error(string endpoint)
    {
        var json = await GetOkAsync($"/api/dashboard/{endpoint}?facility={Facility}&from=2026-01-01&to=2026-01-31");

        switch (endpoint)
        {
            case "summary":
                Assert.Equal([0, 0, 0, 0], Counts(json.GetProperty("range")));
                Assert.All(json.GetProperty("deviations").EnumerateArray(), d => Assert.Equal(0, d.GetProperty("count").GetInt32()));
                break;
            case "trend":
                Assert.Empty(json.GetProperty("points").EnumerateArray());
                break;
            case "failure-reasons":
                Assert.Empty(json.GetProperty("reasons").EnumerateArray());
                break;
            case "societies":
                Assert.Empty(json.GetProperty("societies").EnumerateArray());
                break;
            default:
                Assert.Empty(json.GetProperty("batches").EnumerateArray());
                break;
        }
    }

    [Theory]
    [InlineData("summary?from=2026-10-02&to=2026-10-01", "from")]
    [InlineData("trend?from=2025-01-01&to=2026-10-01", "to")]
    [InlineData("failure-reasons?productLine=XX", "productLine")]
    [InlineData("recent-failures?limit=0", "limit")]
    [InlineData("recent-failures?limit=51", "limit")]
    [InlineData("societies?limit=0", "limit")]
    public async Task Invalid_parameters_are_a_400_problem_naming_the_parameter(string pathAndQuery, string parameter)
    {
        var response = await Client(WonrichRoles.QualityControlOfficer).GetAsync($"/api/dashboard/{pathAndQuery}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(parameter, out _), json.RootElement.GetRawText());
    }

    [Fact]
    public async Task Data_as_of_is_null_when_nothing_has_been_processed()
    {
        await RemoveProcessedMessagesAsync();

        var json = await GetOkAsync($"/api/dashboard/summary?facility={Facility}");

        Assert.Equal(JsonValueKind.Null, json.GetProperty("dataAsOf").ValueKind);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Every_endpoint_allows_a_qco_refuses_an_analyst_and_needs_a_token(string endpoint)
    {
        var path = $"/api/dashboard/{endpoint}";

        Assert.Equal(HttpStatusCode.OK, (await Client(WonrichRoles.QualityControlOfficer).GetAsync(path)).StatusCode);
        // The lab's role is QualityAnalyst; a token naming a LabTechnician role, which is not
        // one of the configured roles, is refused the same way.
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(WonrichRoles.QualityAnalyst).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("LabTechnician").GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await App.CreateClient().GetAsync(path)).StatusCode);
    }

    private async Task<JsonElement> GetOkAsync(string path)
    {
        var response = await Client(WonrichRoles.QualityControlOfficer).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private HttpClient Client(string role)
    {
        var client = App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(role: role));
        return client;
    }

    private static int[] Counts(JsonElement counts) =>
    [
        counts.GetProperty("pending").GetInt32(),
        counts.GetProperty("cleared").GetInt32(),
        counts.GetProperty("failed").GetInt32(),
        counts.GetProperty("onHold").GetInt32(),
    ];

    private async Task RemoveSeededDataAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        await db.DailyQualityAggregates.Where(a => a.Facility.StartsWith("TEST-DASH")).ExecuteDeleteAsync();
        await db.DailyReasonCodeAggregates.Where(a => a.Facility.StartsWith("TEST-DASH")).ExecuteDeleteAsync();
        // Cascades to the timeline event and deviations.
        await db.BatchRecords.Where(b => b.Facility.StartsWith("TEST-DASH")).ExecuteDeleteAsync();
        await RemoveProcessedMessagesAsync();
    }

    private async Task RemoveProcessedMessagesAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        await db.ProcessedMessages.Where(m => m.ConsumerGroup == ConsumerGroup).ExecuteDeleteAsync();
    }

    private static DateTime Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static DailyQualityAggregate Aggregate(
        string facility, DateOnly date, string productLine, int batches,
        int pending, int cleared, int failed, int onHold,
        int mcc, int intake, int processing, int lab) => new()
    {
        Date = date,
        ProductLine = productLine,
        Facility = facility,
        BatchCount = batches,
        PendingCount = pending,
        ClearedCount = cleared,
        FailedCount = failed,
        OnHoldCount = onHold,
        DeviationCount = mcc + intake + processing + lab,
        MccDeviationCount = mcc,
        IntakeDeviationCount = intake,
        ProcessingDeviationCount = processing,
        LabDeviationCount = lab,
        UpdatedAt = ProcessedAt
    };

    private static DailyReasonCodeAggregate Reason(
        string facility, DateOnly date, string productLine, string reasonCode, int count) => new()
    {
        Date = date,
        ProductLine = productLine,
        Facility = facility,
        ReasonCode = reasonCode,
        DeviationCount = count,
        UpdatedAt = ProcessedAt
    };

    private static BatchRecord Batch(string batchId, string facility, string productLine, BatchStatus status, DateTime lastEventAt) => new()
    {
        BatchId = batchId,
        ProductLine = productLine,
        Facility = facility,
        Status = status,
        Completeness = TraceCompleteness.Partial,
        FirstEventAt = lastEventAt.AddHours(-6),
        LastEventAt = lastEventAt
    };

    private static UpstreamSnapshot Snapshot(string batchId, params UpstreamSnapshotPayload.ConsignmentPart[] consignments) => new()
    {
        BatchId = batchId,
        ResolutionStatus = ResolutionStatus.Resolved,
        AttemptCount = 1,
        ResolvedAt = ProcessedAt,
        Payload = UpstreamSnapshotPayload.Serialize(new UpstreamSnapshotPayload
        {
            BatchReference = $"WR-{batchId}",
            Dispatch = new UpstreamSnapshotPayload.DispatchPart { Reference = $"DN-{batchId}", DispatchedAt = ProcessedAt, TotalLitres = 600m },
            Intake = new UpstreamSnapshotPayload.IntakePart { ArrivedAt = ProcessedAt, ScreenedAt = ProcessedAt },
            Tanks = [new UpstreamSnapshotPayload.TankPart { Code = "T1", QuantityDrawnLitres = 600m, Consignments = [.. consignments] }]
        })
    };

    private static UpstreamSnapshotPayload.ConsignmentPart SocietyConsignment(
        string reference, string society, decimal? margin, string? measure) => new()
    {
        Reference = reference,
        Society = new UpstreamSnapshotPayload.SocietyPart { Code = society },
        VolumeLitres = 300m,
        RegisteredAt = ProcessedAt,
        Panel = new UpstreamSnapshotPayload.PanelPart
        {
            FatPercentage = 4.00m,
            SnfPercentage = 8.60m,
            ClrReading = 28.00m,
            TemperatureC = 5.00m,
            Verdict = "Accepted",
            TightestMargin = margin,
            TightestMeasure = measure
        }
    };

    private static DeviationRecord Deviation(TimelineEvent lab, string reasonCode) => new()
    {
        BatchId = lab.BatchId,
        TimelineEventId = lab.Id,
        Checkpoint = Checkpoint.Lab,
        OccurredAt = lab.OccurredAt,
        ReasonCode = reasonCode
    };

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
