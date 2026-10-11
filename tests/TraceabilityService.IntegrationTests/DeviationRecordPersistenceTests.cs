using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// Readings are stored as exact decimals, so a fat of 3.85% is 3.85 in the database,
/// not the nearest binary fraction.
/// </summary>
[Collection(ApiCollection.Name)]
public class DeviationRecordPersistenceTests(TraceabilityApiFactory factory)
{
    [Theory]
    [InlineData("3.85")]
    [InlineData("8.64")]
    public async Task Observed_value_and_limit_are_stored_and_read_back_exactly(string text)
    {
        var value = decimal.Parse(text, CultureInfo.InvariantCulture);
        var batchId = $"TEST-DECIMAL-{text}";

        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
                await SaveDeviationAsync(db, batchId, value);
            }

            // A new scope, so the values come from MySQL rather than EF's change tracker.
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

                var stored = await db.DeviationRecords.AsNoTracking().SingleAsync(d => d.BatchId == batchId);
                Assert.Equal(value, stored.ObservedValue);
                Assert.Equal(value, stored.Limit);

                // As MySQL holds them, before any conversion to .NET types.
                var raw = await db.Database
                    .SqlQuery<string>($"SELECT CONCAT(ObservedValue, '|', `Limit`) AS Value FROM deviation_records WHERE BatchId = {batchId}")
                    .SingleAsync();
                Assert.Equal($"{text}|{text}", raw);
            }
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
            // Cascades to the timeline event and the deviation.
            await db.BatchRecords.Where(b => b.BatchId == batchId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Observed_value_and_limit_columns_are_decimal_5_2()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

        // A double column would also read 3.85 back as "3.85", so check the type itself.
        var types = await db.Database
            .SqlQuery<string>($"""
                SELECT CONCAT(COLUMN_NAME, ' ', COLUMN_TYPE) AS Value
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'deviation_records'
                  AND COLUMN_NAME IN ('ObservedValue', 'Limit')
                ORDER BY COLUMN_NAME
                """)
            .ToListAsync();

        Assert.Equal(["Limit decimal(5,2)", "ObservedValue decimal(5,2)"], types);
    }

    private static async Task SaveDeviationAsync(TraceabilityDbContext db, string batchId, decimal value)
    {
        var at = new DateTime(2026, 9, 19, 5, 40, 0, DateTimeKind.Utc);

        db.BatchRecords.Add(new BatchRecord
        {
            BatchId = batchId,
            ProductLine = "FM",
            Facility = "FACTORY-01",
            Status = BatchStatus.Processing,
            Completeness = TraceCompleteness.Partial,
            FirstEventAt = at,
            LastEventAt = at
        });

        var mcc = new TimelineEvent
        {
            BatchId = batchId,
            Checkpoint = Checkpoint.Mcc,
            EventType = "MccDispatchCreated",
            OccurredAt = at,
            IsDeviation = true,
            Payload = "{}",
            SourceEventId = Guid.NewGuid(),
            ContractVersion = "v1"
        };
        db.TimelineEvents.Add(mcc);
        await db.SaveChangesAsync();

        db.DeviationRecords.Add(new DeviationRecord
        {
            BatchId = batchId,
            TimelineEventId = mcc.Id,
            Checkpoint = Checkpoint.Mcc,
            OccurredAt = at,
            ReasonCode = "FatLow",
            Parameter = "FatPercentage",
            ObservedValue = value,
            Limit = value
        });
        await db.SaveChangesAsync();
    }
}
