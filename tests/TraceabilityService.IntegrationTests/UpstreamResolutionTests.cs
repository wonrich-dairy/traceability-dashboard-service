using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SRC.Authorization;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Infrastructure.Kafka;
using TraceabilityService.Infrastructure.MccIntake;
using Wonrich.Processing.Contracts.Events;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// Upstream resolution end to end against real MySQL (SCRUM-132): a Processing event through the
/// stage consumer's processor, the retry worker resolving the new batch's snapshot from the MCC
/// and Intake Service, and the batch read back through GET /api/batches/{id}. Both the MCC and
/// Intake Service and the Auth Service are a recording stub handler. Each test runs its own app,
/// with the worker on, on the shared database, and removes what it wrote.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UpstreamResolutionTests(TraceabilityApiFactory factory) : IAsyncLifetime
{
    private const string Facility = ProcessingEventApplier.DefaultFacility;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly StubUpstream _stub = new();
    private WebApplicationFactory<Program> _app = null!;

    public Task InitializeAsync()
    {
        _app = factory.WithWebHostBuilder(web =>
        {
            web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:Auth:BaseUrl"] = "http://auth.test/",
                ["Services:MccIntake:BaseUrl"] = "http://mcc.test/",
                ["Services:MccIntake:ClientId"] = "svc-traceability",
                ["Services:MccIntake:ClientSecret"] = "test-secret",
                // Only the attempt queued by the consumer, and the worker's first poll, run
                // during a test.
                ["Upstream:PollIntervalSeconds"] = "3600",
            }));
            web.ConfigureServices(services =>
            {
                services.AddHttpClient(ServiceTokenProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _stub);
                services.AddHttpClient<IMccIntakeClient, MccIntakeClient>().ConfigurePrimaryHttpMessageHandler(() => _stub);
            });
        });

        // Starts the app, and with it the worker.
        _ = _app.Services;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task A_multi_tank_batch_is_stored_whole_and_comes_back_through_the_batch_endpoint()
    {
        await using var test = new TestBatch(_app, dayOfYear: 301);
        _stub.Map($"/api/factory/batches?dispatchNote={test.Dispatch}", HttpStatusCode.OK, new[] { test.Intake() });
        _stub.Map($"/api/factory/batches/{test.Reference}/trace", HttpStatusCode.OK, test.Trace(
            new TracedTankView("T1", "Tank 1", 560m,
                [test.Consignment("CON-1", "VS009", 300m), test.Consignment("CON-2", "VS045", 260m)], []),
            new TracedTankView("T2", "Tank 2", 350m,
                [test.Consignment("CON-3", "VS014", 350m)], [])));

        await test.StageAsync(test.Allocated());
        await test.WaitForSnapshotAsync(s => s.ResolutionStatus == ResolutionStatus.Resolved);

        using var batch = await test.GetAsync();
        var root = batch.RootElement;
        Assert.Equal(test.Reference, root.GetProperty("batchReference").GetString());
        Assert.Equal("Resolved", root.GetProperty("upstream").GetProperty("status").GetString());

        var links = root.GetProperty("links");
        Assert.Equal((test.Dispatch, 910m),
            (links.GetProperty("dispatch").GetProperty("reference").GetString(), links.GetProperty("dispatch").GetProperty("totalLitres").GetDecimal()));

        var tanks = links.GetProperty("tanks").EnumerateArray().Select(tank => (
            Code: tank.GetProperty("code").GetString(),
            Drawn: tank.GetProperty("quantityDrawnLitres").GetDecimal(),
            Consignments: string.Join(", ", tank.GetProperty("consignments").EnumerateArray().Select(c =>
                $"{c.GetProperty("reference").GetString()} {c.GetProperty("society").GetProperty("code").GetString()} {c.GetProperty("volumeLitres").GetDecimal()}"))))
            .ToList();
        Assert.Equal(
        [
            ("T1", 560m, $"{test.Suffix}-CON-1 VS009 300, {test.Suffix}-CON-2 VS045 260"),
            ("T2", 350m, $"{test.Suffix}-CON-3 VS014 350"),
        ], tanks);

        // The MCC section: one record per consignment and one for the dispatch.
        var mcc = Section(root, "Mcc");
        Assert.Equal("Recorded", mcc.GetProperty("state").GetString());
        Assert.Equal(4, mcc.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task A_404_upstream_still_returns_the_batch_marked_unavailable_with_its_gaps_named()
    {
        await using var test = new TestBatch(_app, dayOfYear: 302);
        _stub.Map($"/api/factory/batches?dispatchNote={test.Dispatch}", HttpStatusCode.OK, new[] { test.Intake() });
        _stub.Map($"/api/factory/batches/{test.Reference}/trace", HttpStatusCode.NotFound, new
        {
            title = "Batch not found",
            status = 404,
            detail = $"No batch carries the reference '{test.Reference}'.",
            code = "entity_not_found",
        });

        await test.StageAsync(test.Allocated());
        await test.WaitForSnapshotAsync(s => s.ResolutionStatus == ResolutionStatus.Unavailable);

        using var batch = await test.GetAsync();
        var root = batch.RootElement;

        // Marked unavailable, saying why, rather than left without the fields.
        var upstream = root.GetProperty("upstream");
        Assert.Equal("Unavailable", upstream.GetProperty("status").GetString());
        Assert.Equal(1, upstream.GetProperty("attemptCount").GetInt32());
        Assert.Contains(test.Reference, upstream.GetProperty("failureReason").GetString());

        // MCC and Intake are named as gaps; what Processing reported is still there.
        Assert.Equal("NoDataReceived", Section(root, "Mcc").GetProperty("state").GetString());
        Assert.Equal("NoDataReceived", Section(root, "Intake").GetProperty("state").GetString());
        Assert.Equal("Recorded", Section(root, "Processing").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("links").ValueKind);
    }

    [Fact]
    public async Task Reading_a_resolved_batch_twice_makes_no_request_to_the_mcc_and_intake_service()
    {
        await using var test = new TestBatch(_app, dayOfYear: 303);
        _stub.Map($"/api/factory/batches?dispatchNote={test.Dispatch}", HttpStatusCode.OK, new[] { test.Intake() });
        _stub.Map($"/api/factory/batches/{test.Reference}/trace", HttpStatusCode.OK, test.Trace(
            new TracedTankView("T1", "Tank 1", 560m, [test.Consignment("CON-1", "VS009", 560m)], [])));

        await test.StageAsync(test.Allocated());
        await test.WaitForSnapshotAsync(s => s.ResolutionStatus == ResolutionStatus.Resolved);

        (await test.GetAsync()).Dispose();
        (await test.GetAsync()).Dispose();

        // Resolving is the lookup by dispatch note, which gives the batch reference, and the one
        // trace. The two reads added nothing.
        Assert.Equal(
        [
            $"/api/factory/batches?dispatchNote={test.Dispatch}",
            $"/api/factory/batches/{test.Reference}/trace",
        ], _stub.MccRequestsFor(test));
        Assert.Single(_stub.MccRequestsFor(test), path => path.EndsWith("/trace"));
    }

    [Fact]
    public async Task With_the_mcc_and_intake_service_down_the_event_is_stored_and_the_snapshot_left_pending()
    {
        // Nothing mapped: every MCC and Intake request gets a 500.
        await using var test = new TestBatch(_app, dayOfYear: 304);
        var allocated = test.Allocated();

        Assert.Equal(EventOutcome.Handled, await test.StageAsync(allocated));
        var snapshot = await test.WaitForSnapshotAsync(s => s.FailureReason is not null);

        Assert.Equal((ResolutionStatus.PendingRetry, 1), (snapshot.ResolutionStatus, snapshot.AttemptCount));
        Assert.Contains("500", snapshot.FailureReason);
        Assert.True(snapshot.NextAttemptAt > snapshot.LastAttemptAt);
        Assert.NotEmpty(_stub.MccRequestsFor(test));

        // The event committed before the lookup: on the timeline, and recorded as processed.
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
            Assert.Equal(["MilkAllocatedToMixingTank"],
                await db.TimelineEvents.Where(e => e.BatchId == test.BatchId).Select(e => e.EventType).ToListAsync());
            Assert.True(await db.ProcessedMessages.AnyAsync(m => m.SourceEventId == allocated.EventId));
        }

        using var batch = await test.GetAsync();
        Assert.Equal("PendingRetry", batch.RootElement.GetProperty("upstream").GetProperty("status").GetString());
        Assert.Equal("Recorded", Section(batch.RootElement, "Processing").GetProperty("state").GetString());
    }

    private static JsonElement Section(JsonElement batch, string checkpoint) =>
        batch.GetProperty("checkpoints").EnumerateArray().Single(s => s.GetProperty("checkpoint").GetString() == checkpoint);

    /// <summary>
    /// The Auth Service's sign-in, and the MCC and Intake Service: mapped paths answer with their
    /// body, any other MCC request with a 500. Records every MCC request.
    /// </summary>
    private sealed class StubUpstream : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, (HttpStatusCode Status, string Body)> _routes = new();
        private readonly ConcurrentQueue<string> _mccRequests = new();

        public void Map(string pathAndQuery, HttpStatusCode status, object body) =>
            _routes[pathAndQuery] = (status, JsonSerializer.Serialize(body, Json));

        public List<string> MccRequestsFor(TestBatch test) =>
            [.. _mccRequests.Where(path => path.Contains(test.Dispatch) || path.Contains(test.Reference))];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "auth.test")
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    accessToken = "service-token",
                    expiresAtUtc = DateTime.UtcNow.AddHours(1),
                }, Json)));
            }

            _mccRequests.Enqueue(uri.PathAndQuery);
            return Task.FromResult(_routes.TryGetValue(uri.PathAndQuery, out var route)
                ? JsonResponse(route.Status, route.Body)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>One batch from its own dispatch, on a production day of its own at the factory facility.</summary>
    private sealed class TestBatch(WebApplicationFactory<Program> app, int dayOfYear) : IAsyncDisposable
    {
        private readonly DateOnly _day = new DateOnly(2026, 1, 1).AddDays(dayOfYear - 1);

        public string Suffix { get; } = Guid.NewGuid().ToString("N")[..8];

        public string BatchId => $"{dayOfYear}-FM-{Suffix}";

        public string Dispatch => $"DN-TEST-{Suffix}";

        public string Reference => $"WR-TEST-{Suffix}";

        private DateTime Morning => _day.ToDateTime(new TimeOnly(3, 0), DateTimeKind.Utc);

        public MilkAllocatedToMixingTankEvent Allocated() => new()
        {
            BatchId = BatchId, BatchCode = BatchId, DispatchNumber = Dispatch, AllocatedAtUtc = Morning, AllocatedBy = "tech-1",
        };

        public async Task<EventOutcome> StageAsync(ProcessingEventBase processingEvent)
        {
            var processor = app.Services.GetRequiredKeyedService<EventProcessor>(ProcessingConsumers.StageEvents);
            return await processor.HandleOnceAsync(new ConsumedEvent(
                processor.Options.Topic, Partition: 0, Offset: 0, processingEvent.BatchId, processingEvent.GetType().Name,
                processingEvent.EventId, CorrelationId: $"test-{Suffix}",
                JsonSerializer.Serialize(processingEvent, processingEvent.GetType(), Json)));
        }

        // The worker runs beside the test, so wait for it rather than for a fixed time.
        public async Task<UpstreamSnapshot> WaitForSnapshotAsync(Func<UpstreamSnapshot, bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                using (var scope = app.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
                    var snapshot = await db.UpstreamSnapshots.AsNoTracking().SingleOrDefaultAsync(s => s.BatchId == BatchId);
                    if (snapshot is not null && done(snapshot))
                    {
                        return snapshot;
                    }

                    if (DateTime.UtcNow > deadline)
                    {
                        Assert.Fail($"Snapshot for {BatchId} did not get there in 30s; last seen: "
                            + (snapshot is null ? "none" : $"{snapshot.ResolutionStatus}, {snapshot.AttemptCount} attempt(s), {snapshot.FailureReason}"));
                    }
                }

                await Task.Delay(100);
            }
        }

        public async Task<JsonDocument> GetAsync()
        {
            var client = app.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", TestTokens.Create(role: WonrichRoles.QualityControlOfficer));

            var response = await client.GetAsync($"/api/batches/{BatchId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        public BatchView Intake() => new(
            Reference, Dispatch, _day, Morning.AddHours(5.5), 9.80m, "intake.officer", Morning.AddMinutes(-30));

        public BatchTraceView Trace(params TracedTankView[] tanks) => new(
            Reference, _day, Morning.AddMinutes(-30), Morning.AddHours(5), "intake.officer", Morning.AddMinutes(-30),
            Dispatch, "BWR-02", "N. Silva", Morning.AddHours(3), "mcc.manager", Morning.AddHours(-2.5), 910m,
            tanks, [], []);

        public TracedConsignmentView Consignment(string reference, string society, decimal litres) => new(
            $"{Suffix}-{reference}", society, $"Society {society}", ["C1"], litres, litres * 1.03m, Morning.AddHours(1),
            "gate.officer", Morning.AddHours(-4.5), Morning.AddHours(-4), "mcc.operator", null, MarginToThreshold.Unknown,
            ["No gate quality test is recorded for this consignment."]);

        public async ValueTask DisposeAsync()
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

            var eventIds = await db.TimelineEvents.Where(e => e.BatchId == BatchId).Select(e => e.SourceEventId).ToListAsync();
            await db.ProcessedMessages.Where(m => eventIds.Contains(m.SourceEventId)).ExecuteDeleteAsync();

            // Cascades to the timeline and the snapshot.
            await db.BatchRecords.Where(b => b.BatchId == BatchId).ExecuteDeleteAsync();
            await db.DailyQualityAggregates.Where(a => a.Facility == Facility && a.Date == _day).ExecuteDeleteAsync();
            await db.DailyReasonCodeAggregates.Where(a => a.Facility == Facility && a.Date == _day).ExecuteDeleteAsync();
        }
    }
}
