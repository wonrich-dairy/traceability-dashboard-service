using System.Diagnostics;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Infrastructure.Kafka;
using Wonrich.QualityLab.Contracts.Events;

namespace TraceabilityService.Application;

/// <summary>
/// Rebuilds the Traceability projection store from Kafka event history (SCRUM-140).
/// Reads retained topic history using a separate consumer group, builds a prefixed shadow table set,
/// re-resolves upstream snapshots, atomically promotes the shadow tables, and aligns live offsets.
/// </summary>
public class ProjectionRebuildService(
    IConfiguration configuration,
    IServiceScopeFactory scopes,
    IDeadLetterPublisher deadLetterPublisher,
    IAuditTrailService audit,
    TimeProvider time,
    ILogger<ProjectionRebuildService> logger) : IProjectionRebuildService
{
    private const string RebuildLockName = "wonrich-traceability-projection-rebuild";
    private static readonly string[] ProjectionTables =
    [
        "batch_records",
        "timeline_events",
        "upstream_snapshots",
        "processed_messages",
        "deviation_records",
        "lab_determinations",
        "pending_dispatch_events",
        "daily_quality_aggregates",
        "daily_reason_code_aggregates",
        "daily_society_aggregates",
    ];

    private sealed record TopicInfo(string Name, string Topic, string ConsumerGroup);

    private IReadOnlyList<TopicInfo> GetTopics() =>
    [
        new("StageEvents",
            configuration["Kafka:Topics:StageEvents"] ?? "wonrich.processing.stage-events.v1",
            configuration["Kafka:ConsumerGroups:StageEvents"] ?? "traceability-stage-events"),
        new("HoldEvents",
            configuration["Kafka:Topics:HoldEvents"] ?? "wonrich.processing.hold-events.v1",
            configuration["Kafka:ConsumerGroups:HoldEvents"] ?? "traceability-hold-events"),
        new("BatchDeterminations",
            configuration["Kafka:Topics:BatchDeterminations"] ?? "wonrich.quality-lab.batch-determinations.v1",
            configuration["Kafka:ConsumerGroups:BatchDeterminations"] ?? "traceability-batch-determinations"),
        new("ConsignmentTested",
            configuration["Kafka:Topics:ConsignmentTested"] ?? "wonrich.mcc.consignment-tested.v1",
            configuration["Kafka:ConsumerGroups:ConsignmentTested"] ?? "traceability-consignment-tested"),
    ];

    public async Task<ProjectionRebuildResultDto> RebuildAsync(
        ProjectionRebuildRequest request, AuditRequester requester, CancellationToken cancellationToken = default)
    {
        var bootstrapServers = configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            throw new InvalidOperationException("Kafka:BootstrapServers is not configured. Cannot perform rebuild from Kafka topics.");
        }
        if (configuration.GetValue("Kafka:Consumers:Enabled", true))
        {
            throw new InvalidOperationException(
                "Projection rebuild requires Kafka:Consumers:Enabled=false and all service replicas restarted with consumers stopped. " +
                "Live consumers must stay stopped through the table cutover and offset alignment.");
        }

        await using var rebuildLock = await AcquireRebuildLockAsync(cancellationToken);

        var sanitizedRequestedGroup = SanitizeForLogging(request.ConsumerGroup);
        var rebuildGroup = string.IsNullOrWhiteSpace(sanitizedRequestedGroup) ? $"traceability-rebuild-{Guid.NewGuid():N}" : sanitizedRequestedGroup;
        var topics = GetTopics();

        logger.LogInformation("Starting projection rebuild using consumer group {RebuildGroup}", rebuildGroup);

        // Capture an end offset for every partition before reading. Events appended
        // after each captured boundary remain for the live groups after restart.
        var allEvents = new List<ConsumeResult<string, string>>();
        var rebuildBoundaries = new Dictionary<TopicPartition, Offset>();

        var consumerConfig = KafkaClientSettings.Apply(new ConsumerConfig
        {
            GroupId = rebuildGroup,
            EnableAutoCommit = false,
            EnablePartitionEof = true,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }, configuration);

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig)
            .SetErrorHandler((_, e) => logger.LogWarning("Rebuild consumer Kafka error: {Reason}", e.Reason))
            .Build();

        foreach (var t in topics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var partitionId in GetPartitionIds(t.Topic))
            {
                var tp = new TopicPartition(t.Topic, partitionId);
                WatermarkOffsets watermark;
                try
                {
                    watermark = consumer.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds(5));
                }
                catch (KafkaException ex)
                {
                    throw new InvalidOperationException($"Could not query {t.Topic} partition {partitionId}; rebuild aborted.", ex);
                }

                rebuildBoundaries[tp] = watermark.High;

                if (watermark.High <= watermark.Low)
                {
                    continue;
                }

                consumer.Assign(new TopicPartitionOffset(tp, watermark.Low));
                var high = watermark.High.Value;

                while (!cancellationToken.IsCancellationRequested)
                {
                    ConsumeResult<string, string>? cr = null;
                    try
                    {
                        cr = consumer.Consume(TimeSpan.FromMilliseconds(500));
                    }
                    catch (ConsumeException ex)
                    {
                        throw new InvalidOperationException($"Could not read {t.Topic} partition {partitionId} completely; rebuild aborted.", ex);
                    }

                    if (cr is null)
                    {
                        continue;
                    }
                    if (cr.IsPartitionEOF)
                    {
                        break;
                    }

                    allEvents.Add(cr);

                    if (cr.Offset.Value + 1 >= high)
                    {
                        break;
                    }
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        consumer.Close();

        // Process the events before switching the active groups to their captured boundaries.
        var effectiveRequest = request with { ConsumerGroup = rebuildGroup };
        ProjectionRebuildResultDto result;
        try
        {
            result = await ProcessEventsAsync(allEvents, effectiveRequest, requester, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordRebuildFailureAsync(rebuildGroup, request, requester, allEvents.Count, ex, cancellationToken);
            throw;
        }

        // Align live consumers to the exact boundaries included in the rebuild.
        // Never move them past events the rebuilt projection has not read.
        result.ConsumerOffsetsAligned = await TryAlignConsumerGroupOffsetsAsync(topics, rebuildBoundaries, cancellationToken);
        if (!result.ConsumerOffsetsAligned)
        {
            result.Message += " Consumer offsets were not aligned; keep consumers stopped and verify before restarting them.";
        }

        await RecordRebuildAuditAsync(result, effectiveRequest, requester, cancellationToken);

        return result;
    }

    private IReadOnlyList<int> GetPartitionIds(string topic)
    {
        using var admin = new AdminClientBuilder(KafkaClientSettings.Apply(new AdminClientConfig(), configuration)).Build();
        var metadata = admin.GetMetadata(topic, TimeSpan.FromSeconds(10));
        var topicMetadata = metadata.Topics.FirstOrDefault(t => string.Equals(t.Topic, topic, StringComparison.Ordinal));
        if (topicMetadata is null || topicMetadata.Error.Code != ErrorCode.NoError)
        {
            throw new InvalidOperationException($"Could not discover partitions for Kafka topic '{topic}'.");
        }

        return topicMetadata.Partitions.Select(p => p.PartitionId).Order().ToArray();
    }

    public async Task<ProjectionRebuildResultDto> RebuildFromEventsAsync(
        IReadOnlyList<ConsumeResult<string, string>> events,
        ProjectionRebuildRequest request,
        AuditRequester requester,
        CancellationToken cancellationToken = default)
    {
        await using var rebuildLock = await AcquireRebuildLockAsync(cancellationToken);
        var rebuildGroup = request.ConsumerGroup ?? $"traceability-rebuild-{Guid.NewGuid():N}";
        ProjectionRebuildResultDto result;
        try
        {
            result = await ProcessEventsAsync(events, request with { ConsumerGroup = rebuildGroup }, requester, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordRebuildFailureAsync(rebuildGroup, request, requester, events.Count, ex, cancellationToken);
            throw;
        }
        await RecordRebuildAuditAsync(result, request, requester, cancellationToken);
        return result;
    }

    private async Task<ProjectionRebuildResultDto> ProcessEventsAsync(
        IReadOnlyList<ConsumeResult<string, string>> events,
        ProjectionRebuildRequest request,
        AuditRequester requester,
        CancellationToken cancellationToken)
    {
        if (configuration.GetValue("Kafka:Consumers:Enabled", true))
        {
            throw new InvalidOperationException(
                "Projection rebuild requires Kafka:Consumers:Enabled=false and all service replicas restarted with consumers stopped.");
        }

        if (!request.ClearStore)
        {
            throw new InvalidOperationException("A shadow rebuild always creates a fresh projection store; clearStore must be true.");
        }

        var stopwatch = Stopwatch.StartNew();
        var rebuildGroup = request.ConsumerGroup ?? $"traceability-rebuild-{Guid.NewGuid():N}";
        var safeRebuildGroupForLog = rebuildGroup.Replace("\r", string.Empty).Replace("\n", string.Empty);
        var topics = GetTopics();
        var stageGroup = topics.Single(t => t.Name == "StageEvents").ConsumerGroup;
        var holdGroup = topics.Single(t => t.Name == "HoldEvents").ConsumerGroup;
        var labGroup = topics.Single(t => t.Name == "BatchDeterminations").ConsumerGroup;
        var societyGroup = topics.Single(t => t.Name == "ConsignmentTested").ConsumerGroup;
        var shadowPrefix = $"rb{Guid.NewGuid():N}_";
        var shadowSwapped = false;

        logger.LogInformation("Building shadow projection from {EventCount} event(s), reader group {RebuildGroup}, table prefix {Prefix}",
            events.Count, safeRebuildGroupForLog, shadowPrefix);

        using var tableScope = ProjectionTableScope.Enter(shadowPrefix, includeAuditEntries: false);
        await using var shadowScope = scopes.CreateAsyncScope();
        var shadowDb = shadowScope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

        try
        {
            await CreateShadowTablesAsync(shadowDb, cancellationToken);

            // Handlers write processed-message rows under their real groups so replay
            // deduplication remains valid after the shadow tables become active.
            var stageHandlers = new EventHandlerMap();
            ProcessingConsumers.StageHandlers(stageHandlers);

            var holdHandlers = new EventHandlerMap();
            ProcessingConsumers.HoldHandlers(holdHandlers);

            var labHandlers = new EventHandlerMap();
            labHandlers.Add<LabDeterminationHandler<BatchClearedEvent>>("BatchCleared");
            labHandlers.Add<LabDeterminationHandler<BatchFailedEvent>>("BatchFailed");

            var societyHandlers = new EventHandlerMap();
            societyHandlers.Add<ConsignmentTestedHandler>("ConsignmentTested");

            var stageProcessor = CreateProcessor("StageEvents", stageHandlers.Handlers, stageGroup);
            var holdProcessor = CreateProcessor("HoldEvents", holdHandlers.Handlers, holdGroup);
            var labProcessor = CreateProcessor("BatchDeterminations", labHandlers.Handlers, labGroup);
            var societyProcessor = CreateProcessor("ConsignmentTested", societyHandlers.Handlers, societyGroup);

            // Apply events chronologically to respect stage -> hold -> lab ordering.
            var sortedEvents = events.OrderBy(EventTimestamp).ToList();
            var deferred = new List<ConsumeResult<string, string>>();
            var processedCount = 0;

            foreach (var cr in sortedEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var processor = SelectProcessor(cr.Topic, stageProcessor, holdProcessor, labProcessor, societyProcessor);

                try
                {
                    var outcome = await processor.ProcessAsync(cr, cancellationToken);
                    if (outcome == EventOutcome.DeadLettered)
                    {
                        throw new InvalidOperationException(
                            $"Event at {cr.Topic}[{cr.Partition.Value}]@{cr.Offset.Value} was dead-lettered; shadow rebuild aborted.");
                    }
                    processedCount++;
                }
                catch (BatchNotFoundException)
                {
                    deferred.Add(cr);
                }
            }

            foreach (var cr in deferred)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var processor = SelectProcessor(cr.Topic, stageProcessor, holdProcessor, labProcessor, societyProcessor);
                var outcome = await processor.ProcessAsync(cr, cancellationToken);
                if (outcome == EventOutcome.DeadLettered)
                {
                    throw new InvalidOperationException(
                        $"Deferred event at {cr.Topic}[{cr.Partition.Value}]@{cr.Offset.Value} was dead-lettered; shadow rebuild aborted.");
                }
                processedCount++;
            }

            var snapshotsResolved = request.ResolveUpstreamSnapshots
                ? await ResolveUpstreamSnapshotsAsync(
                    shadowDb,
                    shadowScope.ServiceProvider.GetRequiredService<IUpstreamResolver>(),
                    requester.CorrelationId ?? Guid.NewGuid().ToString("N"),
                    cancellationToken)
                : 0;

            var batchesCount = await shadowDb.BatchRecords.CountAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var retiredPrefix = await SwapProjectionTablesAsync(shadowDb, shadowPrefix, cancellationToken);
            shadowSwapped = true;
            stopwatch.Stop();

            logger.LogInformation(
                "Projection shadow cutover complete; retired tables use {RetiredTablePrefix}. {Events} events, {Snapshots} snapshots resolved, {Batches} batches in {Ms}ms",
                retiredPrefix, processedCount, snapshotsResolved, batchesCount, stopwatch.ElapsedMilliseconds);

            return new ProjectionRebuildResultDto
            {
                ConsumerGroup = rebuildGroup,
                EventsProcessed = processedCount,
                SnapshotsResolved = snapshotsResolved,
                BatchesCount = batchesCount,
                ConsumerOffsetsAligned = false,
                SwitchedOver = true,
                RetiredTablePrefix = retiredPrefix,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Message = $"Projection rebuilt from {processedCount} event(s), cut over atomically, and contains {batchesCount} batch record(s). {snapshotsResolved} upstream snapshot(s) re-resolved.",
            };
        }
        catch
        {
            if (!shadowSwapped)
            {
                try
                {
                    await DropShadowTablesAsync(shadowDb, shadowPrefix, CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    logger.LogWarning(cleanupException, "Could not remove failed shadow projection tables with prefix {Prefix}", shadowPrefix);
                }
            }

            throw;
        }
    }

    private Task RecordRebuildAuditAsync(
        ProjectionRebuildResultDto result,
        ProjectionRebuildRequest request,
        AuditRequester requester,
        CancellationToken cancellationToken) =>
        audit.RecordAdminActionAsync(
            AuditActions.ProjectionRebuilt,
            "Projection",
            result.ConsumerGroup,
            new
            {
                consumerGroup = result.ConsumerGroup,
                eventsProcessed = result.EventsProcessed,
                snapshotsResolved = result.SnapshotsResolved,
                batchesCount = result.BatchesCount,
                durationMs = result.DurationMs,
                clearStore = request.ClearStore,
                consumerOffsetsAligned = result.ConsumerOffsetsAligned,
                switchedOver = result.SwitchedOver,
                retiredTablePrefix = result.RetiredTablePrefix,
                role = requester.Role,
            },
            requester,
            cancellationToken);

    private async Task RecordRebuildFailureAsync(
        string rebuildGroup,
        ProjectionRebuildRequest request,
        AuditRequester requester,
        int eventsRead,
        Exception failure,
        CancellationToken cancellationToken)
    {
        var safeRebuildGroup = SanitizeForLogging(rebuildGroup);
        try
        {
            await audit.RecordAdminActionAsync(
                AuditActions.ProjectionRebuilt,
                "Projection",
                safeRebuildGroup,
                new
                {
                    consumerGroup = safeRebuildGroup,
                    eventsRead,
                    clearStore = request.ClearStore,
                    failed = true,
                    failure = failure.Message,
                    consumerOffsetsAligned = false,
                    switchedOver = false,
                    role = requester.Role,
                },
                requester,
                cancellationToken);
        }
        catch (Exception auditException)
        {
            logger.LogError(auditException, "Could not write the failure audit for projection rebuild {RebuildGroup}", safeRebuildGroup);
        }
    }

    private static string SanitizeForLogging(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private async Task<RebuildLockLease> AcquireRebuildLockAsync(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
            if (!db.Database.IsRelational())
            {
                throw new InvalidOperationException("Projection rebuild requires MySQL advisory locks.");
            }

            await db.Database.OpenConnectionAsync(cancellationToken);
            var connection = db.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT GET_LOCK(@lockName, 0)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@lockName";
            parameter.Value = RebuildLockName;
            command.Parameters.Add(parameter);

            var acquired = await command.ExecuteScalarAsync(cancellationToken);
            if (Convert.ToInt32(acquired, System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException("Another projection rebuild is already running.");
            }

            return new RebuildLockLease(scope, db);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private sealed class RebuildLockLease(AsyncServiceScope scope, TraceabilityDbContext db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                var connection = db.Database.GetDbConnection();
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    try
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = "SELECT RELEASE_LOCK(@lockName)";
                        var parameter = command.CreateParameter();
                        parameter.ParameterName = "@lockName";
                        parameter.Value = RebuildLockName;
                        command.Parameters.Add(parameter);
                        await command.ExecuteScalarAsync();
                    }
                    finally
                    {
                        await db.Database.CloseConnectionAsync();
                    }
                }
            }
            finally
            {
                await scope.DisposeAsync();
            }
        }
    }

    private static async Task CreateShadowTablesAsync(
        TraceabilityDbContext shadowDb,
        CancellationToken cancellationToken)
    {
        if (!shadowDb.Database.IsRelational())
        {
            throw new InvalidOperationException("Atomic projection cutover requires a relational database provider.");
        }

        var creator = shadowDb.Database.GetService<IRelationalDatabaseCreator>();
        await creator.CreateTablesAsync(cancellationToken);
    }

    private static async Task<string> SwapProjectionTablesAsync(
        TraceabilityDbContext db,
        string shadowPrefix,
        CancellationToken cancellationToken)
    {
        var retiredPrefix = $"old{Guid.NewGuid():N}_";
        var renames = ProjectionTables.SelectMany(table => new[]
        {
            $"`{table}` TO `{retiredPrefix}{table}`",
            $"`{shadowPrefix}{table}` TO `{table}`",
        });

        // MySQL executes one multi-table RENAME TABLE atomically. audit_entries is
        // intentionally omitted: it stays in place and retains its append-only triggers.
#pragma warning disable EF1002 // Identifiers are fixed table names plus internally generated GUID prefixes.
        await db.Database.ExecuteSqlRawAsync(
            $"RENAME TABLE {string.Join(", ", renames)}",
            cancellationToken);
#pragma warning restore EF1002
        return retiredPrefix;
    }

    private static async Task DropShadowTablesAsync(
        TraceabilityDbContext db,
        string prefix,
        CancellationToken cancellationToken)
    {
        // Child tables go first because the shadow schema has the same foreign keys as the active store.
        foreach (var table in ProjectionTables.Reverse())
        {
#pragma warning disable EF1002 // The table comes from the fixed projection table list and the prefix is an internal GUID.
            await db.Database.ExecuteSqlRawAsync(
                $"DROP TABLE IF EXISTS `{prefix}{table}`",
                cancellationToken);
#pragma warning restore EF1002
        }
    }

    private async Task<int> ResolveUpstreamSnapshotsAsync(
        TraceabilityDbContext shadowDb,
        IUpstreamResolver shadowResolver,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var pendingBatchIds = await shadowDb.UpstreamSnapshots
            .Where(s => s.ResolutionStatus == ResolutionStatus.PendingRetry)
            .Select(s => s.BatchId)
            .ToListAsync(cancellationToken);

        var resolvedCount = 0;
        foreach (var batchId in pendingBatchIds)
        {
            try
            {
                var attempt = await shadowResolver.ResolveAsync(batchId, correlationId, cancellationToken);
                if (attempt == ResolutionAttempt.Resolved)
                {
                    resolvedCount++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resolve upstream snapshot for {BatchId} during rebuild", batchId);
            }
        }

        return resolvedCount;
    }

    private async Task<bool> TryAlignConsumerGroupOffsetsAsync(
        IReadOnlyList<TopicInfo> topics,
        IReadOnlyDictionary<TopicPartition, Offset> boundaries,
        CancellationToken cancellationToken)
    {
        var bootstrapServers = configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            return false;
        }

        try
        {
            using var admin = new AdminClientBuilder(
                KafkaClientSettings.Apply(new AdminClientConfig(), configuration)).Build();
            foreach (var t in topics)
            {
                var partitionOffsets = boundaries
                    .Where(pair => string.Equals(pair.Key.Topic, t.Topic, StringComparison.Ordinal))
                    .Select(pair => new TopicPartitionOffset(pair.Key, pair.Value))
                    .ToList();

                if (partitionOffsets.Count == 0)
                {
                    logger.LogWarning("No captured rebuild offsets found for {Topic}; refusing partial switchover", t.Topic);
                    return false;
                }

                await admin.AlterConsumerGroupOffsetsAsync(
                [
                    new ConsumerGroupTopicPartitionOffsets(t.ConsumerGroup, partitionOffsets)
                ]);
                logger.LogInformation("Aligned consumer group {Group} offsets to the rebuilt partition boundaries", t.ConsumerGroup);
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not alter consumer group offsets during switchover (broker may be in test/standalone mode)");
            return false;
        }
    }

    private EventProcessor CreateProcessor(string name, IReadOnlyDictionary<string, Type> handlers, string consumerGroup)
    {
        return new EventProcessor(
            new KafkaConsumerOptions
            {
                Name = name,
                Topic = configuration[$"Kafka:Topics:{name}"] ?? name,
                ConsumerGroup = consumerGroup,
                DeadLetterTopic = configuration[$"Kafka:Topics:{name}DeadLetter"] ?? $"{name}-dlq",
            },
            handlers,
            scopes,
            deadLetterPublisher,
            time,
            scopes.CreateScope().ServiceProvider.GetRequiredService<ILogger<EventProcessor>>());
    }

    private EventProcessor SelectProcessor(
        string topic, EventProcessor stage, EventProcessor hold, EventProcessor lab, EventProcessor society)
    {
        if (topic.Contains("consignment", StringComparison.OrdinalIgnoreCase))
        {
            return society;
        }
        if (topic.Contains("hold", StringComparison.OrdinalIgnoreCase))
        {
            return hold;
        }
        if (topic.Contains("determination", StringComparison.OrdinalIgnoreCase))
        {
            return lab;
        }
        return stage;
    }

    private static DateTime EventTimestamp(ConsumeResult<string, string> cr)
    {
        if (cr.Message.Timestamp.Type != TimestampType.NotAvailable)
        {
            return cr.Message.Timestamp.UtcDateTime;
        }
        return DateTime.UtcNow;
    }
}

