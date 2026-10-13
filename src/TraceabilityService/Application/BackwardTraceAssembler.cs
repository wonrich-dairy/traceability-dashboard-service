using System.Text.Json;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// Builds the backward trace (SCRUM-134) from rows already loaded: the batch, its timeline
/// events, its deviations, its active lab determinations and its upstream snapshot. No
/// database access and no clock, so the same rows always give the same trace, and the
/// origin rules are unit-tested without a database.
/// </summary>
/// <remarks>
/// Nothing is recomputed that the read model already holds: the status is the one
/// BatchStatusRules stored on the batch (SCRUM-130), the lab result comes from LabResults,
/// a checkpoint's gap rule is BatchRecordAssembler's, and an open hold is one
/// BatchStatusRules.OpenHolds calls open. Tanks, consignments and societies keep the
/// snapshot's order.
/// </remarks>
public static class BackwardTraceAssembler
{
    public const string HoldRaised = "ProcessingHoldRaised";
    public const string HoldResolved = "ProcessingHoldResolved";

    private static readonly Checkpoint[] StageOrder = BatchRecordAssembler.StageOrder;

    // Returns null when there is no batch, which the caller turns into a 404.
    public static BatchTraceResponse? Assemble(
        BatchRecord? batch,
        IEnumerable<TimelineEvent> events,
        IEnumerable<DeviationRecord> deviations,
        IEnumerable<LabDetermination> determinations,
        UpstreamSnapshot? snapshot)
    {
        if (batch is null)
        {
            return null;
        }

        var payload = snapshot is { ResolutionStatus: ResolutionStatus.Resolved, Payload: not null }
            ? UpstreamSnapshotPayload.Parse(snapshot.Payload)
            : null;

        var eventList = events.ToList();
        var sourceEventIds = eventList.ToDictionary(e => e.Id, e => e.SourceEventId);

        // OccurredAt alone is not enough: two deviations at one time would come back in load
        // order. SourceEventId breaks that tie, and Id the one left when one event reported
        // several (a BatchFailed with several out-of-spec parameters).
        var orderedDeviations = deviations
            .Select(d => (Row: d, SourceEventId: sourceEventIds.GetValueOrDefault(d.TimelineEventId)))
            .OrderBy(d => d.Row.OccurredAt)
            .ThenBy(d => d.SourceEventId)
            .ThenBy(d => d.Row.Id)
            .Select(d => Deviation(d.Row, d.SourceEventId))
            .ToList();
        var deviationsByCheckpoint = orderedDeviations.ToLookup(d => d.Checkpoint);

        var recorded = StageOrder.Select(c => RecordedTimes(c, payload, eventList)).ToArray();

        // A deviation is data: a checkpoint that reported one is never a gap.
        var hasData = StageOrder
            .Select((c, i) => recorded[i].Count > 0 || deviationsByCheckpoint[c].Any())
            .ToArray();

        var chain = StageOrder.Select((checkpoint, index) =>
        {
            var checkpointDeviations = deviationsByCheckpoint[checkpoint].ToList();
            var status = checkpointDeviations.Count > 0
                ? TraceCheckpointStatus.Deviation
                : BatchRecordAssembler.StateOf(index, hasData, batch.Status) switch
                {
                    CheckpointState.Recorded => TraceCheckpointStatus.WithinSpec,
                    CheckpointState.NotYetReached => TraceCheckpointStatus.NotYetReached,
                    _ => TraceCheckpointStatus.MissingData,
                };

            return new TraceCheckpoint
            {
                Checkpoint = checkpoint,
                Status = status,
                FirstRecordedAt = recorded[index].Count == 0 ? null : recorded[index].Min(),
                LastRecordedAt = recorded[index].Count == 0 ? null : recorded[index].Max(),
                MissingReason = status == TraceCheckpointStatus.MissingData ? MissingReason(checkpoint, snapshot) : null,
                Deviations = checkpointDeviations,
            };
        }).ToList();

        // The first checkpoint in stage order that deviated, not the first deviation by the
        // clock: milk that left the MCC warm is the origin even if Processing's deviation
        // carries an earlier time.
        var origin = chain.FirstOrDefault(c => c.Status == TraceCheckpointStatus.Deviation);

        return new BatchTraceResponse
        {
            BatchId = batch.BatchId,
            DispatchRef = batch.DispatchRef,
            BatchReference = batch.BatchReference,
            ProductLine = batch.ProductLine,
            Facility = batch.Facility,
            Status = batch.Status,
            Completeness = batch.Completeness,
            FirstEventAt = batch.FirstEventAt,
            LastEventAt = batch.LastEventAt,
            LabResult = LabResults.Of(determinations),

            HasDeviation = origin is not null,
            OriginCheckpoint = origin?.Checkpoint,
            Origin = origin?.Deviations[0],
            Chain = chain,
            Holds = Holds(eventList),

            Links = payload is null ? null : BatchRecordAssembler.LinksOf(payload),
            Societies = payload is null
                ? []
                : [.. payload.Tanks
                    .SelectMany(t => t.Consignments)
                    .Select(c => c.Society)
                    .DistinctBy(s => s.Code)
                    .Select(s => new SocietyLinkDto { Code = s.Code, Name = s.Name })],
        };
    }

    // When each checkpoint recorded something. MCC and Intake publish no events, so theirs
    // come from the snapshot, as on the batch record; Mcc or Intake timeline events are not used.
    private static List<DateTime> RecordedTimes(
        Checkpoint checkpoint, UpstreamSnapshotPayload? payload, List<TimelineEvent> events) => checkpoint switch
    {
        Checkpoint.Mcc when payload is not null =>
        [
            .. payload.Tanks.SelectMany(t => t.Consignments).Select(c => c.RegisteredAt),
            payload.Dispatch.DispatchedAt,
        ],
        Checkpoint.Intake when payload is not null => [payload.Intake.ScreenedAt],
        Checkpoint.Mcc or Checkpoint.Intake => [],
        _ => [.. events.Where(e => e.Checkpoint == checkpoint).Select(e => e.OccurredAt)],
    };

    private static string MissingReason(Checkpoint checkpoint, UpstreamSnapshot? snapshot) => checkpoint switch
    {
        Checkpoint.Mcc or Checkpoint.Intake => snapshot switch
        {
            null => "The upstream lookup has not run for this batch yet.",
            { ResolutionStatus: ResolutionStatus.Unavailable } =>
                $"The upstream lookup gave up: {snapshot.FailureReason ?? "no reason recorded"}",
            { ResolutionStatus: ResolutionStatus.PendingRetry, FailureReason: { } reason } =>
                $"The upstream lookup has not resolved yet and will be retried: {reason}",
            { ResolutionStatus: ResolutionStatus.PendingRetry } =>
                "The upstream lookup has not resolved yet and will be retried.",
            // Resolved, but the payload is missing: not written by this service's resolver.
            _ => "The upstream snapshot is resolved but holds no data.",
        },
        _ => $"No {checkpoint} events were received, although the batch is past this checkpoint.",
    };

    private static TraceDeviation Deviation(DeviationRecord d, Guid sourceEventId) => new()
    {
        Checkpoint = d.Checkpoint,
        OccurredAt = d.OccurredAt,
        ReasonCode = d.ReasonCode,
        Parameter = d.Parameter,
        ObservedValue = d.ObservedValue,
        Limit = d.Limit,
        Description = d.Description,
        SourceEventId = sourceEventId,
    };

    // One per HoldId, raised or resolved, whichever arrived. When a hold's event arrived
    // twice under different EventIds, the first by (OccurredAt, SourceEventId) is used.
    private static List<TraceHold> Holds(List<TimelineEvent> events)
    {
        var holdEvents = events
            .Where(e => e.Checkpoint == Checkpoint.Processing && e.EventType is HoldRaised or HoldResolved)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.SourceEventId)
            .Select(e => new HoldEvent(e, HoldBody.Parse(e.Payload)))
            .ToList();

        var open = BatchStatusRules.OpenHolds(new StoredBatchFacts
        {
            HoldsRaised = [.. holdEvents.Where(h => h.IsRaise).Select(h => h.Body.HoldId)],
            HoldsResolved = [.. holdEvents.Where(h => !h.IsRaise).Select(h => h.Body.HoldId)],
        });

        return [.. holdEvents
            .GroupBy(h => h.Body.HoldId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var raise = g.FirstOrDefault(h => h.IsRaise);
                var resolve = g.FirstOrDefault(h => !h.IsRaise);
                return new TraceHold
                {
                    HoldId = g.Key,
                    // A group has at least one event, so without a raise there is a resolve.
                    RaisedAt = raise?.Event.OccurredAt ?? resolve!.Body.RaisedAt ?? resolve!.Event.OccurredAt,
                    RaisedBy = raise?.Event.RecordedBy,
                    Reason = (raise ?? resolve)!.Body.Reason,
                    FailedParameter = raise?.Body.FailedParameter,
                    FailedValue = raise?.Body.FailedValue,
                    IsOpen = open.Contains(g.Key),
                    ResolvedAt = resolve?.Event.OccurredAt,
                    ResolvedBy = resolve?.Event.RecordedBy,
                    Resolution = resolve?.Body.Resolution,
                };
            })
            .OrderBy(h => h.RaisedAt)
            .ThenBy(h => h.HoldId, StringComparer.Ordinal)];
    }

    private sealed record HoldEvent(TimelineEvent Event, HoldBody Body)
    {
        public bool IsRaise => Event.EventType == HoldRaised;
    }

    // The fields of a hold event's stored payload the trace shows. Read by name rather than
    // as the contract type, so a field Processing adds or drops does not fail the trace.
    private sealed record HoldBody(
        string HoldId, string? Reason, string? FailedParameter, string? FailedValue, string? Resolution, DateTime? RaisedAt)
    {
        public static HoldBody Parse(string payload)
        {
            using var body = JsonDocument.Parse(payload);
            var root = body.RootElement;
            return new HoldBody(
                Text(root, "holdId") ?? "",
                Text(root, "reason"),
                Text(root, "failedParameter"),
                Text(root, "failedValue"),
                Text(root, "resolution"),
                root.TryGetProperty("raisedAtUtc", out var at) && at.TryGetDateTime(out var raisedAt) ? AsUtc(raisedAt) : null);
        }

        private static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // A …Utc field without a Z is read back as Unspecified; it is UTC all the same.
        private static DateTime AsUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }
}
