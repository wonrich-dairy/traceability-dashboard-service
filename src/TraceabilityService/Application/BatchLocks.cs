using Microsoft.EntityFrameworkCore;
using TraceabilityService.Api.Infrastructure.Persistence;

namespace TraceabilityService.Application;

/// <summary>
/// Row locks that make events for one batch take turns. The stage, hold and lab consumers run
/// side by side, so two events for a batch can be handled at once; each works out the status
/// from what is stored, and without the lock neither would see the other's event, so one
/// status would overwrite the other and the aggregates would move twice. Taken first in the
/// event's transaction (READ COMMITTED, <c>EfEventUnitOfWork</c>), so everything read after it
/// includes what the other consumer committed. Released when the transaction ends.
/// </summary>
public static class BatchLocks
{
    public static Task LockBatchAsync(this TraceabilityDbContext db, string batchId, CancellationToken cancellationToken) =>
        db.Database.IsRelational()
            ? db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM batch_records WHERE BatchId = {batchId} FOR UPDATE", cancellationToken)
            : Task.CompletedTask;

    public static Task LockDispatchAsync(this TraceabilityDbContext db, string dispatchNumber, CancellationToken cancellationToken) =>
        db.Database.IsRelational()
            ? db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM batch_records WHERE DispatchRef = {dispatchNumber} FOR UPDATE", cancellationToken)
            : Task.CompletedTask;
}
