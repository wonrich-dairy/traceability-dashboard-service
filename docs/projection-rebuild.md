# Projection Rebuild and Dead-Letter Inspection Runbook (SCRUM-140)

This runbook documents the procedures for inspecting failed messages, replaying dead-lettered events, and rebuilding the Traceability projection store from event history (PF-15).

---

## 1. Overview

The Traceability Service database is a read-model projection built from Kafka events published by the Processing Service and Quality Lab Service, enriched with upstream data cached from the MCC and Intake Service (SCRUM-12).

Because the database is a projection, it can be rebuilt by replaying retained source Kafka events from each partition's low watermark into a shadow table set, re-resolving upstream snapshots from the MCC and Intake Service, and atomically promoting the shadow tables.

Furthermore, messages that fail processing after retries are parked on dead-letter topics (`wonrich.dlq.*`) where they can be inspected and replayed once the root cause is resolved.

---

## 2. Dead-Letter Inspection & Replay

### Topics and Consumer Groups

| Consumer Group | Source Topic | Dead-Letter Topic (DLQ) |
|---|---|---|
| `traceability-stage-events` | `wonrich.processing.stage-events.v1` | `wonrich.dlq.traceability-stage-events.v1` |
| `traceability-hold-events` | `wonrich.processing.hold-events.v1` | `wonrich.dlq.traceability-hold-events.v1` |
| `traceability-batch-determinations` | `wonrich.quality-lab.batch-determinations.v1` | `wonrich.dlq.traceability-batch-determinations.v1` |

### Dead-Letter Inspection Endpoint

**System Administrators only** (`[Authorize(Policy = TraceabilityPolicies.AdminOnly)]`).

```http
GET /api/admin/dead-letters
GET /api/admin/dead-letters/{consumerGroup}
```

When listing all groups, a group whose dead-letter topic is missing from Kafka is skipped and
logged; messages from the other available topics are still returned. Requesting that group
directly returns `404` with the missing topic name in the problem detail.

Response payload includes for each message:
- `consumerGroup`: which consumer group failed
- `topic`: dead-letter topic name
- `partition` and `offset`: location on the dead-letter topic
- `key`: message key (e.g. batchId)
- `eventId`: event UUID
- `eventType`: record type
- `correlationId`: correlation ID for tracing across services
- `failureReason`: exception description and message
- `failureTimestamp`: UTC timestamp when the event was parked
- `attemptCount`: number of failed delivery attempts
- `originalTopic`, `originalPartition`, `originalOffset`: original event location
- `payload`: raw JSON payload

### Replaying Dead-Letter Messages

Once the underlying issue (e.g. bug fix deployed, upstream data corrected) is resolved:

```http
POST /api/admin/dead-letters/replay
Content-Type: application/json

{
  "consumerGroup": "traceability-stage-events",
  "partition": 0,
  "offset": 42
}
```

Or by group path:

```http
POST /api/admin/dead-letters/traceability-stage-events/replay
Content-Type: application/json

{
  "partition": 0,
  "offset": 42
}
```

#### Idempotence & Deduplication Guarantee
- If the event has **not** been processed: it is published back to its source topic (e.g. `wonrich.processing.stage-events.v1`) with original headers intact (`eventId`, `eventType`, `x-correlation-id`). The consumer group consumes and handles it.
- If the event has **already** been processed by this consumer group: the replay detects the existing `processed_messages` record. No duplicate batch record, timeline event, deviation, or aggregate is created.
- **Both operations are audited** in `audit_entries` as `DeadLetterReplayed`.

---

## 3. Projection Rebuild Procedure

### Automated Rebuild via Admin API

Triggered by an administrator:

```http
POST /api/admin/rebuild
Authorization: Bearer <Admin_JWT_Token>
Content-Type: application/json

{
  "clearStore": true,
  "resolveUpstreamSnapshots": true
}
```

Before invoking the endpoint, set `Kafka__Consumers__Enabled=false` for every running replica and restart them. This keeps in-flight live writes from landing in the retired table set. The API refuses to start if that setting is enabled.

What the automated rebuild does:
1. **Creates a separate consumer group:** e.g. `traceability-rebuild-<guid>`.
2. **Creates a fresh shadow table set:** Uses a unique `rb<guid>_` prefix and the current EF model. `audit_entries` is excluded and remains in place.
3. **Consumes retained events:** Captures each partition's high watermark, then reads from its low watermark across every discovered partition of:
   - `wonrich.processing.stage-events.v1`
   - `wonrich.processing.hold-events.v1`
   - `wonrich.quality-lab.batch-determinations.v1`
4. **Applies events chronologically:** Dispatches each event to its typed handler inside a transaction per event. Processed-message rows are recorded under the live consumer groups so replay deduplication still works after cutover. If an event is dead-lettered, the rebuild aborts and the active tables remain untouched.
5. **Re-resolves Upstream Snapshots (SCRUM-12):** Calls the MCC and Intake Service for pending snapshots. Successful resolutions are stored; failures remain pending for the normal retry worker.
6. **Cuts over atomically:** One MySQL multi-table `RENAME TABLE` promotes the complete shadow set and retains the previous set with an `old<guid>_` prefix. `audit_entries` remains untouched.
7. **Aligns consumer offsets:** Sets the live groups to the captured end offsets, so later events remain available. The response reports both `switchedOver` and `consumerOffsetsAligned`; the operation is audited (`ProjectionRebuilt`).

After the API reports a successful cutover and offset alignment, record `retiredTablePrefix`, then re-enable consumers and restart all replicas. If offset alignment fails, the new tables are already active; keep consumers stopped and repair the group offsets before restarting them. The previous table set is retained for rollback and should only be removed after the new projection is verified.

### Rollback

If verification fails, keep every consumer stopped. Use a single MySQL multi-table `RENAME TABLE` statement to move each active projection table to a fresh `failed_<guid>_` prefix and move the corresponding `<retiredTablePrefix><table>` back to its active name. Include all projection tables listed above in the same statement; leave `audit_entries` untouched. Then reset each live consumer group to the earliest retained source offset and restart consumers. The restored `processed_messages` rows make replay idempotent for events already present in the previous projection.

---

## 4. Manual Rebuild Procedure (Staging / Runbook Fallback)

If the automated rebuild is unavailable or for cold disaster recovery:

### Step 1: Pause Live Consumers
Stop the service or set `Kafka:Consumers:Enabled=false` in environment variables:
```bash
az webapp config appsettings set --name wonrich-traceability-staging \
  --resource-group wonrich-rg --settings Kafka__Consumers__Enabled=false
```

### Step 2: Empty Projection Tables
Connect to the database (`traceability` on staging, `traceability_prod` on production) and execute the reset script (preserving `audit_entries`):
```sql
START TRANSACTION;

DELETE FROM deviation_records;
DELETE FROM timeline_events;
DELETE FROM lab_determinations;
DELETE FROM upstream_snapshots;
DELETE FROM pending_dispatch_events;
DELETE FROM daily_quality_aggregates;
DELETE FROM daily_reason_code_aggregates;
DELETE FROM batch_records;
DELETE FROM processed_messages;

COMMIT;
```

### Step 3: Reset Consumer Group Offsets to Earliest
Reset offsets for all three consumer groups to the beginning of their topics:
```bash
kafka-consumer-groups --bootstrap-server <broker-host>:9092 \
  --group traceability-stage-events \
  --topic wonrich.processing.stage-events.v1 \
  --reset-offsets --to-earliest --execute

kafka-consumer-groups --bootstrap-server <broker-host>:9092 \
  --group traceability-hold-events \
  --topic wonrich.processing.hold-events.v1 \
  --reset-offsets --to-earliest --execute

kafka-consumer-groups --bootstrap-server <broker-host>:9092 \
  --group traceability-batch-determinations \
  --topic wonrich.quality-lab.batch-determinations.v1 \
  --reset-offsets --to-earliest --execute
```

### Step 4: Restart Consumers
Re-enable the consumers and restart the service:
```bash
az webapp config appsettings set --name wonrich-traceability-staging \
  --resource-group wonrich-rg --settings Kafka__Consumers__Enabled=true
az webapp restart --name wonrich-traceability-staging --resource-group wonrich-rg
```

### Step 5: Verify Upstream Snapshots & Data Integrity
The `UpstreamResolutionWorker` will automatically pick up and resolve all pending snapshots from the MCC and Intake Service (`SCRUM-12`).

Verify integrity against the expected seeded dataset or counts:
```sql
SELECT
    (SELECT COUNT(*) FROM batch_records)                AS batches,
    (SELECT COUNT(*) FROM upstream_snapshots WHERE ResolutionStatus = 'Resolved') AS resolved_snapshots,
    (SELECT COUNT(*) FROM timeline_events)              AS timeline_events,
    (SELECT COUNT(*) FROM deviation_records)            AS deviation_records,
    (SELECT COUNT(*) FROM daily_quality_aggregates)     AS daily_aggregates;
```

Verify that all batches have their expected statuses and completeness:
```sql
SELECT Status, Completeness, COUNT(*) FROM batch_records GROUP BY Status, Completeness;
```

---

## 5. Expected Results for the Seeded Dataset

Replaying the 24 sample batches defined in `docs/sample-data.sql` should reproduce:
- 24 batch records across 6 product lines (`FM`, `FLM`, `SY`, `SK`, `DY`, `CD`).
- Exact matching status distribution (`Processing`, `AwaitingLab`, `OnHold`, `Cleared`, `Failed`).
- Accurate daily quality aggregate counts for the 5 production days.
- Resolved upstream snapshots matching dispatch notes from the MCC and Intake Service.
