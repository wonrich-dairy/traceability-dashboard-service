using System.Text.Json;
using System.Text.Json.Serialization;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;
using static TraceabilityService.Domain.Entities.UpstreamSnapshotPayload;

namespace TraceabilityService.UnitTests;

/// <summary>
/// BackwardTraceAssembler builds GET /api/batches/{batchId}/trace from rows already loaded,
/// so these run without a database. The first four groups are the Definition of Done's
/// cases: first-deviation selection, no deviation, missing data and determinism.
/// </summary>
public class BackwardTraceAssemblerTests
{
    private static readonly DateTime Dispatched = new(2026, 9, 19, 5, 40, 0, DateTimeKind.Utc);

    // As the API writes it (Program.cs): camelCase, enums as names.
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // Each checkpoint's event, so every deviation has a timeline event to point at.
    private static readonly TimelineEvent MccEvent = Event(1, Checkpoint.Mcc, "MccDispatchCreated", Dispatched);
    private static readonly TimelineEvent ProcessingEvent = Event(2, Checkpoint.Processing, "ProcessingStageRecorded", Dispatched.AddHours(4));
    private static readonly TimelineEvent LabEvent = Event(3, Checkpoint.Lab, "BatchFailed", Dispatched.AddHours(6));

    // ── First-deviation selection ────────────────────────────────────────────

    [Fact]
    public void Deviations_at_the_lab_and_the_mcc_give_the_mcc_as_the_origin()
    {
        var trace = Assemble(BatchStatus.Failed, [MccEvent, LabEvent],
        [
            Deviation(1, LabEvent, "LOW_PH", "Ph", 6.32m, 6.60m),
            Deviation(2, MccEvent, "TemperatureHigh", "TemperatureC", 12.40m, 4.00m),
        ]);

        Assert.True(trace.HasDeviation);
        Assert.Equal(Checkpoint.Mcc, trace.OriginCheckpoint);
        Assert.NotNull(trace.Origin);
        Assert.Equal("TemperatureC", trace.Origin.Parameter);
        Assert.Equal(12.40m, trace.Origin.ObservedValue);
        Assert.Equal(4.00m, trace.Origin.Limit);
        Assert.Equal(Dispatched, trace.Origin.OccurredAt);
        Assert.Equal(MccEvent.SourceEventId, trace.Origin.SourceEventId);
    }

    [Fact]
    public void Deviations_at_the_lab_and_processing_give_processing_as_the_origin()
    {
        var trace = Assemble(BatchStatus.Failed, [ProcessingEvent, LabEvent],
        [
            Deviation(1, LabEvent, "LOW_FAT", "FatPercent", 2.80m, 3.00m),
            Deviation(2, ProcessingEvent, null, "EndTemperatureC", 78.00m, 72.00m),
        ]);

        Assert.Equal(Checkpoint.Processing, trace.OriginCheckpoint);
        Assert.Equal("EndTemperatureC", trace.Origin?.Parameter);
    }

    [Fact]
    public void A_single_deviation_is_the_origin()
    {
        var trace = Assemble(BatchStatus.Failed, [LabEvent], [Deviation(1, LabEvent, "LOW_PH", "Ph", 6.32m, 6.60m)]);

        Assert.Equal(Checkpoint.Lab, trace.OriginCheckpoint);
        Assert.Equal("LOW_PH", trace.Origin?.ReasonCode);
    }

    // The origin follows the stage order, not the clock: an MCC deviation stays the origin
    // even when a later checkpoint's deviation carries an earlier time (skewed clocks).
    [Fact]
    public void The_origin_is_the_first_checkpoint_in_stage_order_not_the_earliest_deviation()
    {
        var earlyProcessing = Event(4, Checkpoint.Processing, "ProcessingStageRecorded", Dispatched.AddHours(-1));
        var trace = Assemble(BatchStatus.Failed, [MccEvent, earlyProcessing],
        [
            Deviation(1, earlyProcessing, null, "FatPercent", 2.80m, 3.00m),
            Deviation(2, MccEvent, "TemperatureHigh", "TemperatureC", 12.40m, 4.00m),
        ]);

        Assert.Equal(Checkpoint.Mcc, trace.OriginCheckpoint);
        Assert.Equal(TraceCheckpointStatus.Deviation, Link(trace, Checkpoint.Processing).Status);
    }

    // ── No deviation ─────────────────────────────────────────────────────────

    [Fact]
    public void A_clean_batch_has_no_origin_and_four_checkpoints_within_spec()
    {
        var trace = Assemble(BatchStatus.Cleared, [ProcessingEvent, Event(3, Checkpoint.Lab, "BatchCleared", Dispatched.AddHours(6))], []);

        Assert.False(trace.HasDeviation);
        Assert.Null(trace.OriginCheckpoint);
        Assert.Null(trace.Origin);
        Assert.Equal(
            [Checkpoint.Mcc, Checkpoint.Intake, Checkpoint.Processing, Checkpoint.Lab],
            trace.Chain.Select(c => c.Checkpoint));
        Assert.All(trace.Chain, c => Assert.Equal(TraceCheckpointStatus.WithinSpec, c.Status));
        Assert.All(trace.Chain, c => Assert.Empty(c.Deviations));
        Assert.All(trace.Chain, c => Assert.Null(c.MissingReason));
    }

    // ── Missing data ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]   // no snapshot row yet
    [InlineData(ResolutionStatus.PendingRetry)]
    [InlineData(ResolutionStatus.Unavailable)]
    public void Without_a_resolved_snapshot_mcc_and_intake_are_missing_data_and_still_in_the_chain(ResolutionStatus? status)
    {
        var snapshot = status is null
            ? null
            : new UpstreamSnapshot { BatchId = "262-FM-B", ResolutionStatus = status.Value, FailureReason = "MCC returned 503" };

        var trace = Assemble(BatchStatus.Failed, [ProcessingEvent, LabEvent], [], snapshot: snapshot, resolved: false);

        Assert.Equal(4, trace.Chain.Count);
        Assert.Equal(
            [TraceCheckpointStatus.MissingData, TraceCheckpointStatus.MissingData, TraceCheckpointStatus.WithinSpec, TraceCheckpointStatus.WithinSpec],
            trace.Chain.Select(c => c.Status));
        Assert.NotNull(Link(trace, Checkpoint.Mcc).MissingReason);
        Assert.Null(trace.Links);
        Assert.Empty(trace.Societies);
    }

    // "Never omitted" is about the JSON: a serializer that drops nulls would pass a test
    // that only checks values, so this checks the keys are there.
    [Fact]
    public void Missing_checkpoints_and_an_absent_origin_are_written_not_left_out()
    {
        var trace = Assemble(BatchStatus.Failed, [ProcessingEvent, LabEvent], [], snapshot: null, resolved: false);

        // Even with nulls dropped by default, the trace's nullable fields are still written.
        var dropNulls = new JsonSerializerOptions(ApiJson) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(trace, dropNulls));
        var root = json.RootElement;

        Assert.True(root.TryGetProperty("originCheckpoint", out var originCheckpoint));
        Assert.Equal(JsonValueKind.Null, originCheckpoint.ValueKind);
        Assert.True(root.TryGetProperty("origin", out _));
        Assert.True(root.TryGetProperty("links", out _));

        var chain = root.GetProperty("chain").EnumerateArray().ToList();
        Assert.Equal(["Mcc", "Intake", "Processing", "Lab"], chain.Select(c => c.GetProperty("checkpoint").GetString()));
        foreach (var missing in chain.Take(2))
        {
            Assert.Equal("MissingData", missing.GetProperty("status").GetString());
            Assert.True(missing.TryGetProperty("firstRecordedAt", out var first));
            Assert.Equal(JsonValueKind.Null, first.ValueKind);
            Assert.True(missing.TryGetProperty("lastRecordedAt", out _));
            Assert.Equal(JsonValueKind.String, missing.GetProperty("missingReason").ValueKind);
            Assert.Equal(JsonValueKind.Array, missing.GetProperty("deviations").ValueKind);
        }
    }

    [Fact]
    public void A_batch_still_in_processing_has_not_yet_reached_the_lab()
    {
        var trace = Assemble(BatchStatus.Processing, [ProcessingEvent], []);

        Assert.Equal(
            [TraceCheckpointStatus.WithinSpec, TraceCheckpointStatus.WithinSpec, TraceCheckpointStatus.WithinSpec, TraceCheckpointStatus.NotYetReached],
            trace.Chain.Select(c => c.Status));
        Assert.Null(Link(trace, Checkpoint.Lab).MissingReason);
    }

    [Fact]
    public void A_lab_result_without_processing_events_makes_processing_missing_data()
    {
        var trace = Assemble(BatchStatus.Failed, [LabEvent], []);

        Assert.Equal(TraceCheckpointStatus.MissingData, Link(trace, Checkpoint.Processing).Status);
        Assert.Contains("Processing", Link(trace, Checkpoint.Processing).MissingReason);
    }

    // ── Determinism ──────────────────────────────────────────────────────────

    [Fact]
    public void The_same_input_gives_the_same_trace_byte_for_byte()
    {
        var events = new[] { MccEvent, ProcessingEvent, LabEvent, HoldRaisedEvent(5, "HOLD-1", 30), HoldResolvedEvent(6, "HOLD-1", 90) };
        var deviations = new[]
        {
            Deviation(1, LabEvent, "LOW_PH", "Ph", 6.32m, 6.60m),
            Deviation(2, LabEvent, "LOW_FAT", "FatPercent", 2.80m, 3.00m),
            Deviation(3, MccEvent, "TemperatureHigh", "TemperatureC", 12.40m, 4.00m),
        };

        var first = JsonSerializer.Serialize(Assemble(BatchStatus.Failed, events, deviations), ApiJson);
        var second = JsonSerializer.Serialize(Assemble(BatchStatus.Failed, events, deviations), ApiJson);
        // The database returns rows in no promised order: the trace must not depend on it.
        var reversed = JsonSerializer.Serialize(
            Assemble(BatchStatus.Failed, events.Reverse(), deviations.Reverse()), ApiJson);

        Assert.Equal(first, second);
        Assert.Equal(first, reversed);
    }

    [Fact]
    public void Deviations_at_the_same_time_are_ordered_by_source_event_id_whatever_order_they_load_in()
    {
        var at = Dispatched.AddHours(4);
        var low = Event(10, Checkpoint.Processing, "ProcessingStageRecorded", at, new Guid("00000000-0000-0000-0000-000000000001"));
        var high = Event(11, Checkpoint.Processing, "ProcessingStageRecorded", at, new Guid("ffffffff-0000-0000-0000-000000000000"));
        // The higher Id on the lower SourceEventId, so ordering by Id would give the other answer.
        var deviations = new[]
        {
            Deviation(1, high, null, "EndTemperatureC", 80.00m, 72.00m),
            Deviation(2, low, null, "EndTemperatureC", 78.00m, 72.00m),
        };

        foreach (var input in new[] { deviations, deviations.Reverse().ToArray() })
        {
            var trace = Assemble(BatchStatus.Failed, [low, high], input);

            Assert.Equal(
                [low.SourceEventId, high.SourceEventId],
                Link(trace, Checkpoint.Processing).Deviations.Select(d => d.SourceEventId));
            Assert.Equal(low.SourceEventId, trace.Origin?.SourceEventId);
        }
    }

    // ── Upstream, holds, lab result ──────────────────────────────────────────

    [Fact]
    public void Tanks_and_societies_keep_the_snapshots_order()
    {
        var trace = Assemble(BatchStatus.Failed, [ProcessingEvent, LabEvent], []);

        Assert.NotNull(trace.Links);
        Assert.Equal(["T2", "T1"], trace.Links.Tanks.Select(t => t.Code));
        Assert.Equal([350m, 560m], trace.Links.Tanks.Select(t => t.QuantityDrawnLitres));
        // Not alphabetical, and VS045, in both tanks, is listed once where it first appears.
        Assert.Equal(["VS045", "VS009", "VS022"], trace.Societies.Select(s => s.Code));
        Assert.Equal("Society VS045", trace.Societies[0].Name);
    }

    [Fact]
    public void Mcc_and_intake_times_come_from_the_snapshot()
    {
        var trace = Assemble(BatchStatus.Failed, [ProcessingEvent, LabEvent], []);

        Assert.Equal(Dispatched.AddMinutes(-40), Link(trace, Checkpoint.Mcc).FirstRecordedAt);
        Assert.Equal(Dispatched, Link(trace, Checkpoint.Mcc).LastRecordedAt);
        Assert.Equal(Dispatched.AddMinutes(145), Link(trace, Checkpoint.Intake).FirstRecordedAt);
    }

    [Fact]
    public void Holds_are_listed_in_the_order_raised_with_open_ones_marked()
    {
        var events = new[]
        {
            ProcessingEvent,
            HoldRaisedEvent(5, "HOLD-2", 120),
            HoldRaisedEvent(6, "HOLD-1", 30),
            HoldResolvedEvent(7, "HOLD-1", 90),
        };

        var trace = Assemble(BatchStatus.OnHold, events, []);

        Assert.Equal(["HOLD-1", "HOLD-2"], trace.Holds.Select(h => h.HoldId));
        var resolved = trace.Holds[0];
        Assert.False(resolved.IsOpen);
        Assert.Equal(Dispatched.AddMinutes(30), resolved.RaisedAt);
        Assert.Equal(Dispatched.AddMinutes(90), resolved.ResolvedAt);
        Assert.Equal("qa-1", resolved.RaisedBy);
        Assert.Equal("qa-2", resolved.ResolvedBy);
        Assert.Equal("Released", resolved.Resolution);
        Assert.Equal("FatPercent", resolved.FailedParameter);
        Assert.Equal("2.8 < 3.0 min", resolved.FailedValue);

        Assert.True(trace.Holds[1].IsOpen);
        Assert.Null(trace.Holds[1].ResolvedAt);
    }

    [Fact]
    public void A_hold_resolved_before_its_raise_arrived_is_listed_closed()
    {
        var trace = Assemble(BatchStatus.Processing, [ProcessingEvent, HoldResolvedEvent(5, "HOLD-9", 90)], []);

        var hold = Assert.Single(trace.Holds);
        Assert.False(hold.IsOpen);
        // The resolve carries when the hold was raised.
        Assert.Equal(Dispatched.AddMinutes(80), hold.RaisedAt);
    }

    [Fact]
    public void The_status_is_the_stored_one_and_the_lab_result_the_active_determinations()
    {
        var determinations = new[]
        {
            new LabDetermination
            {
                DeterminationId = Guid.NewGuid(), BatchId = "262-FM-B", Result = DeterminationResult.Failed,
                DeterminedAt = Dispatched.AddHours(6), DeterminedBy = "analyst-1",
            },
        };

        var trace = BackwardTraceAssembler.Assemble(
            Batch(BatchStatus.Failed), [LabEvent], [], determinations, ResolvedSnapshot());

        Assert.NotNull(trace);
        Assert.Equal(BatchStatus.Failed, trace.Status);
        Assert.Equal(LabResult.Failed, trace.LabResult);
    }

    [Fact]
    public void Not_found_no_batch_gives_no_trace()
    {
        Assert.Null(BackwardTraceAssembler.Assemble(null, [], [], [], null));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static BatchTraceResponse Assemble(
        BatchStatus status,
        IEnumerable<TimelineEvent> events,
        IEnumerable<DeviationRecord> deviations,
        UpstreamSnapshot? snapshot = null,
        bool resolved = true)
    {
        var trace = BackwardTraceAssembler.Assemble(
            Batch(status), events, deviations, [], resolved ? ResolvedSnapshot() : snapshot);
        Assert.NotNull(trace);
        return trace;
    }

    private static TraceCheckpoint Link(BatchTraceResponse trace, Checkpoint checkpoint) =>
        trace.Chain.Single(c => c.Checkpoint == checkpoint);

    private static BatchRecord Batch(BatchStatus status) => new()
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
    };

    private static TimelineEvent Event(
        long id, Checkpoint checkpoint, string eventType, DateTime occurredAt, Guid? sourceEventId = null, string payload = "{}") => new()
    {
        Id = id,
        BatchId = "262-FM-B",
        Checkpoint = checkpoint,
        EventType = eventType,
        OccurredAt = occurredAt,
        RecordedBy = "processing.tech",
        Payload = payload,
        SourceEventId = sourceEventId ?? Guid.NewGuid(),
        ContractVersion = "v1"
    };

    private static TimelineEvent HoldRaisedEvent(long id, string holdId, int minutes)
    {
        var e = Event(id, Checkpoint.Processing, BackwardTraceAssembler.HoldRaised, Dispatched.AddMinutes(minutes), payload: $$"""
            {"holdId": "{{holdId}}", "reason": "Failed: FatPercent 2.8 < 3.0 min",
             "failedParameter": "FatPercent", "failedValue": "2.8 < 3.0 min", "isDeviation": true}
            """);
        e.RecordedBy = "qa-1";
        return e;
    }

    private static TimelineEvent HoldResolvedEvent(long id, string holdId, int minutes)
    {
        var raisedAt = Dispatched.AddMinutes(minutes - 10).ToString("O");
        var e = Event(id, Checkpoint.Processing, BackwardTraceAssembler.HoldResolved, Dispatched.AddMinutes(minutes), payload: $$"""
            {"holdId": "{{holdId}}", "raisedAtUtc": "{{raisedAt}}", "reason": "Previous hold", "resolution": "Released"}
            """);
        e.RecordedBy = "qa-2";
        return e;
    }

    private static DeviationRecord Deviation(
        int id, TimelineEvent reportedBy, string? reason, string parameter, decimal observed, decimal limit) => new()
    {
        Id = id,
        BatchId = "262-FM-B",
        TimelineEventId = reportedBy.Id,
        Checkpoint = reportedBy.Checkpoint,
        OccurredAt = reportedBy.OccurredAt,
        ReasonCode = reason,
        Parameter = parameter,
        ObservedValue = observed,
        Limit = limit
    };

    // T2 before T1 and societies out of alphabetical order, so any sorting shows.
    private static UpstreamSnapshot ResolvedSnapshot() => new()
    {
        BatchId = "262-FM-B",
        ResolutionStatus = ResolutionStatus.Resolved,
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
                ArrivedAt = Dispatched.AddMinutes(140),
                ScreenedAt = Dispatched.AddMinutes(145),
                ScreenedBy = "intake.officer",
                TemperatureC = 9.80m
            },
            Tanks =
            [
                new TankPart
                {
                    Code = "T2",
                    QuantityDrawnLitres = 350m,
                    Consignments =
                    [
                        Consignment("VS045", "A", 200m, Dispatched.AddMinutes(-40)),
                        Consignment("VS009", "A", 150m, Dispatched.AddMinutes(-35)),
                    ]
                },
                new TankPart
                {
                    Code = "T1",
                    QuantityDrawnLitres = 560m,
                    Consignments =
                    [
                        Consignment("VS022", "B", 300m, Dispatched.AddMinutes(-30)),
                        Consignment("VS045", "B", 260m, Dispatched.AddMinutes(-25)),
                    ]
                },
            ]
        })
    };

    private static ConsignmentPart Consignment(string society, string suffix, decimal litres, DateTime registeredAt) => new()
    {
        Reference = $"CON-20260919-{society}-{suffix}",
        Society = new SocietyPart { Code = society, Name = $"Society {society}" },
        VolumeLitres = litres,
        RegisteredAt = registeredAt,
        RegisteredBy = $"{society.ToLowerInvariant()}.officer"
    };
}
