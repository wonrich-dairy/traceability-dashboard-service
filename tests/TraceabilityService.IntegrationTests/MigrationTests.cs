using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// Runs the migrations down as well as up. Each test migrates its own database on the
/// shared MySQL container, so rolling back never touches the one the API is using.
/// </summary>
[Collection(ApiCollection.Name)]
public class MigrationTests(TraceabilityApiFactory factory)
{
    // The last migration before the read model replaced the batch_traces schema.
    private const string BeforeReadModel = "20260920172550_AddBatchTraceProductionDateStatusIndex";

    private static readonly string[] OldTables = ["batch_sources", "batch_traces", "checkpoint_records"];

    private static readonly string[] ReadModelTables =
    [
        "audit_entries", "batch_records", "daily_quality_aggregates", "daily_reason_code_aggregates",
        "deviation_records", "processed_messages", "timeline_events", "upstream_snapshots"
    ];

    [Fact]
    public async Task Read_model_migration_rolls_back_with_data_present_and_reapplies()
    {
        await using var db = CreateContext("trc_down_read_model");
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync();

            // Rows in a child of a child, so Down has to drop the tables in foreign key order.
            await SeedBatchWithDeviationAsync(db);

            await migrator.MigrateAsync(BeforeReadModel);

            var tables = await TablesAsync(db);
            Assert.All(OldTables, t => Assert.Contains(t, tables));
            Assert.All(ReadModelTables, t => Assert.DoesNotContain(t, tables));
            Assert.Equal(BeforeReadModel, (await db.Database.GetAppliedMigrationsAsync()).Last());

            await migrator.MigrateAsync();

            tables = await TablesAsync(db);
            Assert.All(ReadModelTables, t => Assert.Contains(t, tables));
            Assert.All(OldTables, t => Assert.DoesNotContain(t, tables));
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Every_migration_rolls_back_to_an_empty_database_and_reapplies()
    {
        await using var db = CreateContext("trc_down_all");
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync();

            await migrator.MigrateAsync(Migration.InitialDatabase);

            // EF keeps its own history table; everything else must be gone.
            Assert.Equal(["__EFMigrationsHistory"], await TablesAsync(db));
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());

            await migrator.MigrateAsync();

            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    /// <summary>
    /// A context on its own database. Connects as root, because the app's user can
    /// only reach the database it was created for.
    /// </summary>
    private TraceabilityDbContext CreateContext(string database)
    {
        var appConnection = factory.Services.GetRequiredService<IConfiguration>()
            .GetConnectionString("TraceabilityDb")
            ?? throw new InvalidOperationException("The factory did not set ConnectionStrings:TraceabilityDb.");

        var connection = new MySqlConnectionStringBuilder(appConnection)
        {
            UserID = "root",
            Database = database
        };

        var options = new DbContextOptionsBuilder<TraceabilityDbContext>()
            .UseMySql(connection.ConnectionString, new MySqlServerVersion(new Version(8, 0, 46)))
            .Options;

        return new TraceabilityDbContext(options);
    }

    private static async Task SeedBatchWithDeviationAsync(TraceabilityDbContext db)
    {
        var at = new DateTime(2026, 9, 19, 5, 40, 0, DateTimeKind.Utc);

        db.BatchRecords.Add(new BatchRecord
        {
            BatchId = "262-FM-B",
            ProductLine = "FM",
            Facility = "FACTORY-01",
            Status = BatchStatus.Failed,
            Completeness = TraceCompleteness.Complete,
            FirstEventAt = at,
            LastEventAt = at
        });

        var mcc = new TimelineEvent
        {
            BatchId = "262-FM-B",
            Checkpoint = Checkpoint.Mcc,
            EventType = "MccDispatchCreated",
            OccurredAt = at,
            IsDeviation = true,
            Payload = """{"temperatureC": 12.40}""",
            SourceEventId = Guid.NewGuid(),
            ContractVersion = "v1"
        };
        db.TimelineEvents.Add(mcc);
        await db.SaveChangesAsync();

        db.DeviationRecords.Add(new DeviationRecord
        {
            BatchId = "262-FM-B",
            TimelineEventId = mcc.Id,
            Checkpoint = Checkpoint.Mcc,
            OccurredAt = at,
            ReasonCode = "TemperatureHigh",
            Parameter = "TemperatureC",
            ObservedValue = 12.40m,
            Limit = 4.00m
        });
        await db.SaveChangesAsync();
    }

    private static Task<List<string>> TablesAsync(TraceabilityDbContext db) =>
        db.Database
            .SqlQueryRaw<string>(
                "SELECT TABLE_NAME AS Value FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME")
            .ToListAsync();
}
