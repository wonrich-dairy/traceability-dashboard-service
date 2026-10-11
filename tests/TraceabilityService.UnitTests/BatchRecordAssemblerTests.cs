using TraceabilityService.Api.Contracts;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;
using static TraceabilityService.Domain.Entities.UpstreamSnapshotPayload;

namespace TraceabilityService.UnitTests;

/// <summary>
/// BatchRecordAssembler builds GET /api/batches/{batchId} from rows already loaded, so
/// these run without a database. The first four are the Definition of Done's cases:
/// found, partial, missing and not found.
/// </summary>
public class BatchRecordAssemblerTests
{
    private static readonly DateTime Dispatched = new(2026, 9, 19, 5, 40, 0, DateTimeKind.Utc);

    [Fact]
    public void Found_every_checkpoint_is_recorded_with_its_records_deviations_and_links()
    {
        var events = new[]
        {
            Event(1, Checkpoint.Processing, "ProcessingCompleted", Dispatched.AddHours(4)),
            Event(2, Checkpoint.Lab, "BatchFailed", Dispatched.AddHours(6)),
        };
        var deviations = new[]
        {
            Deviation(1, Checkpoint.Lab, Dispatched.AddHours(6), "PhLow", 6.32m, 6.60m),
            Deviation(2, Checkpoint.Mcc, Dispatched, "TemperatureHigh", 12.40m, 4.00m),
        };

        var record = Assemble(BatchStatus.Failed, events, ResolvedSnapshot(), deviations);

        Assert.NotNull(record);
        Assert.Equal("262-FM-B", record.BatchId);
        Assert.Equal("WR-20260919-02", record.BatchReference);
        Assert.Equal(
            [Checkpoint.Mcc, Checkpoint.Intake, Checkpoint.Processing, Checkpoint.Lab],
            record.Checkpoints.Select(c => c.Checkpoint));
        Assert.All(record.Checkpoints, c => Assert.Equal(CheckpointState.Recorded, c.State));

        // MCC: each consignment as registered, then the dispatch, in time order.
        var mcc = Section(record, Checkpoint.Mcc);
        Assert.Equal(
            ["ConsignmentRegistered", "ConsignmentRegistered", "ConsignmentRegistered", "DispatchCreated"],
            mcc.Records.Select(r => r.Kind));
        Assert.Equal(["vs009.officer", "vs045.officer", "vs022.officer", "mcc.manager"], mcc.Records.Select(r => r.RecordedBy));
        Assert.Equal(Dispatched.AddMinutes(-40), mcc.FirstRecordedAt);
        Assert.Equal(Dispatched, mcc.LastRecordedAt);
        Assert.Equal("CON-20260919-VS009", mcc.Records[0].Data.GetProperty("reference").GetString());
        Assert.Equal(4.03m, mcc.Records[0].Data.GetProperty("panel").GetProperty("fatPercentage").GetDecimal());

        var intake = Assert.Single(Section(record, Checkpoint.Intake).Records);
        Assert.Equal("ArrivalScreened", intake.Kind);
        Assert.Equal("intake.officer", intake.RecordedBy);
        Assert.Equal(9.80m, intake.Data.GetProperty("temperatureC").GetDecimal());

        Assert.Equal("ProcessingCompleted", Assert.Single(Section(record, Checkpoint.Processing).Records).Kind);
        Assert.Equal("BatchFailed", Assert.Single(Section(record, Checkpoint.Lab).Records).Kind);

        // Deviations land in the section of the checkpoint that reported them.
        Assert.Equal("TemperatureHigh", Assert.Single(mcc.Deviations).ReasonCode);
        Assert.Equal(6.60m, Assert.Single(Section(record, Checkpoint.Lab).Deviations).Limit);
        Assert.Empty(Section(record, Checkpoint.Processing).Deviations);

        // Links: dispatch -> tanks -> consignments -> societies.
        Assert.NotNull(record.Links);
        Assert.Equal("DN-20260919-02", record.Links.Dispatch.Reference);
        Assert.Equal(["T1", "T2"], record.Links.Tanks.Select(t => t.Code));
        Assert.Equal([560m, 350m], record.Links.Tanks.Select(t => t.QuantityDrawnLitres));
        Assert.Equal(
            ["VS009", "VS045", "VS022"],
            record.Links.Tanks.SelectMany(t => t.Consignments).Select(c => c.Society.Code));

        Assert.NotNull(record.Upstream);
        Assert.Equal(ResolutionStatus.Resolved, record.Upstream.Status);
    }

    [Fact]
    public void Partial_a_batch_still_in_processing_has_not_yet_reached_processing_or_the_lab()
    {
        var record = Assemble(BatchStatus.Processing, [], ResolvedSnapshot(), []);

        Assert.NotNull(record);
        Assert.Equal(
            [CheckpointState.Recorded, CheckpointState.Recorded, CheckpointState.NotYetReached, CheckpointState.NotYetReached],
            record.Checkpoints.Select(c => c.State));
        Assert.Empty(Section(record, Checkpoint.Lab).Records);
        Assert.Null(Section(record, Checkpoint.Lab).FirstRecordedAt);
    }

    [Fact]
    public void Missing_intake_has_no_data_received_when_the_upstream_lookup_gave_up()
    {
        // The lab has decided, but the upstream lookup gave up and Processing never reported.
        var events = new[] { Event(1, Checkpoint.Lab, "BatchFailed", Dispatched.AddHours(6)) };
        var snapshot = new UpstreamSnapshot
        {
            BatchId = "262-FM-B",
            ResolutionStatus = ResolutionStatus.Unavailable,
            AttemptCount = 1,
            FailureReason = "MCC and Intake Service has no dispatch note DN-20260919-02"
        };

        var record = Assemble(BatchStatus.Failed, events, snapshot, []);

        Assert.NotNull(record);
        Assert.Equal(
            [CheckpointState.NoDataReceived, CheckpointState.NoDataReceived, CheckpointState.NoDataReceived, CheckpointState.Recorded],
            record.Checkpoints.Select(c => c.State));
        Assert.Null(record.Links);
        Assert.NotNull(record.Upstream);
        Assert.Equal(ResolutionStatus.Unavailable, record.Upstream.Status);
        Assert.Contains("no dispatch note", record.Upstream.FailureReason);
    }

    [Fact]
    public void Not_found_no_batch_gives_no_record()
    {
        Assert.Null(BatchRecordAssembler.Assemble(null, [], null, []));
    }

    // A batch only exists once it has passed factory intake, so MCC and Intake without data
    // are a gap whatever the snapshot is doing, even before any later checkpoint reports.
    // The Upstream block is what says why.
    [Theory]
    [InlineData(null)]   // no snapshot row yet
    [InlineData(ResolutionStatus.PendingRetry)]
    [InlineData(ResolutionStatus.Unavailable)]
    public void Mcc_and_intake_without_a_resolved_snapshot_are_no_data_received_never_not_yet_reached(
        ResolutionStatus? status)
    {
        var snapshot = status is null ? null : new UpstreamSnapshot { BatchId = "262-FM-B", ResolutionStatus = status.Value };

        var record = Assemble(BatchStatus.Processing, [], snapshot, []);

        Assert.NotNull(record);
        Assert.Equal(
            [CheckpointState.NoDataReceived, CheckpointState.NoDataReceived, CheckpointState.NotYetReached, CheckpointState.NotYetReached],
            record.Checkpoints.Select(c => c.State));
        Assert.Equal(status, record.Upstream?.Status);
    }

    [Theory]
    [InlineData(BatchStatus.Chilling, CheckpointState.NotYetReached, CheckpointState.NotYetReached)]
    [InlineData(BatchStatus.Processing, CheckpointState.NotYetReached, CheckpointState.NotYetReached)]
    [InlineData(BatchStatus.OnHold, CheckpointState.NotYetReached, CheckpointState.NotYetReached)]
    [InlineData(BatchStatus.AwaitingLab, CheckpointState.NoDataReceived, CheckpointState.NotYetReached)]
    [InlineData(BatchStatus.Cleared, CheckpointState.NoDataReceived, CheckpointState.NoDataReceived)]
    [InlineData(BatchStatus.Failed, CheckpointState.NoDataReceived, CheckpointState.NoDataReceived)]
    public void A_status_past_a_checkpoint_with_no_data_is_no_data_received(
        BatchStatus status, CheckpointState processing, CheckpointState lab)
    {
        var record = Assemble(status, [], ResolvedSnapshot(), []);

        Assert.NotNull(record);
        Assert.Equal(processing, Section(record, Checkpoint.Processing).State);
        Assert.Equal(lab, Section(record, Checkpoint.Lab).State);
    }

    [Fact]
    public void Records_are_ordered_by_occurred_at_then_id()
    {
        var at = Dispatched.AddHours(4);
        var events = new[]
        {
            Event(7, Checkpoint.Processing, "ProcessingStageRecorded", at),
            Event(3, Checkpoint.Processing, "ProcessingStageRecorded", at),
            Event(9, Checkpoint.Processing, "MilkAllocatedToMixingTank", at.AddMinutes(-30)),
        };

        var record = Assemble(BatchStatus.AwaitingLab, events, ResolvedSnapshot(), []);

        Assert.NotNull(record);
        Assert.Equal(
            [events[2].SourceEventId, events[1].SourceEventId, events[0].SourceEventId],
            Section(record, Checkpoint.Processing).Records.Select(r => r.SourceEventId!.Value));
    }

    [Fact]
    public void Mcc_and_intake_records_come_from_the_snapshot_not_the_timeline()
    {
        var events = new[] { Event(1, Checkpoint.Mcc, "MccDispatchCreated", Dispatched) };

        var record = Assemble(BatchStatus.Processing, events, snapshot: null, []);

        Assert.NotNull(record);
        Assert.Empty(Section(record, Checkpoint.Mcc).Records);
        Assert.Equal(CheckpointState.NoDataReceived, Section(record, Checkpoint.Mcc).State);
    }

    private static BatchRecordDto? Assemble(
        BatchStatus status,
        IEnumerable<TimelineEvent> events,
        UpstreamSnapshot? snapshot,
        IEnumerable<DeviationRecord> deviations) =>
        BatchRecordAssembler.Assemble(
            new BatchRecord
            {
                BatchId = "262-FM-B",
                DispatchRef = "DN-20260919-02",
                BatchReference = "WR-20260919-02",
                ProductLine = "FM",
                Facility = "FACTORY-01",
                Status = status,
                Completeness = TraceCompleteness.Partial,
                FirstEventAt = Dispatched,
                LastEventAt = Dispatched
            },
            events, snapshot, deviations);

    private static CheckpointSectionDto Section(BatchRecordDto record, Checkpoint checkpoint) =>
        record.Checkpoints.Single(c => c.Checkpoint == checkpoint);

    private static UpstreamSnapshot ResolvedSnapshot() => new()
    {
        BatchId = "262-FM-B",
        ResolutionStatus = ResolutionStatus.Resolved,
        AttemptCount = 1,
        ResolvedAt = Dispatched.AddMinutes(1),
        Payload = Serialize(new UpstreamSnapshotPayload
        {
            BatchReference = "WR-20260919-02",
            Dispatch = new DispatchPart
            {
                Reference = "DN-20260919-02",
                DispatchedAt = Dispatched,
                DispatchedBy = "mcc.manager",
                TotalLitres = 910m
            },
            Intake = new IntakePart
            {
                ArrivedAt = Dispatched.AddMinutes(145),
                ScreenedAt = Dispatched.AddMinutes(145),
                ScreenedBy = "intake.officer",
                TemperatureC = 9.80m
            },
            Tanks =
            [
                new TankPart
                {
                    Code = "T1",
                    QuantityDrawnLitres = 560m,
                    Consignments =
                    [
                        Consignment("VS009", 300m, Dispatched.AddMinutes(-40), fat: 4.03m),
                        Consignment("VS045", 260m, Dispatched.AddMinutes(-35), fat: 3.87m),
                    ]
                },
                new TankPart
                {
                    Code = "T2",
                    QuantityDrawnLitres = 350m,
                    Consignments = [Consignment("VS022", 350m, Dispatched.AddMinutes(-30), fat: 3.95m)]
                },
            ]
        })
    };

    private static ConsignmentPart Consignment(string society, decimal litres, DateTime registeredAt, decimal fat) => new()
    {
        Reference = $"CON-20260919-{society}",
        Society = new SocietyPart { Code = society, Name = $"Society {society}" },
        VolumeLitres = litres,
        RegisteredAt = registeredAt,
        RegisteredBy = $"{society.ToLowerInvariant()}.officer",
        Panel = new PanelPart
        {
            FatPercentage = fat,
            SnfPercentage = 8.52m,
            ClrReading = 27.80m,
            TemperatureC = 4.40m,
            Verdict = "Accepted"
        }
    };

    private static TimelineEvent Event(long id, Checkpoint checkpoint, string eventType, DateTime occurredAt) => new()
    {
        Id = id,
        BatchId = "262-FM-B",
        Checkpoint = checkpoint,
        EventType = eventType,
        OccurredAt = occurredAt,
        RecordedBy = "processing.tech",
        Payload = "{}",
        SourceEventId = Guid.NewGuid(),
        ContractVersion = "v1"
    };

    private static DeviationRecord Deviation(
        int id, Checkpoint checkpoint, DateTime occurredAt, string reason, decimal observed, decimal limit) => new()
    {
        Id = id,
        BatchId = "262-FM-B",
        Checkpoint = checkpoint,
        OccurredAt = occurredAt,
        ReasonCode = reason,
        ObservedValue = observed,
        Limit = limit
    };
}
