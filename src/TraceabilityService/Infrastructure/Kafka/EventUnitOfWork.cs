using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Infrastructure.Kafka;

/// <summary>
/// One event's transaction, opened by recording the event as processed. Disposing it
/// without <see cref="CommitAsync"/> rolls back everything written, the processed_messages
/// row included, so a retry starts clean.
/// </summary>
public interface IEventUnitOfWork
{
    /// <summary>False when this consumer group has already handled the event: skip it.</summary>
    Task<bool> BeginAsync(string consumerGroup, ConsumedEvent consumed, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IEventUnitOfWork"/> on the scope's DbContext, so the handler's writes and the
/// processed_messages row commit together. Inserting the row is the duplicate check: the
/// unique index on (ConsumerGroup, SourceEventId) rejects a second one, even if two
/// consumers race.
/// </summary>
public sealed class EfEventUnitOfWork(TraceabilityDbContext db, TimeProvider time) : IEventUnitOfWork, IAsyncDisposable
{
    private IDbContextTransaction? _transaction;

    public async Task<bool> BeginAsync(string consumerGroup, ConsumedEvent consumed, CancellationToken cancellationToken)
    {
        // READ COMMITTED: a handler that waits for a batch's row lock then reads what the other
        // consumer committed meanwhile (see BatchLocks), and no gap locks are taken.
        _transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        if (await db.ProcessedMessages.AnyAsync(m => m.ConsumerGroup == consumerGroup && m.SourceEventId == consumed.EventId, cancellationToken))
        {
            if (_transaction is not null)
            {
                await _transaction.RollbackAsync(cancellationToken);
            }
            return false;
        }

        db.ProcessedMessages.Add(new ProcessedMessage
        {
            ConsumerGroup = consumerGroup,
            SourceEventId = consumed.EventId,
            Topic = consumed.Topic,
            ProcessedAt = time.GetUtcNow().UtcDateTime,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            db.ChangeTracker.Clear();
            if (_transaction is not null)
            {
                await _transaction.RollbackAsync(cancellationToken);
            }
            return false;
        }
    }

    private static bool IsDuplicate(DbUpdateException ex) =>
        ex.InnerException is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry }
        || ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true;

    public Task CommitAsync(CancellationToken cancellationToken) =>
        _transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    // An uncommitted transaction is rolled back when it is disposed.
    public ValueTask DisposeAsync() => _transaction?.DisposeAsync() ?? ValueTask.CompletedTask;
}
