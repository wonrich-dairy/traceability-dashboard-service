using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Infrastructure.MccIntake;
using TraceabilityService.Observability;

namespace TraceabilityService.Application;

/// <summary>Upstream:MaxAttempts and Upstream:PollIntervalSeconds.</summary>
public sealed class UpstreamResolutionOptions
{
    public const string Section = "Upstream";

    // Attempts before a snapshot that keeps failing is given up on as Unavailable.
    public int MaxAttempts { get; set; } = 10;

    // How often the retry worker looks for due PendingRetry rows.
    public int PollIntervalSeconds { get; set; } = 30;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds > 0 ? PollIntervalSeconds : 30);
}

public enum ResolutionAttempt
{
    // Not due, not PendingRetry, or another worker is on it: nothing was asked.
    Skipped,
    Resolved,
    PendingRetry,
    Unavailable
}

public interface IUpstreamResolver
{
    /// <summary>One attempt to resolve a batch's snapshot, if it is PendingRetry and due.</summary>
    Task<ResolutionAttempt> ResolveAsync(string batchId, string correlationId, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves a batch's upstream snapshot from the MCC and Intake Service and stores the result
/// (SCRUM-132). One attempt is three steps, so no transaction is held open over HTTP:
/// <list type="number">
/// <item>Claim the row: count the attempt, push NextAttemptAt past the time the calls can take,
/// and set LastCorrelationId. That is the row's concurrency token, so of two workers claiming
/// it at once only one UPDATE matches; and a row another worker has claimed is not due.</item>
/// <item>Look the batch up: factory intake's batch by the dispatch number, then its trace
/// (<see cref="UpstreamSnapshotMapper"/>).</item>
/// <item>Under the batch's row lock, store what <see cref="UpstreamResolutionRules"/> decides.
/// Resolving also fills BatchReference and can make the trace Complete.</item>
/// </list>
/// </summary>
public sealed class UpstreamResolver(
    TraceabilityDbContext db,
    IMccIntakeClient mcc,
    IOptions<UpstreamResolutionOptions> options,
    IOptions<MccIntakeOptions> mccOptions,
    TimeProvider time,
    ILogger<UpstreamResolver> logger) : IUpstreamResolver
{
    public async Task<ResolutionAttempt> ResolveAsync(string batchId, string correlationId, CancellationToken cancellationToken)
    {
        var attemptCount = await ClaimAsync(batchId, correlationId, cancellationToken);
        if (attemptCount is null)
        {
            return ResolutionAttempt.Skipped;
        }

        var dispatchRef = await db.BatchRecords.AsNoTracking()
            .Where(b => b.BatchId == batchId)
            .Select(b => b.DispatchRef)
            .SingleAsync(cancellationToken);

        var lookup = await LookUpAsync(dispatchRef, correlationId, cancellationToken);
        var decision = UpstreamResolutionRules.Decide(lookup, attemptCount.Value, options.Value.MaxAttempts, Now);

        if (!await StoreAsync(batchId, correlationId, lookup, decision, cancellationToken))
        {
            return ResolutionAttempt.Skipped;
        }

        TraceabilityMetrics.RecordUpstreamResolution(decision.Status);
        logger.Log(decision.Status == ResolutionStatus.Unavailable ? LogLevel.Warning : LogLevel.Information,
            "Upstream snapshot for {BatchId}: {Status} after attempt {Attempt} ({Result}). {FailureReason}",
            batchId, decision.Status, attemptCount, lookup.Result, decision.FailureReason);

        return decision.Status switch
        {
            ResolutionStatus.Resolved => ResolutionAttempt.Resolved,
            ResolutionStatus.Unavailable => ResolutionAttempt.Unavailable,
            _ => ResolutionAttempt.PendingRetry,
        };
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // Longer than the two calls can take, each bounded by the timeout, with room to write the result.
    private TimeSpan ClaimLease => mccOptions.Value.Timeout * 2 + TimeSpan.FromMinutes(1);

    // The attempt count with this attempt, or null when the row is not this worker's to try.
    private async Task<int?> ClaimAsync(string batchId, string correlationId, CancellationToken cancellationToken)
    {
        var now = Now;
        var snapshot = await db.UpstreamSnapshots.SingleOrDefaultAsync(
            s => s.BatchId == batchId
                 && s.ResolutionStatus == ResolutionStatus.PendingRetry
                 && (s.NextAttemptAt == null || s.NextAttemptAt <= now),
            cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        snapshot.AttemptCount++;
        snapshot.LastAttemptAt = now;
        snapshot.NextAttemptAt = now + ClaimLease;
        snapshot.LastCorrelationId = correlationId;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return snapshot.AttemptCount;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker claimed it between the read and the UPDATE.
            return null;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task<UpstreamLookup> LookUpAsync(string? dispatchRef, string correlationId, CancellationToken cancellationToken)
    {
        // Every batch Processing creates has one; this covers a batch whose events carried none.
        if (string.IsNullOrWhiteSpace(dispatchRef))
        {
            return UpstreamLookup.Failed("No dispatch number is known for the batch yet, so it cannot be looked up.");
        }

        try
        {
            var intake = UpstreamSnapshotMapper.IntakeBatchOf(await mcc.FindBatchesAsync(dispatchRef, correlationId, cancellationToken));
            if (intake is null)
            {
                return UpstreamLookup.NotFound($"MCC and Intake Service has no batch from dispatch note {dispatchRef}.");
            }

            var trace = await mcc.TraceBatchAsync(intake.Reference, correlationId, cancellationToken);
            return trace is null
                ? UpstreamLookup.NotFound($"MCC and Intake Service has no batch {intake.Reference} to trace (dispatch note {dispatchRef}).")
                : UpstreamSnapshotMapper.Map(intake, trace);
        }
        catch (HttpRequestException ex)
        {
            return UpstreamLookup.Failed(ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpstreamLookup.Failed($"MCC and Intake Service did not answer within {mccOptions.Value.Timeout.TotalSeconds:0}s.");
        }
        catch (JsonException ex)
        {
            return UpstreamLookup.Failed($"MCC and Intake Service sent a response that does not match its contract: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recorded like any failure, so the attempt counts and the row is given up on eventually.
            logger.LogError(ex, "Upstream lookup for dispatch {DispatchRef} failed unexpectedly", dispatchRef);
            return UpstreamLookup.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // False when the row is no longer this attempt's: deleted, or the claim ran out and another
    // worker took it, whose result wins.
    private async Task<bool> StoreAsync(
        string batchId, string correlationId, UpstreamLookup lookup, ResolutionDecision decision, CancellationToken cancellationToken)
    {
        // READ COMMITTED with the batch's row lock, like the consumers (BatchLocks): the
        // completeness check below then sees what a consumer committed meanwhile. The unit
        // tests' in-memory database has neither, like BatchLocks allows for.
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        await db.LockBatchAsync(batchId, cancellationToken);

        var batch = await db.BatchRecords
            .Include(b => b.UpstreamSnapshot)
            .SingleOrDefaultAsync(b => b.BatchId == batchId, cancellationToken);
        var snapshot = batch?.UpstreamSnapshot;

        if (batch is null || snapshot is null
            || snapshot.ResolutionStatus != ResolutionStatus.PendingRetry
            || snapshot.LastCorrelationId != correlationId)
        {
            logger.LogInformation("Upstream snapshot for {BatchId} changed during the attempt; result discarded", batchId);
            return false;
        }

        snapshot.ResolutionStatus = decision.Status;
        snapshot.FailureReason = decision.FailureReason;
        snapshot.NextAttemptAt = decision.NextAttemptAt;

        if (decision.Status == ResolutionStatus.Resolved && lookup.Payload is { } payload)
        {
            snapshot.Payload = UpstreamSnapshotPayload.Serialize(payload);
            snapshot.ResolvedAt = Now;
            batch.BatchReference = payload.BatchReference;

            // The same rule as the consumers: MCC and Intake from the resolved snapshot, which
            // this is, Processing and Lab from timeline events.
            if (batch.Completeness == TraceCompleteness.Partial
                && await db.TimelineEvents.AnyAsync(e => e.BatchId == batchId && e.Checkpoint == Checkpoint.Processing, cancellationToken)
                && await db.TimelineEvents.AnyAsync(e => e.BatchId == batchId && e.Checkpoint == Checkpoint.Lab, cancellationToken))
            {
                batch.Completeness = TraceCompleteness.Complete;
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Upstream snapshot for {BatchId} was claimed again during the attempt; result discarded", batchId);
            return false;
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return true;
    }
}
