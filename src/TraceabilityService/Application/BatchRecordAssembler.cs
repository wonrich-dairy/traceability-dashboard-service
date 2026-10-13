using System.Text.Json;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// Builds the batch record response from rows already loaded: the batch, its timeline
/// events, its upstream snapshot and its deviations. No database access, so the rules
/// for each checkpoint's state are unit-tested without one.
/// </summary>
public static class BatchRecordAssembler
{
    internal static readonly Checkpoint[] StageOrder =
        [Checkpoint.Mcc, Checkpoint.Intake, Checkpoint.Processing, Checkpoint.Lab];

    // Returns null when there is no batch, which the caller turns into a 404.
    public static BatchRecordDto? Assemble(
        BatchRecord? batch,
        IEnumerable<TimelineEvent> events,
        UpstreamSnapshot? snapshot,
        IEnumerable<DeviationRecord> deviations,
        IEnumerable<LabDetermination> determinations)
    {
        if (batch is null)
        {
            return null;
        }

        var payload = snapshot is { ResolutionStatus: ResolutionStatus.Resolved, Payload: not null }
            ? UpstreamSnapshotPayload.Parse(snapshot.Payload)
            : null;

        // MCC and intake publish no events, so their records come from the snapshot.
        // Any Mcc or Intake timeline events are not shown.
        var eventList = events.ToList();
        List<CheckpointRecordDto>[] records =
        [
            MccRecords(payload),
            IntakeRecords(payload),
            EventRecords(eventList, Checkpoint.Processing),
            EventRecords(eventList, Checkpoint.Lab),
        ];

        var hasData = records.Select(r => r.Count > 0).ToArray();
        var deviationsByCheckpoint = deviations
            .OrderBy(d => d.OccurredAt)
            .ThenBy(d => d.Id)
            .ToLookup(d => d.Checkpoint);

        return new BatchRecordDto
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

            Upstream = snapshot is null ? null : new UpstreamStatusDto
            {
                Status = snapshot.ResolutionStatus,
                AttemptCount = snapshot.AttemptCount,
                LastAttemptAt = snapshot.LastAttemptAt,
                ResolvedAt = snapshot.ResolvedAt,
                FailureReason = snapshot.FailureReason,
            },

            Checkpoints = [.. StageOrder.Select((checkpoint, index) => new CheckpointSectionDto
            {
                Checkpoint = checkpoint,
                State = StateOf(index, hasData, batch.Status),
                FirstRecordedAt = records[index].Count == 0 ? null : records[index].Min(r => r.OccurredAt),
                LastRecordedAt = records[index].Count == 0 ? null : records[index].Max(r => r.OccurredAt),
                Records = records[index],
                Deviations = [.. deviationsByCheckpoint[checkpoint].Select(d => new DeviationDto
                {
                    OccurredAt = d.OccurredAt,
                    ReasonCode = d.ReasonCode,
                    Parameter = d.Parameter,
                    ObservedValue = d.ObservedValue,
                    Limit = d.Limit,
                    Description = d.Description,
                })],
            })],

            Links = payload is null ? null : LinksOf(payload),
        };
    }

    // In the snapshot's order: nothing here sorts tanks, consignments or societies.
    internal static BatchLinksDto LinksOf(UpstreamSnapshotPayload payload) => new()
    {
        Dispatch = new DispatchLinkDto
        {
            Reference = payload.Dispatch.Reference,
            DispatchedAt = payload.Dispatch.DispatchedAt,
            BowserRegistration = payload.Dispatch.BowserRegistration,
            TotalLitres = payload.Dispatch.TotalLitres,
        },
        Tanks = [.. payload.Tanks.Select(t => new TankLinkDto
        {
            Code = t.Code,
            QuantityDrawnLitres = t.QuantityDrawnLitres,
            Consignments = [.. t.Consignments.Select(c => new ConsignmentLinkDto
            {
                Reference = c.Reference,
                VolumeLitres = c.VolumeLitres,
                Society = new SocietyLinkDto { Code = c.Society.Code, Name = c.Society.Name },
            })],
        })],
    };

    /// <summary>
    /// Recorded when the checkpoint has data. Otherwise:
    /// <list type="bullet">
    /// <item>MCC and Intake are always NoDataReceived. A batch only exists once it has passed
    /// factory intake, so it is always past both; whether the snapshot is missing, still
    /// PendingRetry or Unavailable, the gap is real, and the Upstream block says why.</item>
    /// <item>Processing and Lab are NoDataReceived when a later checkpoint has data or the
    /// batch's status is past this point, and NotYetReached when neither.</item>
    /// </list>
    /// The backward trace (SCRUM-134) uses the same rule, so the two endpoints never disagree
    /// on whether a checkpoint is a gap.
    /// </summary>
    /// <param name="index">The checkpoint, as an index into StageOrder.</param>
    /// <param name="hasData">Whether each checkpoint has data, in StageOrder.</param>
    internal static CheckpointState StateOf(
        int index,
        IReadOnlyList<bool> hasData,
        BatchStatus status)
    {
        if (hasData[index])
        {
            return CheckpointState.Recorded;
        }

        if (StageOrder[index] is Checkpoint.Mcc or Checkpoint.Intake)
        {
            return CheckpointState.NoDataReceived;
        }

        var laterHasData = hasData.Skip(index + 1).Any(h => h);
        return laterHasData || index <= LastCheckpointPassed(status)
            ? CheckpointState.NoDataReceived
            : CheckpointState.NotYetReached;
    }

    // The last checkpoint the status says the batch has moved beyond, as an index into
    // StageOrder; only Processing and Lab use it. OnHold counts as in processing: a hold
    // raised later, by the lab, comes with a Lab event, which makes Processing
    // NoDataReceived anyway.
    private static int LastCheckpointPassed(BatchStatus status) => status switch
    {
        BatchStatus.Processing or BatchStatus.OnHold => 1,
        BatchStatus.AwaitingLab => 2,
        BatchStatus.Cleared or BatchStatus.Failed => 3,
        _ => -1,
    };

    private static List<CheckpointRecordDto> MccRecords(UpstreamSnapshotPayload? payload)
    {
        if (payload is null)
        {
            return [];
        }

        var dispatch = payload.Dispatch;
        var consignments = payload.Tanks.SelectMany(t => t.Consignments.Select(c => new CheckpointRecordDto
        {
            Kind = "ConsignmentRegistered",
            OccurredAt = c.RegisteredAt,
            RecordedBy = c.RegisteredBy,
            Data = ToJson(new { c.Reference, c.Society, c.VolumeLitres, TankCode = t.Code, c.Panel }),
        }));

        var dispatched = new CheckpointRecordDto
        {
            Kind = "DispatchCreated",
            OccurredAt = dispatch.DispatchedAt,
            RecordedBy = dispatch.DispatchedBy,
            Data = ToJson(dispatch),
        };

        return [.. consignments.Append(dispatched).OrderBy(r => r.OccurredAt)];
    }

    private static List<CheckpointRecordDto> IntakeRecords(UpstreamSnapshotPayload? payload)
    {
        if (payload is null)
        {
            return [];
        }

        var intake = payload.Intake;
        return
        [
            new CheckpointRecordDto
            {
                Kind = "ArrivalScreened",
                OccurredAt = intake.ScreenedAt,
                RecordedBy = intake.ScreenedBy,
                Data = ToJson(new { payload.BatchReference, intake.ArrivedAt, intake.ScreenedAt, intake.TemperatureC }),
            },
        ];
    }

    // In the order things happened. ThenBy(Id) keeps two events sharing a timestamp in the
    // order they were stored rather than whatever order they were loaded in.
    private static List<CheckpointRecordDto> EventRecords(IEnumerable<TimelineEvent> events, Checkpoint checkpoint) =>
    [
        .. events
            .Where(e => e.Checkpoint == checkpoint)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Select(e => new CheckpointRecordDto
            {
                Kind = e.EventType,
                OccurredAt = e.OccurredAt,
                RecordedBy = e.RecordedBy,
                Data = JsonSerializer.Deserialize<JsonElement>(e.Payload),
                SourceEventId = e.SourceEventId,
            }),
    ];

    private static JsonElement ToJson(object value) =>
        JsonSerializer.SerializeToElement(value, UpstreamSnapshotPayload.JsonOptions);
}
