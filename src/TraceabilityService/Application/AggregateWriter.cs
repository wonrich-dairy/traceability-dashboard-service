using Microsoft.EntityFrameworkCore;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// How one event moves a batch's row in each aggregate table. DeviationCount on
/// daily_quality_aggregates moves by the sum of the checkpoint counts.
/// </summary>
public sealed record AggregateDelta
{
    public int Batches { get; init; }

    public IReadOnlyDictionary<StatusBucket, int> StatusCounts { get; init; } = new Dictionary<StatusBucket, int>();

    public int ProcessingDeviations { get; init; }

    public int LabDeviations { get; init; }

    public IReadOnlyDictionary<string, int> ReasonCodes { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    public int Deviations => ProcessingDeviations + LabDeviations;

    // -1 on the old status's count and +1 on the new one's, or nothing when both are in the
    // same count: Chilling, Processing and AwaitingLab are all Pending.
    public static IReadOnlyDictionary<StatusBucket, int> StatusMove(BatchStatus from, BatchStatus to)
    {
        var (fromBucket, toBucket) = (DeterminationResolver.BucketOf(from), DeterminationResolver.BucketOf(to));
        return fromBucket == toBucket
            ? new Dictionary<StatusBucket, int>()
            : new Dictionary<StatusBucket, int> { [fromBucket] = -1, [toBucket] = 1 };
    }
}

/// <summary>
/// Applies an <see cref="AggregateDelta"/> to both aggregate tables, inside the caller's
/// transaction. INSERT ... ON DUPLICATE KEY UPDATE adds to the row atomically, so consumers on
/// other partitions updating the same day cannot lose each other's changes, and a missing row
/// is created.
/// </summary>
public sealed class AggregateWriter(TraceabilityDbContext db)
{
    public async Task ApplyAsync(
        BatchRecord batch, DateOnly day, AggregateDelta delta, DateTime now, CancellationToken cancellationToken)
    {
        if (delta.Batches != 0 || delta.StatusCounts.Values.Any(c => c != 0) || delta.Deviations != 0)
        {
            var batches = delta.Batches;
            var pending = delta.StatusCounts.GetValueOrDefault(StatusBucket.Pending);
            var cleared = delta.StatusCounts.GetValueOrDefault(StatusBucket.Cleared);
            var failed = delta.StatusCounts.GetValueOrDefault(StatusBucket.Failed);
            var onHold = delta.StatusCounts.GetValueOrDefault(StatusBucket.OnHold);
            var deviations = delta.Deviations;
            var processing = delta.ProcessingDeviations;
            var lab = delta.LabDeviations;

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO daily_quality_aggregates
                    (Date, ProductLine, Facility, BatchCount, PendingCount, ClearedCount, FailedCount, OnHoldCount,
                     DeviationCount, MccDeviationCount, IntakeDeviationCount, ProcessingDeviationCount, LabDeviationCount, UpdatedAt)
                VALUES ({day}, {batch.ProductLine}, {batch.Facility}, {batches}, {pending}, {cleared}, {failed}, {onHold},
                        {deviations}, 0, 0, {processing}, {lab}, {now})
                ON DUPLICATE KEY UPDATE
                    BatchCount = BatchCount + {batches},
                    PendingCount = PendingCount + {pending},
                    ClearedCount = ClearedCount + {cleared},
                    FailedCount = FailedCount + {failed},
                    OnHoldCount = OnHoldCount + {onHold},
                    DeviationCount = DeviationCount + {deviations},
                    ProcessingDeviationCount = ProcessingDeviationCount + {processing},
                    LabDeviationCount = LabDeviationCount + {lab},
                    UpdatedAt = {now}
                """, cancellationToken);
        }

        foreach (var (code, change) in delta.ReasonCodes.Where(r => r.Value != 0))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO daily_reason_code_aggregates (Date, ProductLine, Facility, ReasonCode, DeviationCount, UpdatedAt)
                VALUES ({day}, {batch.ProductLine}, {batch.Facility}, {code}, {change}, {now})
                ON DUPLICATE KEY UPDATE
                    DeviationCount = DeviationCount + {change},
                    UpdatedAt = {now}
                """, cancellationToken);

            // The dashboard lists every row it sums, so a reason that no longer applies is removed.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM daily_reason_code_aggregates
                WHERE Facility = {batch.Facility} AND Date = {day} AND ProductLine = {batch.ProductLine}
                  AND ReasonCode = {code} AND DeviationCount = 0
                """, cancellationToken);
        }
    }
}
