using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Wonrich.QualityLab.Contracts.Events;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Infrastructure.Kafka;

namespace TraceabilityService.Application;

/// <summary>One message from wonrich.quality-lab.batch-determinations.v1, as received.</summary>
/// <param name="EventType">The eventType header; the body's eventType is used when it is missing.</param>
/// <param name="Payload">The message value: the event as camelCase JSON.</param>
public sealed record BatchDeterminationMessage(string Topic, string? EventType, string Payload);

public enum BatchDeterminationOutcome
{
    Applied,

    // Already handled by this consumer group: Quality Lab's relay delivers at least once.
    Duplicate,

    // Not BatchCleared or BatchFailed. Quality Lab's contract says to skip an unknown type
    // on a v1 topic rather than dead-letter it.
    Ignored
}

/// <summary>Applies one lab determination message; see <see cref="BatchDeterminationHandler"/>.</summary>
public interface IBatchDeterminationHandler
{
    Task<BatchDeterminationOutcome> HandleAsync(BatchDeterminationMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies one Quality Lab determination to the read model in a single transaction
/// (SCRUM-131): the lab_determinations row (and the one it supersedes), a Lab timeline
/// event, a failure's deviations, the batch's status, LastEventAt and completeness, and
/// both daily aggregate tables. What the determination does is decided by
/// <see cref="DeterminationResolver"/>; this class only reads and writes.
/// </summary>
public class BatchDeterminationHandler(
    TraceabilityDbContext db, TimeProvider time, BatchDeterminationConsumerOptions options) : IBatchDeterminationHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TraceabilityDbContext _db = db;
    private readonly TimeProvider _time = time;

    // processed_messages is per consumer group, so this is the group the consumer reads as.
    private readonly string _consumerGroup = options.ConsumerGroup;

    /// <exception cref="BatchNotFoundException">
    /// The batch is not here yet. Nothing is written, so a retry starts clean.
    /// </exception>
    public async Task<BatchDeterminationOutcome> HandleAsync(
        BatchDeterminationMessage message, CancellationToken cancellationToken = default)
    {
        var determination = Deserialize(message);
        if (determination is null)
        {
            return BatchDeterminationOutcome.Ignored;
        }

        var now = _time.GetUtcNow().UtcDateTime;

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        if (!await TryRecordProcessedAsync(determination.EventId, message.Topic, now, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return BatchDeterminationOutcome.Duplicate;
        }

        // Thrown inside the transaction, so the processed_messages row goes with it.
        var batch = await _db.BatchRecords
            .Include(b => b.UpstreamSnapshot)
            .SingleOrDefaultAsync(b => b.BatchId == determination.BatchId, cancellationToken)
            ?? throw new BatchNotFoundException(determination.BatchId);

        var stored = await _db.LabDeterminations
            .Where(d => d.BatchId == batch.BatchId)
            .ToListAsync(cancellationToken);

        var deviations = determination is BatchFailedEvent failed ? LabDeviations.From(failed) : [];
        var result = determination is BatchFailedEvent ? DeterminationResult.Failed : DeterminationResult.Cleared;

        var resolution = DeterminationResolver.Resolve(
            batch.Status,
            await FactsOfAsync(batch.BatchId, stored, cancellationToken),
            new DeterminationFacts(
                determination.DeterminationId,
                result,
                determination.DeterminedAtUtc,
                determination.SupersedesDeterminationId,
                [.. deviations.Select(d => d.AggregateReasonCode)]));

        // The same determination under another event ID: it is already applied.
        if (resolution.IsDuplicate)
        {
            await transaction.RollbackAsync(cancellationToken);
            return BatchDeterminationOutcome.Duplicate;
        }

        // Saved first: the determination and deviation rows point at its Id.
        var timelineEvent = new TimelineEvent
        {
            BatchId = batch.BatchId,
            Checkpoint = Checkpoint.Lab,
            EventType = determination.EventType,
            OccurredAt = determination.DeterminedAtUtc,
            RecordedBy = determination.DeterminedBy,
            IsDeviation = determination.IsDeviation || deviations.Count > 0,
            Payload = message.Payload,
            SourceEventId = determination.EventId,
            ContractVersion = determination.SchemaVersion,
        };
        _db.TimelineEvents.Add(timelineEvent);
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var replaced in resolution.Supersede)
        {
            var row = stored.Single(d => d.DeterminationId == replaced.DeterminationId);
            row.IsSuperseded = true;
            row.SupersededAt = replaced.SupersededAt ?? row.SupersededAt;
        }

        _db.LabDeterminations.Add(new LabDetermination
        {
            DeterminationId = determination.DeterminationId,
            BatchId = batch.BatchId,
            Result = result,
            DeterminedAt = determination.DeterminedAtUtc,
            DeterminedBy = determination.DeterminedBy,
            SupersedesDeterminationId = determination.SupersedesDeterminationId,
            IsSuperseded = !resolution.IsActive,
            SupersededAt = resolution.SupersededAt,
            TimelineEventId = timelineEvent.Id,
        });

        // Kept for a determination that arrives already superseded too: they are its history.
        // Only the current determination's deviations are counted in the aggregates.
        _db.DeviationRecords.AddRange(deviations.Select(d => new DeviationRecord
        {
            BatchId = batch.BatchId,
            TimelineEventId = timelineEvent.Id,
            Checkpoint = Checkpoint.Lab,
            OccurredAt = determination.DeterminedAtUtc,
            ReasonCode = d.ReasonCode,
            Parameter = d.Parameter,
            ObservedValue = d.ObservedValue,
            Limit = d.Limit,
            Description = d.Description,
        }));

        // The production day comes from the batch's first event, before this one can move it.
        var productionDay = FactoryCalendar.ProductionDay(batch.BatchId, batch.FirstEventAt);

        batch.Status = resolution.NewStatus;
        batch.FirstEventAt = Min(batch.FirstEventAt, determination.DeterminedAtUtc);
        batch.LastEventAt = Max(batch.LastEventAt, determination.DeterminedAtUtc);
        if (batch.Completeness == TraceCompleteness.Partial && await IsCompleteAsync(batch, cancellationToken))
        {
            batch.Completeness = TraceCompleteness.Complete;
        }

        await _db.SaveChangesAsync(cancellationToken);

        await ApplyAggregatesAsync(batch, productionDay, resolution.Changes, now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return BatchDeterminationOutcome.Applied;
    }

    private static BatchDeterminationEventBase? Deserialize(BatchDeterminationMessage message)
    {
        var eventType = message.EventType;
        if (string.IsNullOrEmpty(eventType))
        {
            using var body = JsonDocument.Parse(message.Payload);
            eventType = body.RootElement.TryGetProperty("eventType", out var type) ? type.GetString() : null;
        }

        return eventType switch
        {
            "BatchCleared" => JsonSerializer.Deserialize<BatchClearedEvent>(message.Payload, Json),
            "BatchFailed" => JsonSerializer.Deserialize<BatchFailedEvent>(message.Payload, Json),
            _ => null,
        };
    }

    // Inserting is the duplicate check: the unique index on (ConsumerGroup, SourceEventId)
    // rejects a second row, even if two consumers race.
    private async Task<bool> TryRecordProcessedAsync(Guid eventId, string topic, DateTime now, CancellationToken cancellationToken)
    {
        _db.ProcessedMessages.Add(new ProcessedMessage
        {
            ConsumerGroup = _consumerGroup,
            SourceEventId = eventId,
            Topic = topic,
            ProcessedAt = now,
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry })
        {
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    // Each stored determination with the codes of its deviation rows, which are what the
    // aggregates counted for it.
    private async Task<List<DeterminationFacts>> FactsOfAsync(
        string batchId, List<LabDetermination> stored, CancellationToken cancellationToken)
    {
        var reasons = (await _db.DeviationRecords
                .Where(r => r.BatchId == batchId && r.Checkpoint == Checkpoint.Lab)
                .Select(r => new { r.TimelineEventId, r.ReasonCode })
                .ToListAsync(cancellationToken))
            .ToLookup(r => r.TimelineEventId, r => r.ReasonCode ?? DailyReasonCodeAggregate.Unspecified);

        return [.. stored.Select(d => new DeterminationFacts(
            d.DeterminationId,
            d.Result,
            d.DeterminedAt,
            d.SupersedesDeterminationId,
            [.. reasons[d.TimelineEventId]],
            d.IsSuperseded))];
    }

    // The same rule the batch record shows: MCC and Intake come from the resolved upstream
    // snapshot, Processing and Lab from timeline events. This event is the Lab one.
    private async Task<bool> IsCompleteAsync(BatchRecord batch, CancellationToken cancellationToken) =>
        batch.UpstreamSnapshot?.ResolutionStatus == ResolutionStatus.Resolved
        && await _db.TimelineEvents.AnyAsync(
            e => e.BatchId == batch.BatchId && e.Checkpoint == Checkpoint.Processing, cancellationToken);

    // INSERT ... ON DUPLICATE KEY UPDATE adds to the row atomically, so consumers on other
    // partitions updating the same day cannot lose each other's changes, and a missing row
    // is created. A status move is -1 and +1, so BatchCount stays as it is.
    private async Task ApplyAggregatesAsync(
        BatchRecord batch, DateOnly day, AggregateChanges changes, DateTime now, CancellationToken cancellationToken)
    {
        if (changes.StatusCounts.Count > 0 || changes.LabDeviations != 0)
        {
            var pending = changes.StatusCounts.GetValueOrDefault(StatusBucket.Pending);
            var cleared = changes.StatusCounts.GetValueOrDefault(StatusBucket.Cleared);
            var failed = changes.StatusCounts.GetValueOrDefault(StatusBucket.Failed);
            var onHold = changes.StatusCounts.GetValueOrDefault(StatusBucket.OnHold);
            var lab = changes.LabDeviations;

            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO daily_quality_aggregates
                    (Date, ProductLine, Facility, BatchCount, PendingCount, ClearedCount, FailedCount, OnHoldCount,
                     DeviationCount, MccDeviationCount, IntakeDeviationCount, ProcessingDeviationCount, LabDeviationCount, UpdatedAt)
                VALUES ({day}, {batch.ProductLine}, {batch.Facility}, 0, {pending}, {cleared}, {failed}, {onHold},
                        {lab}, 0, 0, 0, {lab}, {now})
                ON DUPLICATE KEY UPDATE
                    PendingCount = PendingCount + {pending},
                    ClearedCount = ClearedCount + {cleared},
                    FailedCount = FailedCount + {failed},
                    OnHoldCount = OnHoldCount + {onHold},
                    DeviationCount = DeviationCount + {lab},
                    LabDeviationCount = LabDeviationCount + {lab},
                    UpdatedAt = {now}
                """, cancellationToken);
        }

        foreach (var (code, change) in changes.ReasonCodes)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO daily_reason_code_aggregates (Date, ProductLine, Facility, ReasonCode, DeviationCount, UpdatedAt)
                VALUES ({day}, {batch.ProductLine}, {batch.Facility}, {code}, {change}, {now})
                ON DUPLICATE KEY UPDATE
                    DeviationCount = DeviationCount + {change},
                    UpdatedAt = {now}
                """, cancellationToken);

            // The dashboard lists every row it sums, so a reason that no longer applies is removed.
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM daily_reason_code_aggregates
                WHERE Facility = {batch.Facility} AND Date = {day} AND ProductLine = {batch.ProductLine}
                  AND ReasonCode = {code} AND DeviationCount = 0
                """, cancellationToken);
        }
    }

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
}
