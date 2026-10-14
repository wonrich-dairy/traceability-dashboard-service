using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Infrastructure.MccIntake;

namespace TraceabilityService.UnitTests;

/// <summary>
/// The upstream resolver (SCRUM-132) through the real MCC and Intake client, with the service
/// stood in for by a stub HttpMessageHandler, time by a fake TimeProvider (nothing sleeps), and
/// MySQL by EF Core's in-memory database.
/// </summary>
public sealed class UpstreamResolverTests
{
    private const int MaxAttempts = 4;
    private const string BatchId = "262-FM-B";
    private const string Dispatch = "DN-20260919-02";
    private const string Reference = "WR-20260919-02";
    private const string LookupPath = $"/api/factory/batches?dispatchNote={Dispatch}";
    private const string TracePath = $"/api/factory/batches/{Reference}/trace";

    private static readonly DateTimeOffset Start = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Screened = new(2026, 9, 19, 8, 5, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FakeTime _time = new(Start);
    private readonly StubMcc _mcc = new();
    private readonly DbContextOptions<TraceabilityDbContext> _database = new DbContextOptionsBuilder<TraceabilityDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options;

    public UpstreamResolverTests()
    {
        // As the consumer leaves it: PendingRetry, due at once.
        using var db = new TraceabilityDbContext(_database);
        db.BatchRecords.Add(new BatchRecord
        {
            BatchId = BatchId,
            DispatchRef = Dispatch,
            ProductLine = "FM",
            Facility = "FACTORY-01",
            Status = BatchStatus.Processing,
            FirstEventAt = Start.UtcDateTime,
            LastEventAt = Start.UtcDateTime,
            UpstreamSnapshot = new UpstreamSnapshot
            {
                BatchId = BatchId,
                ResolutionStatus = ResolutionStatus.PendingRetry,
                NextAttemptAt = Start.UtcDateTime,
            },
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_transient_500_is_retried_with_backoff_until_the_cap_then_given_up_as_unavailable()
    {
        _mcc.Default = HttpStatusCode.InternalServerError;

        Assert.Equal(ResolutionAttempt.PendingRetry, await ResolveAsync());

        var snapshot = await SnapshotAsync();
        Assert.Equal((ResolutionStatus.PendingRetry, 1), (snapshot.ResolutionStatus, snapshot.AttemptCount));
        Assert.Equal(Start.UtcDateTime.AddSeconds(30), snapshot.NextAttemptAt);
        Assert.Contains("500", snapshot.FailureReason);

        // Not due a second before then: nothing is sent.
        _time.Now = Start.AddSeconds(29);
        Assert.Equal(ResolutionAttempt.Skipped, await ResolveAsync());
        Assert.Single(_mcc.Requests);

        // Attempts 2 and 3 wait twice as long each time.
        foreach (var (attempt, wait) in new[] { (2, 60), (3, 120) })
        {
            _time.Now = snapshot.NextAttemptAt!.Value;
            Assert.Equal(ResolutionAttempt.PendingRetry, await ResolveAsync());

            snapshot = await SnapshotAsync();
            Assert.Equal((ResolutionStatus.PendingRetry, attempt), (snapshot.ResolutionStatus, snapshot.AttemptCount));
            Assert.Equal(_time.Now.UtcDateTime.AddSeconds(wait), snapshot.NextAttemptAt);
        }

        // The fourth is the last allowed.
        _time.Now = snapshot.NextAttemptAt!.Value;
        Assert.Equal(ResolutionAttempt.Unavailable, await ResolveAsync());

        snapshot = await SnapshotAsync();
        Assert.Equal((ResolutionStatus.Unavailable, MaxAttempts), (snapshot.ResolutionStatus, snapshot.AttemptCount));
        Assert.Null(snapshot.NextAttemptAt);
        Assert.StartsWith("Retries ran out after 4 attempts. Last: MCC and Intake Service answered 500", snapshot.FailureReason);

        // Given up on: never asked again.
        _time.Now = Start.AddDays(1);
        Assert.Equal(ResolutionAttempt.Skipped, await ResolveAsync());
        Assert.Equal(MaxAttempts, _mcc.Requests.Count);
    }

    [Fact]
    public async Task A_404_is_unavailable_on_the_first_attempt_with_no_retry_scheduled()
    {
        _mcc.Map(LookupPath, HttpStatusCode.OK, new[] { Intake() });
        _mcc.Map(TracePath, HttpStatusCode.NotFound, new
        {
            title = "Batch not found",
            status = 404,
            detail = $"No batch carries the reference '{Reference}'.",
            code = "entity_not_found",
        });

        Assert.Equal(ResolutionAttempt.Unavailable, await ResolveAsync());

        var snapshot = await SnapshotAsync();
        Assert.Equal((ResolutionStatus.Unavailable, 1), (snapshot.ResolutionStatus, snapshot.AttemptCount));
        Assert.Null(snapshot.NextAttemptAt);
        Assert.Null(snapshot.Payload);
        Assert.Contains(Reference, snapshot.FailureReason);

        _time.Now = Start.AddDays(1);
        Assert.Equal(ResolutionAttempt.Skipped, await ResolveAsync());
        Assert.Equal([LookupPath, TracePath], _mcc.Requests);
    }

    [Fact]
    public async Task Tanks_and_consignments_are_stored_in_exactly_the_order_the_service_sent_them()
    {
        // Deliberately in no order this service could arrive at itself: not by tank code,
        // reference, society, registration time or margin.
        var sent = new (string Tank, (string Reference, string Society, int Minute, decimal Margin)[] Consignments)[]
        {
            ("T2", [("CON-C", "VS045", 20, 0.3000m), ("CON-A", "VS009", 0, 0.0020m), ("CON-E", "VS101", 40, -1m)]),
            ("T1", [("CON-D", "VS014", -10, 0.1000m), ("CON-B", "VS022", 30, 0.0500m)]),
        };
        _mcc.Map(LookupPath, HttpStatusCode.OK, new[] { Intake() });
        _mcc.Map(TracePath, HttpStatusCode.OK, Trace([.. sent.Select(t => new TracedTankView(
            t.Tank, $"Tank {t.Tank}", 100m,
            [.. t.Consignments.Select(c => Consignment(c.Reference, c.Society, Screened.AddHours(-3).AddMinutes(c.Minute), c.Margin))],
            []))]));

        Assert.Equal(ResolutionAttempt.Resolved, await ResolveAsync());

        var stored = (await SnapshotAsync()).Payload!;
        using var payload = JsonDocument.Parse(stored);
        var storedOrder = payload.RootElement.GetProperty("tanks").EnumerateArray()
            .Select(t => (t.GetProperty("code").GetString(), string.Join(",", t.GetProperty("consignments").EnumerateArray()
                .Select(c => c.GetProperty("reference").GetString()))))
            .ToList();
        Assert.Equal([("T2", "CON-C,CON-A,CON-E"), ("T1", "CON-D,CON-B")], storedOrder);

        // And in the stored text itself, byte for byte in that sequence.
        var positions = new[] { "\"T2\"", "\"CON-C\"", "\"CON-A\"", "\"CON-E\"", "\"T1\"", "\"CON-D\"", "\"CON-B\"" }
            .Select(token => stored.IndexOf(token, StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
    }

    private async Task<ResolutionAttempt> ResolveAsync()
    {
        await using var db = new TraceabilityDbContext(_database);
        using var http = new HttpClient(_mcc, disposeHandler: false) { BaseAddress = new Uri("http://mcc.test/") };
        var resolver = new UpstreamResolver(
            db,
            new MccIntakeClient(http),
            Options.Create(new UpstreamResolutionOptions { MaxAttempts = MaxAttempts }),
            Options.Create(new MccIntakeOptions()),
            _time,
            NullLogger<UpstreamResolver>.Instance);
        return await resolver.ResolveAsync(BatchId, "corr-unit", CancellationToken.None);
    }

    private async Task<UpstreamSnapshot> SnapshotAsync()
    {
        await using var db = new TraceabilityDbContext(_database);
        return await db.UpstreamSnapshots.AsNoTracking().SingleAsync(s => s.BatchId == BatchId);
    }

    private static BatchView Intake() => new(
        Reference, Dispatch, new DateOnly(2026, 9, 19), new DateTime(2026, 9, 19, 13, 35, 0), 9.80m, "intake.officer", Screened);

    private static BatchTraceView Trace(IReadOnlyList<TracedTankView> tanks) => new(
        Reference, new DateOnly(2026, 9, 19), Screened, new DateTime(2026, 9, 19, 13, 35, 0), "intake.officer", Screened,
        Dispatch, "BWR-02", "N. Silva", new DateTime(2026, 9, 19, 11, 10, 0), "mcc.manager", Screened.AddHours(-2), 910m,
        tanks, [], []);

    private static TracedConsignmentView Consignment(string reference, string society, DateTime registered, decimal margin) => new(
        reference, society, $"Society {society}", ["C1"], 100m, 103m, registered.AddHours(5.5), "gate.officer", registered,
        registered.AddMinutes(15), "mcc.operator",
        new TracedQualityTestView(4.0m, 27.5m, 4.0m, 0m, "Normal", 27.8m, 8.6m, 12.6m, "Good", "72%", "Accepted", "gate.tester", registered,
            [new MeasureMargin("Snf", "8.6", "8.5", margin)]),
        margin, []);

    /// <summary>Answers mapped paths with their body and every other request with <see cref="Default"/>.</summary>
    private sealed class StubMcc : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = [];

        public HttpStatusCode Default { get; set; } = HttpStatusCode.InternalServerError;

        public List<string> Requests { get; } = [];

        public void Map(string pathAndQuery, HttpStatusCode status, object body) =>
            _routes[pathAndQuery] = (status, JsonSerializer.Serialize(body, Json));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(path);
            return Task.FromResult(_routes.TryGetValue(path, out var route)
                ? new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(Default));
        }
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
