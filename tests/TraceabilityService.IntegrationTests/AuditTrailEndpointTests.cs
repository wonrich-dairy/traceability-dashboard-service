using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using SRC.Authorization;
using Xunit.Abstractions;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Migrations;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// GET /api/audit and GET /api/audit/coverage (SCRUM-138), the auditing of opened batches, and
/// audit_entries being append-only in MySQL. audit_entries rows cannot be removed, so each test
/// seeds its own batches and users under a suffix of its own (<see cref="_run"/>), and only ever
/// asks about those. Batch 1 (A1) has a resolved snapshot and three consumed events:
///   18 Sept 23:00  ConsignmentRegistered x2  mcc.officer   MCC      (19 Sept 04:30 in Colombo)
///   19 Sept 02:00  MilkDispatched            mcc.manager   MCC
///   19 Sept 04:00  FactoryIntakeScreened     intake        Intake
///   19 Sept 06:00  ProcessingStageRecorded   tech          Processing
///   20 Sept 06:00  ProcessingHoldRaised      tech          Processing
///   21 Sept 03:00  BatchFailed               analyst       Lab
/// Batch 2 (A2) has a snapshot still being looked up and one event: 22 Sept 06:00 ProcessingCompleted by tech.
/// </summary>
[Collection(ApiCollection.Name)]
public class AuditTrailEndpointTests(TraceabilityApiFactory factory, ITestOutputHelper output) : IAsyncLifetime
{
    private const string Facility = "TEST-AUDIT";
    private static readonly DateTime Day19 = new(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private Guid _stageEventId;

    private string Batch1 => $"TA-{_run}-1";
    private string Batch2 => $"TA-{_run}-2";

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

        db.BatchRecords.AddRange(Batch(Batch1, Day19), Batch(Batch2, Day19.AddDays(3)));
        db.UpstreamSnapshots.AddRange(
            new UpstreamSnapshot
            {
                BatchId = Batch1,
                ResolutionStatus = ResolutionStatus.Resolved,
                ResolvedAt = Day19.AddDays(1),
                Payload = UpstreamSnapshotPayload.Serialize(new UpstreamSnapshotPayload
                {
                    BatchReference = $"WR-{_run}",
                    Dispatch = new() { Reference = $"DN-{_run}", DispatchedAt = Day19.AddHours(2), DispatchedBy = $"mcc.manager-{_run}", TotalLitres = 900m },
                    Intake = new() { ArrivedAt = Day19.AddHours(3), ScreenedAt = Day19.AddHours(4), ScreenedBy = $"intake-{_run}" },
                    Tanks =
                    [
                        new() { Code = "T1", QuantityDrawnLitres = 900m, Consignments = [Consignment("C-1"), Consignment("C-2")] },
                    ],
                }),
            },
            new UpstreamSnapshot { BatchId = Batch2, ResolutionStatus = ResolutionStatus.PendingRetry });

        var stage = Event(Batch1, Checkpoint.Processing, "ProcessingStageRecorded", Day19.AddHours(6), $"tech-{_run}");
        _stageEventId = stage.SourceEventId;
        db.TimelineEvents.AddRange(
            stage,
            Event(Batch1, Checkpoint.Processing, "ProcessingHoldRaised", Day19.AddDays(1).AddHours(6), $"tech-{_run}"),
            Event(Batch1, Checkpoint.Lab, "BatchFailed", Day19.AddDays(2).AddHours(3), $"analyst-{_run}"),
            Event(Batch2, Checkpoint.Processing, "ProcessingCompleted", Day19.AddDays(3).AddHours(6), $"tech-{_run}"));
        await db.SaveChangesAsync();

        UpstreamSnapshotPayload.ConsignmentPart Consignment(string reference) => new()
        {
            Reference = $"{reference}-{_run}",
            Society = new() { Code = "VS001" },
            VolumeLitres = 450m,
            RegisteredAt = Day19.AddHours(-1),
            RegisteredBy = $"mcc.officer-{_run}",
        };
    }

    // The batches go, and with them their events and snapshots; what was audited about them stays.
    public async Task DisposeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        await db.BatchRecords.Where(b => b.BatchId.StartsWith($"TA-{_run}")).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task A_batchs_entries_come_from_its_events_and_its_snapshot_newest_first()
    {
        var page = await AuditAsync($"batchId={Batch1}");
        var items = Items(page);

        Assert.Equal(
            ["BatchFailed", "ProcessingHoldRaised", "ProcessingStageRecorded", "FactoryIntakeScreened", "MilkDispatched",
             "ConsignmentRegistered", "ConsignmentRegistered"],
            items.Select(i => i.GetProperty("action").GetString()));
        Assert.Equal(7, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("message").ValueKind);

        Assert.Equal(
            [$"analyst-{_run}", $"tech-{_run}", $"tech-{_run}", $"intake-{_run}", $"mcc.manager-{_run}", $"mcc.officer-{_run}", $"mcc.officer-{_run}"],
            items.Select(i => i.GetProperty("user").GetString()));
        Assert.Equal(
            ["Lab", "Processing", "Processing", "Intake", "Mcc", "Mcc", "Mcc"],
            items.Select(i => i.GetProperty("checkpoint").GetString()));
        Assert.Equal(
            ["ConsumedEvent", "ConsumedEvent", "ConsumedEvent", "UpstreamSnapshot", "UpstreamSnapshot", "UpstreamSnapshot", "UpstreamSnapshot"],
            items.Select(i => i.GetProperty("source").GetString()));
        Assert.All(items, i =>
        {
            Assert.Equal(Batch1, i.GetProperty("batchId").GetString());
            Assert.Equal(Facility, i.GetProperty("facility").GetString());
        });

        // The snapshot's times are read back exactly, in UTC.
        Assert.Equal(
            [Day19.AddDays(2).AddHours(3), Day19.AddDays(1).AddHours(6), Day19.AddHours(6), Day19.AddHours(4), Day19.AddHours(2), Day19.AddHours(-1), Day19.AddHours(-1)],
            items.Select(i => i.GetProperty("occurredAt").GetDateTime().ToUniversalTime()));
        Assert.EndsWith("Z", items[0].GetProperty("occurredAt").GetString());

        Assert.Equal(_stageEventId.ToString(), items[2].GetProperty("reference").GetString());
        Assert.Equal($"DN-{_run}", items[3].GetProperty("reference").GetString());
        Assert.Equal($"DN-{_run}", items[4].GetProperty("reference").GetString());
        Assert.Equal([$"C-1-{_run}", $"C-2-{_run}"], items.Skip(5).Select(i => i.GetProperty("reference").GetString()));
        Assert.Equal(7, items.Select(i => i.GetProperty("id").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task A_snapshot_still_being_looked_up_gives_no_entries_yet()
    {
        var items = Items(await AuditAsync($"batchId={Batch2}"));

        Assert.Equal(["ProcessingCompleted"], items.Select(i => i.GetProperty("action").GetString()));
    }

    // Each filter on its own, then together. {run} is the test's own suffix.
    [Theory]
    [InlineData("user=tech-{run}", "ProcessingCompleted,ProcessingHoldRaised,ProcessingStageRecorded")]
    [InlineData("user=TECH-{run}", "ProcessingCompleted,ProcessingHoldRaised,ProcessingStageRecorded")]
    [InlineData("user=mcc.officer-{run}", "ConsignmentRegistered,ConsignmentRegistered")]
    [InlineData("user=Intake-{run}", "FactoryIntakeScreened")]
    [InlineData("batchId={batch1}&action=MilkDispatched", "MilkDispatched")]
    [InlineData("batchId={batch1}&action=processingholdraised", "ProcessingHoldRaised")]
    [InlineData("batchId={batch1}&from=2026-09-20", "BatchFailed,ProcessingHoldRaised")]
    [InlineData("batchId={batch1}&to=2026-09-19", "ProcessingStageRecorded,FactoryIntakeScreened,MilkDispatched,ConsignmentRegistered,ConsignmentRegistered")]
    [InlineData("batchId={batch1}&from=2026-09-20&to=2026-09-20", "ProcessingHoldRaised")]
    [InlineData("user=tech-{run}&batchId={batch2}", "ProcessingCompleted")]
    [InlineData("user=tech-{run}&from=2026-09-20&to=2026-09-21", "ProcessingHoldRaised")]
    [InlineData("user=tech-{run}&action=ProcessingStageRecorded&batchId={batch1}&from=2026-09-19&to=2026-09-19", "ProcessingStageRecorded")]
    public async Task Each_filter_narrows_the_trail_and_filters_combine(string query, string expected)
    {
        var page = await AuditAsync(query.Replace("{run}", _run).Replace("{batch1}", Batch1).Replace("{batch2}", Batch2));

        Assert.Equal(expected.Split(','), Items(page).Select(i => i.GetProperty("action").GetString()));
        Assert.Equal(expected.Split(',').Length, page.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_is_an_empty_page_with_a_message()
    {
        var page = await AuditAsync($"user=nobody-{_run}");

        Assert.Empty(Items(page));
        Assert.Equal(0, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, page.GetProperty("totalPages").GetInt32());
        Assert.Equal(AuditTrailService.NoMatchMessage, page.GetProperty("message").GetString());
        Assert.Equal($"nobody-{_run}", page.GetProperty("filters").GetProperty("user").GetString());
    }

    [Fact]
    public async Task Entries_are_paged_fifty_at_a_time_without_overlap()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
            // 53 more on batch 2: 54 in all, some at the same moment, so ties must be broken stably.
            for (var n = 0; n < 53; n++)
            {
                db.TimelineEvents.Add(Event(Batch2, Checkpoint.Processing, "ProcessingStageRecorded", Day19.AddDays(4).AddMinutes(n / 2), $"tech-{_run}"));
            }
            await db.SaveChangesAsync();
        }

        var first = await AuditAsync($"batchId={Batch2}");
        var second = await AuditAsync($"batchId={Batch2}&page=2");

        Assert.Equal(54, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, first.GetProperty("totalPages").GetInt32());
        Assert.Equal(50, Items(first).Count);
        Assert.Equal(4, Items(second).Count);
        Assert.Equal(54, Items(first).Concat(Items(second)).Select(i => i.GetProperty("id").GetString()).Distinct().Count());
        Assert.Equal("ProcessingCompleted", Items(second)[^1].GetProperty("action").GetString());
    }

    [Fact]
    public async Task Opening_a_batch_record_or_its_trace_is_audited_and_shows_in_the_trail()
    {
        var client = Client(WonrichRoles.QualityControlOfficer);
        client.DefaultRequestHeaders.Add("X-Correlation-ID", $"test-audit-open-{_run}");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/batches/{Batch1}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/batches/{Batch1}/trace")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
            var entries = await db.AuditEntries.Where(a => a.CorrelationId == $"test-audit-open-{_run}").OrderBy(a => a.Id).ToListAsync();

            Assert.Equal([AuditActions.BatchRecordOpened, AuditActions.BatchTraceOpened], entries.Select(e => e.Action));
            Assert.All(entries, e =>
            {
                Assert.Equal(TestTokens.UserId, e.Actor);
                Assert.Equal(AuditTrailService.BatchEntityType, e.EntityType);
                Assert.Equal(Batch1, e.EntityId);
                Assert.Contains(WonrichRoles.QualityControlOfficer, e.Details);
                Assert.True(DateTime.UtcNow - e.OccurredAt < TimeSpan.FromMinutes(5));
            });
        }

        var opened = Items(await AuditAsync($"batchId={Batch1}&user={TestTokens.UserId}"));
        Assert.Equal([AuditActions.BatchTraceOpened, AuditActions.BatchRecordOpened], opened.Select(i => i.GetProperty("action").GetString()));
        Assert.All(opened, i =>
        {
            Assert.Equal("Traceability", i.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, i.GetProperty("checkpoint").ValueKind);
            Assert.Equal(Facility, i.GetProperty("facility").GetString());
            Assert.Equal(WonrichRoles.QualityControlOfficer, i.GetProperty("details").GetProperty("role").GetString());
        });
    }

    [Fact]
    public async Task A_batch_not_found_or_refused_is_not_audited()
    {
        var qco = Client(WonrichRoles.QualityControlOfficer);
        qco.DefaultRequestHeaders.Add("X-Correlation-ID", $"test-audit-missing-{_run}");
        var analyst = Client(WonrichRoles.QualityAnalyst);
        analyst.DefaultRequestHeaders.Add("X-Correlation-ID", $"test-audit-refused-{_run}");

        Assert.Equal(HttpStatusCode.NotFound, (await qco.GetAsync($"/api/batches/TA-{_run}-NONE/trace")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await qco.GetAsync($"/api/batches/TA-{_run}-NONE")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync($"/api/batches/{Batch1}/trace")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        Assert.False(await db.AuditEntries.AnyAsync(a => a.CorrelationId!.EndsWith(_run)));
    }

    [Fact]
    public async Task An_export_shows_in_the_trail_with_no_batch_and_what_was_exported()
    {
        var export = await Client(WonrichRoles.SystemAdministrator).GetAsync($"/api/batches/export?user=tech-{_run}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var fileName = export.Content.Headers.ContentDisposition?.FileName?.Trim('"');

        var entry = Items(await AuditAsync($"user={TestTokens.UserId}&action={AuditActions.BatchSearchExported}"))
            .Single(i => i.GetProperty("reference").GetString() == fileName);

        Assert.Equal(JsonValueKind.Null, entry.GetProperty("batchId").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("checkpoint").ValueKind);
        Assert.Equal(2, entry.GetProperty("details").GetProperty("rowCount").GetInt32());
        Assert.Equal(WonrichRoles.SystemAdministrator, entry.GetProperty("details").GetProperty("role").GetString());
    }

    // Append-only at the database: whatever gets past the application is refused by the triggers.
    [Fact]
    public async Task The_database_refuses_to_change_or_delete_an_audit_entry()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        var entry = new AuditEntry
        {
            OccurredAt = Day19, Actor = $"qco-{_run}", Action = AuditActions.BatchTraceOpened,
            EntityType = AuditTrailService.BatchEntityType, EntityId = Batch1,
        };
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync();
        var id = entry.Id;

        var update = await Assert.ThrowsAsync<MySqlException>(() =>
            db.AuditEntries.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Actor, "forged")));
        var delete = await Assert.ThrowsAsync<MySqlException>(() =>
            db.AuditEntries.Where(a => a.Id == id).ExecuteDeleteAsync());
        var raw = await Assert.ThrowsAsync<MySqlException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE audit_entries SET Action = 'Forged' WHERE Id = {id}"));

        Assert.All([update, delete, raw], e =>
        {
            Assert.Equal("45000", e.SqlState);
            Assert.Contains("append-only", e.Message);
        });
        Assert.Contains("changed", update.Message);
        Assert.Contains("deleted", delete.Message);

        // And through the change tracker, the context refuses before MySQL is asked.
        entry.Actor = "forged";
        await Assert.ThrowsAsync<AuditEntryImmutableException>(() => db.SaveChangesAsync());

        using var freshScope = factory.Services.CreateScope();
        var fresh = freshScope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        var stored = await fresh.AuditEntries.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal($"qco-{_run}", stored.Actor);
        Assert.Equal(AuditActions.BatchTraceOpened, stored.Action);
    }

    // The pipeline applies an idempotent script to production, on every deploy. MySQL cannot create a
    // trigger inside the stored procedure Pomelo guards statements with, so this runs the script the
    // way the pipeline does: on an empty database, then again on the same one.
    [Fact]
    public async Task The_idempotent_migration_script_runs_on_an_empty_database_and_again()
    {
        string script;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
            script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        }

        var database = $"trc_script_{_run}";
        try
        {
            for (var run = 1; run <= 2; run++)
            {
                var result = await factory.RunScriptAsync(database, script);
                Assert.True(result.ExitCode == 0, $"Run {run}: {result.Stderr}");
            }

            var triggers = await factory.QueryAsync(database,
                "SELECT TRIGGER_NAME FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE() ORDER BY 1");
            Assert.Equal(
                [MakeAuditEntriesAppendOnly.NoDeleteTrigger, MakeAuditEntriesAppendOnly.NoUpdateTrigger],
                triggers.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));

            var migrations = await factory.QueryAsync(database, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY 1 DESC LIMIT 1");
            Assert.EndsWith("_MakeAuditEntriesAppendOnly", migrations.Stdout.Trim());

            var refused = await factory.QueryAsync(database,
                "INSERT INTO audit_entries (OccurredAt, Actor, Action, EntityType, EntityId) VALUES (NOW(6), 'u', 'A', 'Batch', 'B'); DELETE FROM audit_entries;");
            Assert.NotEqual(0, refused.ExitCode);
            Assert.Contains("append-only", refused.Stderr);
        }
        finally
        {
            await factory.QueryAsync("mysql", $"DROP DATABASE IF EXISTS {database}");
        }
    }

    [Fact]
    public async Task The_coverage_list_says_what_is_and_is_not_covered()
    {
        var response = await Client(WonrichRoles.QualityControlOfficer).GetAsync("/api/audit/coverage");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var covered = json.RootElement.GetProperty("covered").EnumerateArray().ToList();
        Assert.Contains(covered, c => c.GetProperty("action").GetString() == "ProcessingHoldRaised"
            && c.GetProperty("checkpoint").GetString() == "Processing"
            && c.GetProperty("source").GetString() == "ConsumedEvent");
        Assert.Contains(covered, c => c.GetProperty("action").GetString() == AuditActions.BatchTraceOpened
            && c.GetProperty("checkpoint").ValueKind == JsonValueKind.Null);
        Assert.NotEmpty(json.RootElement.GetProperty("notCovered").EnumerateArray());
        Assert.Equal("docs/audit-trail.md", json.RootElement.GetProperty("documentation").GetString());
    }

    [Theory]
    [InlineData("from=2026-09-21&to=2026-09-20", "from")]
    [InlineData("page=0", "page")]
    [InlineData("page=1000001", "page")]
    [InlineData("from=yesterday", "from")]
    public async Task Invalid_parameters_are_a_400_problem_naming_the_parameter(string query, string parameter)
    {
        var response = await Client(WonrichRoles.QualityControlOfficer).GetAsync($"/api/audit?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(parameter, out _), json.RootElement.GetRawText());
    }

    [Fact]
    public async Task A_user_filter_longer_than_any_user_id_is_a_400()
    {
        var response = await Client(WonrichRoles.QualityControlOfficer).GetAsync($"/api/audit?user={new string('x', 101)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/audit")]
    [InlineData("/api/audit/coverage")]
    public async Task Only_an_admin_or_a_qco_may_read_the_audit_trail(string path)
    {
        Assert.Equal(HttpStatusCode.OK, (await Client(WonrichRoles.SystemAdministrator).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(WonrichRoles.QualityControlOfficer).GetAsync(path)).StatusCode);
        foreach (var role in WonrichRoles.All.Except([WonrichRoles.SystemAdministrator, WonrichRoles.QualityControlOfficer]))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Client(role).GetAsync(path)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task There_is_no_endpoint_to_add_change_or_remove_an_entry(string method)
    {
        var client = Client(WonrichRoles.SystemAdministrator);

        foreach (var path in new[] { "/api/audit", "/api/audit/1", "/api/audit/audit-1" })
        {
            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound });
        }
    }

    // A thousand batches with snapshots: the whole trail and a filtered one, each well inside two seconds.
    [Fact]
    public async Task The_trail_answers_within_two_seconds_over_a_thousand_batches()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
            for (var n = 0; n < 1000; n++)
            {
                var batchId = $"TA-{_run}-L{n:0000}";
                var at = Day19.AddHours(n);
                db.BatchRecords.Add(Batch(batchId, at));
                db.UpstreamSnapshots.Add(new UpstreamSnapshot
                {
                    BatchId = batchId,
                    ResolutionStatus = ResolutionStatus.Resolved,
                    Payload = UpstreamSnapshotPayload.Serialize(new UpstreamSnapshotPayload
                    {
                        BatchReference = $"WR-{n}",
                        Dispatch = new() { Reference = $"DN-{n}", DispatchedAt = at, DispatchedBy = "mcc.manager-1", TotalLitres = 900m },
                        Intake = new() { ArrivedAt = at, ScreenedAt = at.AddHours(1), ScreenedBy = "intake-1" },
                        Tanks =
                        [
                            new()
                            {
                                Code = "T1", QuantityDrawnLitres = 900m,
                                Consignments = [.. Enumerable.Range(0, 3).Select(c => new UpstreamSnapshotPayload.ConsignmentPart
                                {
                                    Reference = $"C-{n}-{c}", Society = new() { Code = $"VS{c:000}" }, VolumeLitres = 300m,
                                    RegisteredAt = at.AddHours(-2), RegisteredBy = $"mcc.officer-{c}",
                                })],
                            },
                        ],
                    }),
                });
                db.TimelineEvents.Add(Event(batchId, Checkpoint.Processing, "ProcessingStageRecorded", at.AddHours(2), $"proc.tech-{n % 3}"));
            }
            await db.SaveChangesAsync();
        }

        var client = Client(WonrichRoles.QualityControlOfficer);
        string[] paths =
        [
            "/api/audit",
            "/api/audit?page=20",
            "/api/audit?user=mcc.officer-1",
            "/api/audit?action=MilkDispatched&from=2026-09-25&to=2026-10-05",
            $"/api/audit?batchId=TA-{_run}-L0500",
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

    private async Task<JsonElement> AuditAsync(string query)
    {
        var response = await Client(WonrichRoles.QualityControlOfficer).GetAsync($"/api/audit?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static List<JsonElement> Items(JsonElement page) => [.. page.GetProperty("items").EnumerateArray()];

    private HttpClient Client(string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(role: role));
        return client;
    }

    private static BatchRecord Batch(string batchId, DateTime firstEventAt) => new()
    {
        BatchId = batchId,
        ProductLine = "FM",
        Facility = Facility,
        Status = BatchStatus.Processing,
        Completeness = TraceCompleteness.Partial,
        FirstEventAt = firstEventAt,
        LastEventAt = firstEventAt,
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
}
