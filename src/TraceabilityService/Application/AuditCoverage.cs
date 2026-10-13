using TraceabilityService.Api.Contracts;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// Action names in the consolidated audit trail (SCRUM-138) that are not an upstream event's own.
/// </summary>
public static class AuditActions
{
    // Read from the cached upstream snapshot.
    public const string ConsignmentRegistered = "ConsignmentRegistered";
    public const string MilkDispatched = "MilkDispatched";
    public const string FactoryIntakeScreened = "FactoryIntakeScreened";

    // Traceability's own, written to audit_entries.
    public const string BatchRecordOpened = "BatchRecordOpened";
    public const string BatchTraceOpened = "BatchTraceOpened";
    public const string BatchSearchExported = BatchSearchService.ExportAction;

    // Administrative operations (SCRUM-140)
    public const string DeadLetterReplayed = "DeadLetterReplayed";
    public const string ProjectionRebuilt = "ProjectionRebuilt";
}

/// <summary>
/// Which actions the consolidated audit trail covers and which it does not (SCRUM-138, SCRUM-140). The trail
/// is built from the events Traceability consumes, the upstream snapshot it caches and its own
/// audit_entries, so it covers exactly the actions those carry; anything done upstream without an
/// event is not in it. Served by GET /api/audit/coverage and kept in step with docs/audit-trail.md.
/// </summary>
public static class AuditCoverage
{
    public const string Documentation = "docs/audit-trail.md";

    public static readonly IReadOnlyList<CoveredActionDto> Covered =
    [
        Event("MilkAllocatedToMixingTank", Checkpoint.Processing,
            "Milk from a dispatch allocated to a mixing tank. User: who allocated it; time: when."),
        Event("ProcessingStageRecorded", Checkpoint.Processing,
            "A processing stage recorded, with its readings. User: who recorded it; time: when the stage started."),
        Event("ProcessingCompleted", Checkpoint.Processing,
            "Processing of the batch completed. User: who completed it; time: when."),
        Event("ProcessingHoldRaised", Checkpoint.Processing,
            "A hold raised on the batch. User: who raised it; time: when."),
        Event("ProcessingHoldResolved", Checkpoint.Processing,
            "A hold resolved. User: who resolved it; time: when."),
        Event("BatchCleared", Checkpoint.Lab,
            "Quality Lab cleared the batch. User: the analyst who determined it; time: when. A determination that supersedes an earlier one is a further entry; the earlier one stays."),
        Event("BatchFailed", Checkpoint.Lab,
            "Quality Lab failed the batch. User: the analyst who determined it; time: when. Superseding works as for BatchCleared."),
        Snapshot(AuditActions.ConsignmentRegistered, Checkpoint.Mcc,
            "A consignment registered at the MCC gate, with its quality panel. User: who registered it; time: when."),
        Snapshot(AuditActions.MilkDispatched, Checkpoint.Mcc,
            "The dispatch note the batch's milk left the MCC on. User: who dispatched it; time: when."),
        Snapshot(AuditActions.FactoryIntakeScreened, Checkpoint.Intake,
            "The dispatch screened at factory intake. User: who screened it; time: when."),
        Own(AuditActions.BatchRecordOpened,
            "A batch record opened in Traceability (GET /api/batches/{batchId}). User: the caller; time: when."),
        Own(AuditActions.BatchTraceOpened,
            "A batch's backward trace opened in Traceability (GET /api/batches/{batchId}/trace). User: the caller; time: when."),
        Own(AuditActions.BatchSearchExported,
            "A batch search exported as CSV. User: the caller; time: when. Details: the filters, row count and the caller's role."),
        Own(AuditActions.DeadLetterReplayed,
            "A dead-letter message replayed to its source topic (POST /api/admin/dead-letters/replay). User: the caller; time: when. Details: consumer group, target topic, partition, offset, correlation ID and duplicate status."),
        Own(AuditActions.ProjectionRebuilt,
            "The Traceability projection rebuilt from event history (POST /api/admin/rebuild). User: the caller; time: when. Details: consumer group, events processed, snapshots resolved, batch count, duration, consumer-offset alignment and store-cutover status."),
    ];

    public static readonly IReadOnlyList<UncoveredActionDto> NotCovered =
    [
        Gap("Changes made upstream after the snapshot was taken",
            "The MCC and intake actions are read from the upstream snapshot as it stood when it resolved, and Traceability consumes no MCC or intake events. A consignment, panel or dispatch corrected afterwards in the MCC and Intake Service is not seen; that service's own audit log has it."),
        Gap("Edits made in Processing or Quality Lab without an event",
            "Only the events listed as covered reach Traceability. A stage or test result edited, or work recorded before the determination is published (individual test entries), is in the owning service's records only."),
        Gap("MCC and intake actions of a batch whose snapshot has not resolved",
            "They appear once the snapshot resolves. A snapshot that ends Unavailable never gives them."),
        Gap("Factory arrival, and the quality panel's own time and user",
            "The snapshot records when the milk arrived, but not who recorded it; the gate panel is part of the consignment and has no time or user of its own, so the consignment's ConsignmentRegistered entry stands for it."),
        Gap("Processing events for a dispatch that has no batch yet",
            "They are kept until a batch is made from the dispatch, and appear then, under that batch, with their original time and user."),
        Gap("Events dead-lettered after their retries ran out",
            "They were never applied, so they are not on any timeline until inspected and replayed via POST /api/admin/dead-letters/replay."),
        Gap("Users, roles and society master data",
            "Managed in the Auth Service and the MCC and Intake Service, which publish no events to Traceability."),
        Gap("Reading in Traceability other than opening a batch or exporting",
            "Batch search pages, batch lookups by dispatch or batch reference, the dashboard, and reading this audit trail and coverage list are not audited."),
        Gap("Traceability's own read model updates",
            "Batch status, trace completeness, the daily aggregates and snapshot retries are derived from the covered events, so they are not audited separately."),
    ];

    public static AuditCoverageDto Dto() => new()
    {
        Covered = [.. Covered],
        NotCovered = [.. NotCovered],
        Documentation = Documentation,
    };

    private static CoveredActionDto Event(string action, Checkpoint checkpoint, string description) =>
        new() { Action = action, Checkpoint = checkpoint, Source = AuditSource.ConsumedEvent, Description = description };

    private static CoveredActionDto Snapshot(string action, Checkpoint checkpoint, string description) =>
        new() { Action = action, Checkpoint = checkpoint, Source = AuditSource.UpstreamSnapshot, Description = description };

    private static CoveredActionDto Own(string action, string description) =>
        new() { Action = action, Checkpoint = null, Source = AuditSource.Traceability, Description = description };

    private static UncoveredActionDto Gap(string action, string reason) => new() { Action = action, Reason = reason };
}
