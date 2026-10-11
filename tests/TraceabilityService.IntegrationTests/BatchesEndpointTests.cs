using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SRC.Authorization;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// GET /api/batches/{batchId} and GET /api/batches?dispatchRef=…&amp;batchReference=… (SCRUM-133).
/// Each test seeds its own three batches and removes them afterwards:
///   TEST-API-A and TEST-API-B come from one dispatch, so they share its dispatch
///   note and batch reference; C is from another dispatch and has no reference yet.
/// </summary>
[Collection(ApiCollection.Name)]
public class BatchesEndpointTests(TraceabilityApiFactory factory) : IAsyncLifetime
{
    private const string SharedDispatch = "DN-TEST-API-01";
    private const string SharedReference = "WR-TEST-API-01";
    private static readonly string[] SeededBatchIds = ["TEST-API-A", "TEST-API-B", "TEST-API-C"];

    private static readonly DateTime At = new(2026, 9, 19, 5, 40, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await RemoveSeededBatchesAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

        db.BatchRecords.AddRange(
            Batch("TEST-API-A", SharedDispatch, SharedReference, BatchStatus.Failed, At),
            Batch("TEST-API-B", SharedDispatch, SharedReference, BatchStatus.Processing, At.AddMinutes(30)),
            Batch("TEST-API-C", "DN-TEST-API-02", null, BatchStatus.Processing, At.AddMinutes(60)));

        // A has a Lab event but no Processing event, so Processing is a gap. The Mcc event
        // is not shown (MCC records come from the snapshot); it is here because a
        // deviation has to point at a timeline event.
        var lab = Event("TEST-API-A", Checkpoint.Lab, "BatchFailed", At.AddHours(6), """{"result": "Failed", "ph": 6.32}""");
        var mcc = Event("TEST-API-A", Checkpoint.Mcc, "MccDispatchCreated", At, """{"temperatureC": 12.40}""");
        db.TimelineEvents.AddRange(lab, mcc);

        db.UpstreamSnapshots.Add(new UpstreamSnapshot
        {
            BatchId = "TEST-API-A",
            ResolutionStatus = ResolutionStatus.Resolved,
            Payload = UpstreamSnapshotPayload.Serialize(ResolvedPayload()),
            AttemptCount = 1,
            LastAttemptAt = At.AddMinutes(1),
            ResolvedAt = At.AddMinutes(1)
        });
        await db.SaveChangesAsync();

        db.DeviationRecords.AddRange(
            new DeviationRecord
            {
                BatchId = "TEST-API-A",
                TimelineEventId = mcc.Id,
                Checkpoint = Checkpoint.Mcc,
                OccurredAt = At,
                ReasonCode = "TemperatureHigh",
                Parameter = "TemperatureC",
                ObservedValue = 12.40m,
                Limit = 4.00m,
                Description = "Above 4C leaving the MCC"
            },
            new DeviationRecord
            {
                BatchId = "TEST-API-A",
                TimelineEventId = lab.Id,
                Checkpoint = Checkpoint.Lab,
                OccurredAt = At.AddHours(6),
                ReasonCode = "PhLow",
                Parameter = "PH",
                ObservedValue = 6.32m,
                Limit = 6.60m
            });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => RemoveSeededBatchesAsync();

    [Fact]
    public async Task Get_returns_the_summary_four_checkpoint_sections_links_and_upstream_status()
    {
        var response = await AuthorizedClient().GetAsync("/api/batches/TEST-API-A");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var batch = json.RootElement;

        Assert.Equal("TEST-API-A", batch.GetProperty("batchId").GetString());
        Assert.Equal(SharedDispatch, batch.GetProperty("dispatchRef").GetString());
        Assert.Equal(SharedReference, batch.GetProperty("batchReference").GetString());
        Assert.Equal("Failed", batch.GetProperty("status").GetString());
        Assert.Equal("Resolved", batch.GetProperty("upstream").GetProperty("status").GetString());

        var sections = batch.GetProperty("checkpoints").EnumerateArray().ToList();
        Assert.Equal(["Mcc", "Intake", "Processing", "Lab"], sections.Select(s => s.GetProperty("checkpoint").GetString()));
        Assert.Equal(
            ["Recorded", "Recorded", "NoDataReceived", "Recorded"],
            sections.Select(s => s.GetProperty("state").GetString()));

        // MCC records come from the snapshot, not from the Mcc timeline event.
        var mcc = sections[0];
        Assert.Equal(
            ["ConsignmentRegistered", "DispatchCreated"],
            mcc.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("kind").GetString()));
        var deviation = Assert.Single(mcc.GetProperty("deviations").EnumerateArray());
        Assert.Equal("TemperatureHigh", deviation.GetProperty("reasonCode").GetString());
        Assert.Equal(12.40m, deviation.GetProperty("observedValue").GetDecimal());
        Assert.Equal(4.00m, deviation.GetProperty("limit").GetDecimal());

        // Times from the database are UTC like those from the snapshot, so all carry a Z.
        Assert.EndsWith("Z", batch.GetProperty("firstEventAt").GetString());
        Assert.EndsWith("Z", deviation.GetProperty("occurredAt").GetString());
        Assert.EndsWith("Z", mcc.GetProperty("lastRecordedAt").GetString());

        var intake = Assert.Single(sections[1].GetProperty("records").EnumerateArray());
        Assert.Equal("intake.officer", intake.GetProperty("recordedBy").GetString());

        // The event payload is nested as an object, not returned as a string of JSON.
        var lab = Assert.Single(sections[3].GetProperty("records").EnumerateArray());
        Assert.Equal(JsonValueKind.Object, lab.GetProperty("data").ValueKind);
        Assert.Equal(6.32m, lab.GetProperty("data").GetProperty("ph").GetDecimal());
        Assert.Equal("PhLow", Assert.Single(sections[3].GetProperty("deviations").EnumerateArray()).GetProperty("reasonCode").GetString());

        var links = batch.GetProperty("links");
        Assert.Equal(SharedDispatch, links.GetProperty("dispatch").GetProperty("reference").GetString());
        var tank = Assert.Single(links.GetProperty("tanks").EnumerateArray());
        Assert.Equal("T1", tank.GetProperty("code").GetString());
        Assert.Equal(300m, tank.GetProperty("quantityDrawnLitres").GetDecimal());
        var consignment = Assert.Single(tank.GetProperty("consignments").EnumerateArray());
        Assert.Equal("VS014", consignment.GetProperty("society").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_a_batch_with_no_history_yet_shows_mcc_and_intake_as_gaps_and_the_rest_not_yet_reached()
    {
        var response = await AuthorizedClient().GetAsync("/api/batches/TEST-API-B");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var batch = json.RootElement;
        // The batch exists, so it has passed MCC and intake: no data there is a gap, not
        // "not yet reached". Processing and the lab genuinely have not happened yet.
        Assert.Equal(
            ["NoDataReceived", "NoDataReceived", "NotYetReached", "NotYetReached"],
            batch.GetProperty("checkpoints").EnumerateArray().Select(s => s.GetProperty("state").GetString()));
        Assert.Equal(JsonValueKind.Null, batch.GetProperty("upstream").ValueKind);
        Assert.Equal(JsonValueKind.Null, batch.GetProperty("links").ValueKind);
    }

    [Fact]
    public async Task Get_an_unknown_batch_returns_a_404_problem()
    {
        var response = await AuthorizedClient().GetAsync("/api/batches/TEST-API-NONE");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Batch not found", json.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("dispatchRef=" + SharedDispatch)]
    [InlineData("batchReference=" + SharedReference)]
    [InlineData("dispatchRef=" + SharedDispatch + "&batchReference=" + SharedReference)]
    public async Task List_returns_every_batch_from_one_dispatch_oldest_first(string query)
    {
        var batchIds = await ListBatchIdsAsync(query);

        Assert.Equal(["TEST-API-A", "TEST-API-B"], batchIds);
    }

    [Theory]
    [InlineData("batchReference=WR-TEST-API-NONE")]
    [InlineData("dispatchRef=DN-TEST-API-02&batchReference=" + SharedReference)]
    public async Task List_with_no_match_returns_an_empty_list(string query)
    {
        Assert.Empty(await ListBatchIdsAsync(query));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?dispatchRef=&batchReference=%20")]
    public async Task List_without_a_filter_returns_a_400_problem(string query)
    {
        var response = await AuthorizedClient().GetAsync("/api/batches" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/api/batches/TEST-API-A")]
    [InlineData("/api/batches?dispatchRef=" + SharedDispatch)]
    public async Task Both_endpoints_need_a_token(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(WonrichRoles.QualityControlOfficer, "/api/batches/TEST-API-A")]
    [InlineData(WonrichRoles.QualityControlOfficer, "/api/batches?dispatchRef=" + SharedDispatch)]
    [InlineData(WonrichRoles.SystemAdministrator, "/api/batches/TEST-API-A")]
    [InlineData(WonrichRoles.SystemAdministrator, "/api/batches?dispatchRef=" + SharedDispatch)]
    public async Task Both_endpoints_allow_quality_control_officers_and_administrators(string role, string path)
    {
        var response = await AuthorizedClient(role).GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Every configured role except the two allowed, against both endpoints, so a role
    // added to WonrichRoles later is refused until someone decides otherwise.
    public static TheoryData<string, string> RefusedRoles()
    {
        var data = new TheoryData<string, string>();
        foreach (var role in WonrichRoles.All.Except([WonrichRoles.QualityControlOfficer, WonrichRoles.SystemAdministrator]))
        {
            data.Add(role, "/api/batches/TEST-API-A");
            data.Add(role, "/api/batches?dispatchRef=" + SharedDispatch);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedRoles))]
    public async Task Both_endpoints_refuse_every_other_role_with_403(string role, string path)
    {
        var response = await AuthorizedClient(role).GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<List<string?>> ListBatchIdsAsync(string query)
    {
        var response = await AuthorizedClient().GetAsync("/api/batches?" + query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. json.RootElement.EnumerateArray().Select(b => b.GetProperty("batchId").GetString())];
    }

    private HttpClient AuthorizedClient(string role = WonrichRoles.QualityControlOfficer)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(role: role));
        return client;
    }

    private async Task RemoveSeededBatchesAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        // Cascades to the timeline events, deviations and snapshot. Enumerable.Contains
        // spelled out: on an array, C# 14 picks the span overload, which EF Core 9 cannot
        // translate.
        await db.BatchRecords.Where(b => Enumerable.Contains(SeededBatchIds, b.BatchId)).ExecuteDeleteAsync();
    }

    private static UpstreamSnapshotPayload ResolvedPayload() => new()
    {
        BatchReference = SharedReference,
        Dispatch = new UpstreamSnapshotPayload.DispatchPart
        {
            Reference = SharedDispatch,
            DispatchedAt = At,
            DispatchedBy = "mcc.manager",
            TotalLitres = 300m
        },
        Intake = new UpstreamSnapshotPayload.IntakePart
        {
            ArrivedAt = At.AddHours(2),
            ScreenedAt = At.AddHours(2),
            ScreenedBy = "intake.officer",
            TemperatureC = 9.80m
        },
        Tanks =
        [
            new UpstreamSnapshotPayload.TankPart
            {
                Code = "T1",
                QuantityDrawnLitres = 300m,
                Consignments =
                [
                    new UpstreamSnapshotPayload.ConsignmentPart
                    {
                        Reference = "CON-TEST-API-VS014",
                        Society = new UpstreamSnapshotPayload.SocietyPart { Code = "VS014" },
                        VolumeLitres = 300m,
                        RegisteredAt = At.AddMinutes(-40),
                        RegisteredBy = "vs014.officer"
                    },
                ]
            },
        ]
    };

    private static BatchRecord Batch(
        string batchId, string dispatchRef, string? batchReference, BatchStatus status, DateTime firstEventAt) => new()
    {
        BatchId = batchId,
        DispatchRef = dispatchRef,
        BatchReference = batchReference,
        ProductLine = "FM",
        Facility = "FACTORY-01",
        Status = status,
        Completeness = TraceCompleteness.Partial,
        FirstEventAt = firstEventAt,
        LastEventAt = firstEventAt
    };

    private static TimelineEvent Event(
        string batchId, Checkpoint checkpoint, string eventType, DateTime occurredAt, string payload) => new()
    {
        BatchId = batchId,
        Checkpoint = checkpoint,
        EventType = eventType,
        OccurredAt = occurredAt,
        IsDeviation = checkpoint == Checkpoint.Mcc,
        Payload = payload,
        SourceEventId = Guid.NewGuid(),
        ContractVersion = "v1"
    };
}
