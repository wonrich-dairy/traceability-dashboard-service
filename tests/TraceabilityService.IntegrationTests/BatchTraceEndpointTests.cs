using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SRC.Authorization;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;
using Xunit.Abstractions;
using static TraceabilityService.Domain.Entities.UpstreamSnapshotPayload;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// GET /api/batches/{batchId}/trace (SCRUM-134). Each test seeds its own three failed
/// batches and removes them afterwards, all from one dispatch drawn from two tanks:
///   TEST-TRC-PROC failed at Processing (and again at the lab);
///   TEST-TRC-MCC left the MCC warm, and Processing and the lab deviated too;
///   TEST-TRC-GAP has no upstream snapshot.
/// </summary>
[Collection(ApiCollection.Name)]
public class BatchTraceEndpointTests(TraceabilityApiFactory factory, ITestOutputHelper output) : IAsyncLifetime
{
    private const string Dispatch = "DN-TEST-TRC-01";
    private const string Reference = "WR-TEST-TRC-01";
    private static readonly string[] SeededBatchIds = ["TEST-TRC-PROC", "TEST-TRC-MCC", "TEST-TRC-GAP"];

    private static readonly DateTime At = new(2026, 9, 19, 5, 40, 0, DateTimeKind.Utc);

    // The snapshot's own order: T2 before T1, societies not alphabetical, VS045 in both tanks.
    private static readonly string[] SnapshotSocieties = ["VS045", "VS009", "VS022"];

    public async Task InitializeAsync()
    {
        await RemoveSeededBatchesAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

        db.BatchRecords.AddRange(SeededBatchIds.Select(Batch));
        foreach (var batchId in SeededBatchIds.Where(id => id != "TEST-TRC-GAP"))
        {
            db.UpstreamSnapshots.Add(new UpstreamSnapshot
            {
                BatchId = batchId,
                ResolutionStatus = ResolutionStatus.Resolved,
                Payload = Serialize(TwoTankPayload()),
                AttemptCount = 1,
                LastAttemptAt = At.AddMinutes(1),
                ResolvedAt = At.AddMinutes(1),
            });
        }

        // Processing failure: an end temperature out of range, then the lab fails the batch.
        var procStage = Event("TEST-TRC-PROC", Checkpoint.Processing, "ProcessingStageRecorded", At.AddHours(3));
        var procHold = Event("TEST-TRC-PROC", Checkpoint.Processing, "ProcessingHoldRaised", At.AddHours(3).AddMinutes(5), """
            {"holdId": "HOLD-TRC-1", "reason": "End temperature 78.0 > 72.0 max", "failedParameter": "EndTemperatureC",
             "failedValue": "78.0 > 72.0 max", "isDeviation": true}
            """);
        var procResolved = Event("TEST-TRC-PROC", Checkpoint.Processing, "ProcessingHoldResolved", At.AddHours(4), """
            {"holdId": "HOLD-TRC-1", "raisedAtUtc": "2026-09-19T08:45:00Z", "reason": "End temperature 78.0 > 72.0 max", "resolution": "Released"}
            """);
        var procLab = Event("TEST-TRC-PROC", Checkpoint.Lab, "BatchFailed", At.AddHours(6));

        // MCC failure: 12.40C leaving the MCC. Processing and the lab deviate later as well.
        // The Mcc event is not shown (MCC data comes from the snapshot); it is here because a
        // deviation has to point at a timeline event.
        var mccMcc = Event("TEST-TRC-MCC", Checkpoint.Mcc, "MccDispatchCreated", At);
        var mccStage = Event("TEST-TRC-MCC", Checkpoint.Processing, "ProcessingStageRecorded", At.AddHours(3));
        var mccLab = Event("TEST-TRC-MCC", Checkpoint.Lab, "BatchFailed", At.AddHours(6));

        var gapStage = Event("TEST-TRC-GAP", Checkpoint.Processing, "ProcessingStageRecorded", At.AddHours(3));
        var gapLab = Event("TEST-TRC-GAP", Checkpoint.Lab, "BatchFailed", At.AddHours(6));

        db.TimelineEvents.AddRange(procStage, procHold, procResolved, procLab, mccMcc, mccStage, mccLab, gapStage, gapLab);
        await db.SaveChangesAsync();

        db.DeviationRecords.AddRange(
            Deviation(procStage, null, "EndTemperatureC", 78.00m, 72.00m, "Pasteurisation end temperature out of range"),
            Deviation(procLab, "LOW_FAT", "FatPercent", 2.80m, 3.00m, "Below the minimum"),
            Deviation(mccMcc, "TemperatureHigh", "TemperatureC", 12.40m, 4.00m, "Above 4C leaving the MCC"),
            Deviation(mccStage, null, "EndTemperatureC", 75.00m, 72.00m, null),
            Deviation(mccLab, "LOW_PH", "Ph", 6.32m, 6.60m, "Below the minimum"),
            Deviation(gapLab, "LOW_PH", "Ph", 6.40m, 6.60m, "Below the minimum"));

        db.LabDeterminations.AddRange(
            Determination("TEST-TRC-PROC", procLab),
            Determination("TEST-TRC-MCC", mccLab),
            Determination("TEST-TRC-GAP", gapLab));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => RemoveSeededBatchesAsync();

    [Fact]
    public async Task A_failed_batch_from_two_tanks_lists_both_tanks_and_the_societies_in_snapshot_order()
    {
        using var json = await GetJsonAsync("/api/batches/TEST-TRC-PROC/trace");
        var trace = json.RootElement;

        Assert.Equal("TEST-TRC-PROC", trace.GetProperty("batchId").GetString());
        Assert.Equal("Failed", trace.GetProperty("status").GetString());
        Assert.Equal("Failed", trace.GetProperty("labResult").GetString());

        var tanks = trace.GetProperty("links").GetProperty("tanks").EnumerateArray().ToList();
        Assert.Equal(["T2", "T1"], tanks.Select(t => t.GetProperty("code").GetString()));
        Assert.Equal([350m, 560m], tanks.Select(t => t.GetProperty("quantityDrawnLitres").GetDecimal()));
        Assert.Equal(
            ["VS045", "VS009", "VS022", "VS045"],
            tanks.SelectMany(t => t.GetProperty("consignments").EnumerateArray())
                .Select(c => c.GetProperty("society").GetProperty("code").GetString()));

        // Exactly the snapshot's order, each society once: not sorted by code or by anything else.
        Assert.Equal(
            SnapshotSocieties,
            trace.GetProperty("societies").EnumerateArray().Select(s => s.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task A_failure_injected_at_processing_has_processing_as_its_origin()
    {
        using var json = await GetJsonAsync("/api/batches/TEST-TRC-PROC/trace");
        var trace = json.RootElement;

        Assert.True(trace.GetProperty("hasDeviation").GetBoolean());
        Assert.Equal("Processing", trace.GetProperty("originCheckpoint").GetString());

        var origin = trace.GetProperty("origin");
        Assert.Equal("Processing", origin.GetProperty("checkpoint").GetString());
        Assert.Equal("EndTemperatureC", origin.GetProperty("parameter").GetString());
        Assert.Equal(78.00m, origin.GetProperty("observedValue").GetDecimal());
        Assert.Equal(72.00m, origin.GetProperty("limit").GetDecimal());
        Assert.Equal(At.AddHours(3), origin.GetProperty("occurredAt").GetDateTime().ToUniversalTime());
        Assert.EndsWith("Z", origin.GetProperty("occurredAt").GetString());

        Assert.Equal(
            ["WithinSpec", "WithinSpec", "Deviation", "Deviation"],
            trace.GetProperty("chain").EnumerateArray().Select(c => c.GetProperty("status").GetString()));

        // The hold's payload is read back for the trace (the other events' are not fetched).
        var hold = Assert.Single(trace.GetProperty("holds").EnumerateArray());
        Assert.Equal("HOLD-TRC-1", hold.GetProperty("holdId").GetString());
        Assert.Equal("EndTemperatureC", hold.GetProperty("failedParameter").GetString());
        Assert.Equal("Released", hold.GetProperty("resolution").GetString());
        Assert.False(hold.GetProperty("isOpen").GetBoolean());
    }

    // The AC's reading: the origin is the first checkpoint in stage order that deviated.
    // Processing and the lab also deviate here; the origin is still the MCC.
    [Fact]
    public async Task A_failure_injected_at_the_mcc_has_the_mcc_as_its_origin_even_when_processing_also_deviates()
    {
        using var json = await GetJsonAsync("/api/batches/TEST-TRC-MCC/trace");
        var trace = json.RootElement;

        Assert.Equal("Mcc", trace.GetProperty("originCheckpoint").GetString());
        var origin = trace.GetProperty("origin");
        Assert.Equal("TemperatureHigh", origin.GetProperty("reasonCode").GetString());
        Assert.Equal("TemperatureC", origin.GetProperty("parameter").GetString());
        Assert.Equal(12.40m, origin.GetProperty("observedValue").GetDecimal());
        Assert.Equal(4.00m, origin.GetProperty("limit").GetDecimal());

        var chain = trace.GetProperty("chain").EnumerateArray().ToList();
        Assert.Equal(
            ["Deviation", "WithinSpec", "Deviation", "Deviation"],
            chain.Select(c => c.GetProperty("status").GetString()));
        Assert.Equal(75.00m, Assert.Single(chain[2].GetProperty("deviations").EnumerateArray()).GetProperty("observedValue").GetDecimal());
    }

    [Fact]
    public async Task A_batch_without_a_snapshot_shows_mcc_and_intake_as_missing_data_not_left_out()
    {
        using var json = await GetJsonAsync("/api/batches/TEST-TRC-GAP/trace");
        var trace = json.RootElement;

        var chain = trace.GetProperty("chain").EnumerateArray().ToList();
        Assert.Equal(["Mcc", "Intake", "Processing", "Lab"], chain.Select(c => c.GetProperty("checkpoint").GetString()));
        Assert.Equal(
            ["MissingData", "MissingData", "WithinSpec", "Deviation"],
            chain.Select(c => c.GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.String, chain[0].GetProperty("missingReason").ValueKind);
        Assert.Equal("Lab", trace.GetProperty("originCheckpoint").GetString());

        // Written as null and empty, not left out.
        Assert.Equal(JsonValueKind.Null, trace.GetProperty("links").ValueKind);
        Assert.Empty(trace.GetProperty("societies").EnumerateArray());
    }

    [Fact]
    public async Task The_same_batch_gives_the_same_trace_on_every_request()
    {
        var client = AuthorizedClient();
        var first = await client.GetStringAsync("/api/batches/TEST-TRC-MCC/trace");
        var second = await client.GetStringAsync("/api/batches/TEST-TRC-MCC/trace");

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task An_unknown_batch_returns_a_404_problem()
    {
        var response = await AuthorizedClient().GetAsync("/api/batches/TEST-TRC-NONE/trace");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Batch not found", json.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task The_trace_needs_a_token()
    {
        var response = await factory.CreateClient().GetAsync("/api/batches/TEST-TRC-PROC/trace");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(WonrichRoles.QualityControlOfficer)]
    [InlineData(WonrichRoles.SystemAdministrator)]
    public async Task Quality_control_officers_and_administrators_get_the_trace(string role)
    {
        var response = await AuthorizedClient(role).GetAsync("/api/batches/TEST-TRC-PROC/trace");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The Quality Lab's analysts (the lab technicians) among them.
    public static TheoryData<string> RefusedRoles() =>
        [.. WonrichRoles.All.Except([WonrichRoles.QualityControlOfficer, WonrichRoles.SystemAdministrator])];

    [Theory]
    [MemberData(nameof(RefusedRoles))]
    public async Task Every_other_role_is_refused_with_403(string role)
    {
        var response = await AuthorizedClient(role).GetAsync("/api/batches/TEST-TRC-PROC/trace");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // The query-count evidence for the PR: a fixed five, however many events and deviations
    // the batch has, and one for an unknown batch. The SQL is written to the test output.
    [Fact]
    public async Task A_trace_takes_five_queries_and_an_unknown_batch_one()
    {
        var commands = new CommandRecorder();
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("TraceabilityDb");
        var options = new DbContextOptionsBuilder<TraceabilityDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 46)))
            .AddInterceptors(commands)
            .Options;

        await using (var db = new TraceabilityDbContext(options))
        {
            Assert.NotNull(await new BatchTraceService(db).GetAsync("TEST-TRC-MCC"));
        }

        foreach (var sql in commands.Sql)
        {
            output.WriteLine(sql);
            output.WriteLine("");
        }
        Assert.Equal(5, commands.Sql.Count);

        commands.Sql.Clear();
        await using (var db = new TraceabilityDbContext(options))
        {
            Assert.Null(await new BatchTraceService(db).GetAsync("TEST-TRC-NONE"));
        }
        Assert.Single(commands.Sql);
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        var response = await AuthorizedClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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
        // Cascades to the timeline events, deviations, determinations and snapshot.
        // Enumerable.Contains spelled out: see BatchesEndpointTests.
        await db.BatchRecords.Where(b => Enumerable.Contains(SeededBatchIds, b.BatchId)).ExecuteDeleteAsync();
    }

    private static UpstreamSnapshotPayload TwoTankPayload() => new()
    {
        BatchReference = Reference,
        Dispatch = new DispatchPart
        {
            Reference = Dispatch,
            DispatchedAt = At,
            DispatchedBy = "mcc.manager",
            BowserRegistration = "WP-LK-4521",
            TotalLitres = 910m,
        },
        Intake = new IntakePart
        {
            ArrivedAt = At.AddHours(2),
            ScreenedAt = At.AddHours(2).AddMinutes(5),
            ScreenedBy = "intake.officer",
            TemperatureC = 9.80m,
        },
        Tanks =
        [
            new TankPart
            {
                Code = "T2",
                QuantityDrawnLitres = 350m,
                Consignments = [Consignment("VS045", "A", 200m, -40), Consignment("VS009", "A", 150m, -35)],
            },
            new TankPart
            {
                Code = "T1",
                QuantityDrawnLitres = 560m,
                Consignments = [Consignment("VS022", "B", 300m, -30), Consignment("VS045", "B", 260m, -25)],
            },
        ],
    };

    private static ConsignmentPart Consignment(string society, string suffix, decimal litres, int minutes) => new()
    {
        Reference = $"CON-TEST-TRC-{society}-{suffix}",
        Society = new SocietyPart { Code = society, Name = $"Society {society}" },
        VolumeLitres = litres,
        RegisteredAt = At.AddMinutes(minutes),
        RegisteredBy = $"{society.ToLowerInvariant()}.officer",
    };

    private static BatchRecord Batch(string batchId) => new()
    {
        BatchId = batchId,
        DispatchRef = Dispatch,
        BatchReference = Reference,
        ProductLine = "FM",
        Facility = "FACTORY-01",
        Status = BatchStatus.Failed,
        Completeness = TraceCompleteness.Partial,
        FirstEventAt = At,
        LastEventAt = At.AddHours(6),
    };

    private static TimelineEvent Event(
        string batchId, Checkpoint checkpoint, string eventType, DateTime occurredAt, string payload = "{}") => new()
    {
        BatchId = batchId,
        Checkpoint = checkpoint,
        EventType = eventType,
        OccurredAt = occurredAt,
        RecordedBy = "tech-1",
        IsDeviation = false,
        Payload = payload,
        SourceEventId = Guid.NewGuid(),
        ContractVersion = "v1",
    };

    private static DeviationRecord Deviation(
        TimelineEvent reportedBy, string? reason, string parameter, decimal observed, decimal limit, string? description) => new()
    {
        BatchId = reportedBy.BatchId,
        TimelineEventId = reportedBy.Id,
        Checkpoint = reportedBy.Checkpoint,
        OccurredAt = reportedBy.OccurredAt,
        ReasonCode = reason,
        Parameter = parameter,
        ObservedValue = observed,
        Limit = limit,
        Description = description,
    };

    private static LabDetermination Determination(string batchId, TimelineEvent labEvent) => new()
    {
        DeterminationId = Guid.NewGuid(),
        BatchId = batchId,
        Result = DeterminationResult.Failed,
        DeterminedAt = labEvent.OccurredAt,
        DeterminedBy = "analyst-1",
        TimelineEventId = labEvent.Id,
    };
}
