# Consolidated audit trail (SCRUM-138)

The SRS requires an audit trail of timestamp, user and action for all entries and updates. Traceability answers it from one place: `GET /api/audit` lists who recorded or did what, and when, across every facility. **System Administrators and Quality Control Officers only.**

This page is the coverage statement the traceability matrix (PF-15) links to. The same lists are served by `GET /api/audit/coverage`, from `AuditCoverage` in `src/TraceabilityService/Application/AuditCoverage.cs`. Change both together.

## What the trail is built from

Traceability does not receive every change made in the Wonrich services. It receives events, and it keeps a copy of what the MCC and Intake Service knows about each batch. The trail is read from those, at query time, so **the actions it covers are exactly the actions that have events or a place in the snapshot**. It does not claim to cover edits made in another service without an event.

| Source | Table | What each entry is |
|---|---|---|
| `ConsumedEvent` | `timeline_events` | An event from Processing or Quality Lab, as applied to a batch. User and time are the event's own (`RecordedBy`, `OccurredAt`), not when Traceability received it |
| `UpstreamSnapshot` | `upstream_snapshots` (`Payload`, once `Resolved`) | A record in the batch's upstream snapshot: each consignment's registration, the dispatch, the intake screening |
| `Traceability` | `audit_entries` | Something done in Traceability: a batch record or trace opened, a batch search exported |

Every entry has a time (UTC), a user ID (null only when the upstream service recorded none), an action, a checkpoint (`Mcc`, `Intake`, `Processing`, `Lab`; null for Traceability's own actions) and a batch ID (null for an export, which covers many batches), plus the batch's facility, the source, and a reference: the upstream `EventId`, the dispatch or consignment reference, the batch opened, or the export's file name.

## Actions covered

| Action | Checkpoint | Source | User and time |
|---|---|---|---|
| `ConsignmentRegistered` | Mcc | UpstreamSnapshot | Who registered the consignment at the MCC gate, and when. Stands for its quality panel too |
| `MilkDispatched` | Mcc | UpstreamSnapshot | Who dispatched the milk from the MCC, and when |
| `FactoryIntakeScreened` | Intake | UpstreamSnapshot | Who screened the dispatch at factory intake, and when |
| `MilkAllocatedToMixingTank` | Processing | ConsumedEvent | Who allocated the milk to a mixing tank, and when |
| `ProcessingStageRecorded` | Processing | ConsumedEvent | Who recorded the stage, and when it started |
| `ProcessingCompleted` | Processing | ConsumedEvent | Who completed processing, and when |
| `ProcessingHoldRaised` | Processing | ConsumedEvent | Who raised the hold, and when |
| `ProcessingHoldResolved` | Processing | ConsumedEvent | Who resolved the hold, and when |
| `BatchCleared` | Lab | ConsumedEvent | The analyst who cleared the batch, and when. A determination that supersedes an earlier one is a further entry; the earlier stays |
| `BatchFailed` | Lab | ConsumedEvent | The analyst who failed the batch, and when. Superseding as above |
| `BatchRecordOpened` | none | Traceability | Who opened the batch record (`GET /api/batches/{batchId}`), and when |
| `BatchTraceOpened` | none | Traceability | Who opened the backward trace (`GET /api/batches/{batchId}/trace`), and when |
| `BatchSearchExported` | none | Traceability | Who exported a batch search as CSV, and when, with the filters, row count and role |

## Not covered

| What | Why |
|---|---|
| Changes made upstream after the snapshot was taken | The MCC and intake actions are read from the upstream snapshot as it stood when it resolved, and Traceability consumes no MCC or intake events. A consignment, panel or dispatch corrected afterwards in the MCC and Intake Service is not seen; that service's own audit log has it |
| Edits made in Processing or Quality Lab without an event | Only the events listed above reach Traceability. A stage or test result edited, or work recorded before the determination is published (individual test entries), is in the owning service's records only |
| MCC and intake actions of a batch whose snapshot has not resolved | They appear once the snapshot resolves. A snapshot that ends `Unavailable` never gives them |
| Factory arrival, and the gate quality panel's own time and user | The snapshot records when the milk arrived, but not who recorded it; the panel is part of the consignment and has no time or user of its own, so `ConsignmentRegistered` stands for it |
| Processing events for a dispatch that has no batch yet | Kept until a batch is made from the dispatch (`pending_dispatch_events`), then shown under that batch with their original time and user |
| Events dead-lettered after their retries ran out | Never applied, so on no timeline. They are on the consumer's dead-letter topic |
| Users, roles and society master data | Managed in the Auth Service and the MCC and Intake Service, which publish no events to Traceability |
| Reading in Traceability other than opening a batch or exporting | Batch search pages, lookups by dispatch or batch reference, the dashboard, and reading this audit trail and coverage list are not audited |
| Traceability's own read model updates | Batch status, trace completeness, the daily aggregates and snapshot retries are derived from the covered events, so they are not audited separately |

## Filters

`GET /api/audit` takes `user`, `action`, `from`, `to` and `batchId`, all optional and combined with AND, and `page` (50 entries to a page, newest first, with `totalCount` and `totalPages`).

- `user` and `action` match exactly, without regard to case.
- `from` and `to` are factory days in Asia/Colombo, inclusive, as on the dashboard and the batch search.
- `batchId` leaves out exports, which belong to no single batch.
- `from` after `to`, a text filter over 100 characters, or `page` outside 1 to 1,000,000 is a 400 naming the parameter. No match is a 200 with an empty page and a `message`.

## Append-only

Entries cannot be changed or removed, at two levels:

- **Application.** No endpoint adds, changes or removes an entry: `api/audit` has GET routes only, and `IAuditTrailService` has no method that edits. `TraceabilityDbContext` refuses to save an `AuditEntry` that is modified or deleted (`AuditEntryImmutableException`), before anything reaches the database.
- **Database.** The triggers `audit_entries_no_update` and `audit_entries_no_delete` (migration `MakeAuditEntriesAppendOnly`) fail any `UPDATE` or `DELETE` on `audit_entries` with SQLSTATE `45000`, whoever runs it: EF's `ExecuteUpdate`/`ExecuteDelete`, raw SQL, or a person with a SQL client. `TRUNCATE` and `DROP` fire no trigger; they need privileges the deployed app has no reason to use.

The rows read from `timeline_events` and `upstream_snapshots` belong to the read model. They are written only by the consumers and the snapshot resolver, from upstream data, and are not edited by any endpoint.

## Code

| Piece | File |
|---|---|
| Endpoints | `Api/Controllers/AuditTrailController.cs` |
| Consolidated query (one `UNION ALL` over the three sources) | `Application/AuditTrailService.cs` |
| Filters | `Application/AuditTrailFilter.cs` |
| Coverage list | `Application/AuditCoverage.cs` |
| Auditing opened batches | `Api/Controllers/BatchesController.cs` |
| Append-only, application | `Infrastructure/Persistence/TraceabilityDbContext.cs` |
| Append-only, database | `Migrations/*_MakeAuditEntriesAppendOnly.cs` |
| Keeping the idempotent migration script runnable with triggers | `Infrastructure/Persistence/TriggerAwareMySqlMigrator.cs` |
