using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Infrastructure.MccIntake;
using TraceabilityService.Observability;

namespace TraceabilityService.Application;

/// <summary>A batch to try at once, and the correlation ID of the event that created its snapshot, if any.</summary>
public sealed record QueuedResolution(string BatchId, string? CorrelationId);

/// <summary>
/// Batches whose snapshot was just created, for the worker to try straight away rather than at
/// its next poll. Only a hint: a batch lost from here, e.g. when the app stops, is still
/// PendingRetry and due, so the next poll picks it up.
/// </summary>
public sealed class UpstreamResolutionQueue
{
    // Bounded so a worker that is off (not configured) cannot make it grow; the oldest go first,
    // and the poll finds them anyway.
    private readonly Channel<QueuedResolution> _channel = Channel.CreateBounded<QueuedResolution>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Enqueue(string batchId, string? correlationId) =>
        _channel.Writer.TryWrite(new QueuedResolution(batchId, correlationId));

    public ChannelReader<QueuedResolution> Reader => _channel.Reader;
}

/// <summary>
/// Resolves PendingRetry upstream snapshots in the background (SCRUM-132): batches queued by the
/// consumers as soon as they arrive, and every Upstream:PollIntervalSeconds the rows that are
/// due, <see cref="BatchSize"/> at a time, oldest first. Backoff between attempts is
/// <see cref="UpstreamResolutionRules"/>'; the claim in <see cref="UpstreamResolver"/> keeps two
/// instances off the same row. Idle when the MCC and Intake Service or Auth Service is not configured.
/// </summary>
public sealed class UpstreamResolutionWorker(
    IServiceScopeFactory scopes,
    UpstreamResolutionQueue queue,
    IOptions<UpstreamResolutionOptions> options,
    IOptions<MccIntakeOptions> mccOptions,
    IOptions<AuthServiceOptions> authOptions,
    TimeProvider time,
    ILogger<UpstreamResolutionWorker> logger) : BackgroundService
{
    public const int BatchSize = 20;

    // Batches of due rows per poll, so a long backlog does not hold up newly queued batches.
    private const int MaxBatchesPerPoll = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (MissingConfiguration() is { } missing)
        {
            logger.LogWarning("Upstream resolution is off: {Missing} not configured. Snapshots stay PendingRetry.", missing);
            return;
        }

        var pollInterval = options.Value.PollInterval;
        logger.LogInformation("Upstream resolution worker started: polling every {PollInterval}, {MaxAttempts} attempts per batch",
            pollInterval, options.Value.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ResolveDueAsync(stoppingToken);
                await ResolveQueuedAsync(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // E.g. the database is unreachable. Wait a poll rather than spin.
                logger.LogError(ex, "Upstream resolution poll failed; trying again in {PollInterval}", pollInterval);
                await Task.Delay(pollInterval, time, stoppingToken);
            }
        }
    }

    private string? MissingConfiguration()
    {
        var mcc = mccOptions.Value;
        var missing = new[]
        {
            (Key: $"{MccIntakeOptions.Section}:BaseUrl", Value: mcc.BaseUrl),
            (Key: $"{MccIntakeOptions.Section}:ClientId", Value: mcc.ClientId),
            (Key: $"{MccIntakeOptions.Section}:ClientSecret", Value: mcc.ClientSecret),
            (Key: $"{AuthServiceOptions.Section}:BaseUrl", Value: authOptions.Value.BaseUrl),
        }.Where(s => string.IsNullOrWhiteSpace(s.Value)).Select(s => s.Key).ToList();

        return missing.Count == 0 ? null : string.Join(", ", missing);
    }

    private async Task ResolveDueAsync(CancellationToken cancellationToken)
    {
        for (var round = 0; round < MaxBatchesPerPoll; round++)
        {
            var due = await DueBatchIdsAsync(cancellationToken);
            foreach (var batchId in due)
            {
                await ResolveOneAsync(batchId, correlationId: null, cancellationToken);
            }

            // Each attempt moves its row's NextAttemptAt on, so the next query gets fresh rows.
            if (due.Count < BatchSize)
            {
                return;
            }
        }
    }

    // Served by IX_upstream_snapshots_ResolutionStatus_NextAttemptAt. Null NextAttemptAt (rows
    // from before retries were tracked) is due, and sorts first.
    private async Task<List<string>> DueBatchIdsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        var now = time.GetUtcNow().UtcDateTime;

        return await db.UpstreamSnapshots.AsNoTracking()
            .Where(s => s.ResolutionStatus == ResolutionStatus.PendingRetry && (s.NextAttemptAt == null || s.NextAttemptAt <= now))
            .OrderBy(s => s.NextAttemptAt)
            .ThenBy(s => s.Id)
            .Select(s => s.BatchId)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
    }

    // Waits up to one poll interval for queued batches, then tries everything queued.
    private async Task ResolveQueuedAsync(TimeSpan pollInterval, CancellationToken stoppingToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        wait.CancelAfter(pollInterval);
        try
        {
            await queue.Reader.WaitToReadAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (queue.Reader.TryRead(out var queued))
        {
            await ResolveOneAsync(queued.BatchId, queued.CorrelationId, stoppingToken);
        }
    }

    // One attempt in its own scope, so a failure leaves nothing behind for the next. A failure
    // here is logged and the row stays due once its claim runs out.
    private async Task ResolveOneAsync(string batchId, string? correlationId, CancellationToken cancellationToken)
    {
        // The consumed event's ID when it is safe to forward, so its log lines and the upstream
        // calls share one; otherwise a new one per attempt.
        correlationId = CorrelationIdMiddleware.IsSafeId(correlationId) ? correlationId! : Guid.NewGuid().ToString("N");

        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["BatchId"] = batchId,
        });

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var resolver = scope.ServiceProvider.GetRequiredService<IUpstreamResolver>();
            await resolver.ResolveAsync(batchId, correlationId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Upstream resolution attempt for {BatchId} failed", batchId);
        }
    }
}

public static class UpstreamResolutionServiceCollectionExtensions
{
    /// <summary>
    /// The resolver, its retry worker, and the MCC and Intake Service client they call
    /// (Services:MccIntake, Services:Auth, Upstream).
    /// </summary>
    public static IServiceCollection AddUpstreamResolution(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<UpstreamResolutionOptions>().Bind(configuration.GetSection(UpstreamResolutionOptions.Section));
        services.AddMccIntakeClient(configuration);
        services.TryAddSingleton<UpstreamResolutionQueue>();
        services.AddScoped<IUpstreamResolver, UpstreamResolver>();
        services.AddHostedService<UpstreamResolutionWorker>();
        return services;
    }
}
