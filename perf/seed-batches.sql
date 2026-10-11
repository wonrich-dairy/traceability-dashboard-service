-- Performance seed: 1,000 generated batches for the NFR1 load test (p95 under 2 s)
-- on GET /api/batches/{batchId} and GET /api/batches?dispatchRef=… / ?batchReference=….
--
-- Staging or local only. Never run it against production.
--
-- What it creates, per batch:
--   batch_records       PERF-1000 … PERF-1999, at FACTORY-01, the six product lines in turn
--   upstream_snapshots  one per batch, resolved, with the payload contract from
--                       docs/schema.md (two tanks, three consignments); every 50th is
--                       still PendingRetry, so the gap path is exercised too
--   timeline_events     two Processing events and, unless the batch is awaiting the
--                       lab, one Lab event
--   deviation_records   one PhLow on every failed batch's Lab event
-- Two batches share each dispatch note and batch reference, as when Processing splits
-- a dispatch, so the list lookups return two rows:
--   PERF-1000 and PERF-1001 -> DN-PERF-1000 / WR-PERF-1000, … up to DN-PERF-1499.
--
-- The IDs are predictable so the JMeter plan can pick one at random:
--   /api/batches/PERF-${__Random(1000,1999)}
--   /api/batches?dispatchRef=DN-PERF-${__Random(1000,1499)}
--
-- Re-runnable: deletes the PERF- batches first (the FK cascade takes their events,
-- snapshot and deviations). perf/reset-seed-batches.sql removes them again.
-- To seed a different number, change @batches (up to 9,000, so IDs stay four digits).

SET @batches = 1000;
SET SESSION cte_max_recursion_depth = 10000;

DROP TEMPORARY TABLE IF EXISTS perf_batch;

CREATE TEMPORARY TABLE perf_batch (
    n               int PRIMARY KEY,
    BatchId         varchar(50) AS (CONCAT('PERF-', n)) STORED,
    DispatchNo      int         AS (1000 + (n - 1000) DIV 2) STORED,
    ProductLine     varchar(10) AS (ELT((n - 1000) % 6 + 1, 'FM', 'FLM', 'SY', 'SK', 'DY', 'CD')) STORED,
    Status          varchar(20) AS (CASE
                                        WHEN n % 10 = 0 THEN 'Failed'
                                        WHEN n % 25 = 1 THEN 'AwaitingLab'
                                        ELSE 'Cleared'
                                    END) STORED,
    Resolved        tinyint     AS (n % 50 <> 0) STORED,
    -- One every 37 minutes from 2026-08-01, so the batches spread over three weeks.
    DispatchedAt    datetime    AS (TIMESTAMP('2026-08-01 05:00:00') + INTERVAL (n - 1000) * 37 MINUTE) STORED
);

INSERT INTO perf_batch (n)
WITH RECURSIVE seq AS (
    SELECT 1000 AS n
    UNION ALL
    SELECT n + 1 FROM seq WHERE n < 999 + @batches
)
SELECT n FROM seq;

START TRANSACTION;

DELETE FROM batch_records WHERE BatchId LIKE 'PERF-%';

INSERT INTO batch_records
    (BatchId, DispatchRef, BatchReference, ProductLine, Facility, Status, Completeness,
     FirstEventAt, LastEventAt)
SELECT BatchId,
       CONCAT('DN-PERF-', DispatchNo),
       IF(Resolved, CONCAT('WR-PERF-', DispatchNo), NULL),
       ProductLine,
       'FACTORY-01',
       Status,
       IF(Resolved AND Status <> 'AwaitingLab', 'Complete', 'Partial'),
       DispatchedAt,
       DispatchedAt + INTERVAL IF(Status = 'AwaitingLab', 4, 6) HOUR
FROM perf_batch;

-- The payload contract, docs/schema.md: two tanks, the first with two consignments.
-- Society numbers cycle through VS001-VS050.
INSERT INTO upstream_snapshots
    (BatchId, ResolutionStatus, Payload, AttemptCount, LastAttemptAt, ResolvedAt, FailureReason)
SELECT BatchId,
       IF(Resolved, 'Resolved', 'PendingRetry'),
       IF(Resolved,
          JSON_OBJECT(
              'batchReference', CONCAT('WR-PERF-', DispatchNo),
              'dispatch', JSON_OBJECT(
                  'reference', CONCAT('DN-PERF-', DispatchNo),
                  'dispatchedAt', DATE_FORMAT(DispatchedAt, '%Y-%m-%dT%H:%i:%sZ'),
                  'dispatchedBy', 'mcc.manager',
                  'bowserRegistration', CONCAT('BWR-', DispatchNo % 20),
                  'totalLitres', 1050),
              'intake', JSON_OBJECT(
                  'arrivedAt', DATE_FORMAT(DispatchedAt + INTERVAL 145 MINUTE, '%Y-%m-%dT%H:%i:%sZ'),
                  'screenedAt', DATE_FORMAT(DispatchedAt + INTERVAL 150 MINUTE, '%Y-%m-%dT%H:%i:%sZ'),
                  'screenedBy', 'intake.officer',
                  'temperatureC', 4.50),
              'tanks', JSON_ARRAY(
                  JSON_OBJECT(
                      'code', 'T1',
                      'quantityDrawnLitres', 700,
                      'consignments', JSON_ARRAY(
                          JSON_OBJECT(
                              'reference', CONCAT('CON-PERF-', n, '-1'),
                              'society', JSON_OBJECT('code', CONCAT('VS', LPAD(n % 50 + 1, 3, '0'))),
                              'volumeLitres', 380,
                              'registeredAt', DATE_FORMAT(DispatchedAt - INTERVAL 45 MINUTE, '%Y-%m-%dT%H:%i:%sZ'),
                              'registeredBy', 'mcc.intake.officer',
                              'panel', JSON_OBJECT('fatPercentage', 4.10, 'snfPercentage', 8.70,
                                                   'clrReading', 28.40, 'temperatureC', 5.50,
                                                   'verdict', 'Accepted')),
                          JSON_OBJECT(
                              'reference', CONCAT('CON-PERF-', n, '-2'),
                              'society', JSON_OBJECT('code', CONCAT('VS', LPAD((n + 17) % 50 + 1, 3, '0'))),
                              'volumeLitres', 320,
                              'registeredAt', DATE_FORMAT(DispatchedAt - INTERVAL 40 MINUTE, '%Y-%m-%dT%H:%i:%sZ'),
                              'registeredBy', 'mcc.intake.officer',
                              'panel', JSON_OBJECT('fatPercentage', 3.95, 'snfPercentage', 8.62,
                                                   'clrReading', 28.05, 'temperatureC', 6.00,
                                                   'verdict', 'Accepted')))),
                  JSON_OBJECT(
                      'code', 'T2',
                      'quantityDrawnLitres', 350,
                      'consignments', JSON_ARRAY(
                          JSON_OBJECT(
                              'reference', CONCAT('CON-PERF-', n, '-3'),
                              'society', JSON_OBJECT('code', CONCAT('VS', LPAD((n + 33) % 50 + 1, 3, '0'))),
                              'volumeLitres', 350,
                              'registeredAt', DATE_FORMAT(DispatchedAt - INTERVAL 35 MINUTE, '%Y-%m-%dT%H:%i:%sZ'),
                              'registeredBy', 'mcc.intake.officer'))))),
          NULL),
       IF(Resolved, 1, 0),
       IF(Resolved, DispatchedAt + INTERVAL 1 MINUTE, NULL),
       IF(Resolved, DispatchedAt + INTERVAL 1 MINUTE, NULL),
       NULL
FROM perf_batch;

-- k 1-2: Processing stage and completion; k 3: the lab's determination.
INSERT INTO timeline_events
    (BatchId, Checkpoint, EventType, OccurredAt, RecordedBy, IsDeviation,
     Payload, SourceEventId, ContractVersion)
SELECT b.BatchId,
       IF(e.k = 3, 'Lab', 'Processing'),
       ELT(e.k, 'ProcessingStageRecorded', 'ProcessingCompleted',
           IF(b.Status = 'Failed', 'BatchFailed', 'BatchCleared')),
       b.DispatchedAt + INTERVAL ELT(e.k, 210, 240, 360) MINUTE,
       IF(e.k = 3, 'quality.analyst', 'processing.tech'),
       e.k = 3 AND b.Status = 'Failed',
       ELT(e.k,
           JSON_OBJECT('stageType', 'Pasteuriser', 'endTemperatureC', 73.50),
           JSON_OBJECT('totalDurationMinutes', 95),
           JSON_OBJECT('result', b.Status, 'ph', IF(b.Status = 'Failed', 6.40, 6.70))),
       UUID(),
       'v1'
FROM perf_batch b
CROSS JOIN (SELECT 1 AS k UNION ALL SELECT 2 UNION ALL SELECT 3) e
WHERE NOT (e.k = 3 AND b.Status = 'AwaitingLab');

INSERT INTO deviation_records
    (BatchId, TimelineEventId, Checkpoint, OccurredAt, ReasonCode, Parameter, ObservedValue, `Limit`, Description)
SELECT e.BatchId, e.Id, 'Lab', e.OccurredAt, 'PhLow', 'PH', 6.40, 6.60, 'Below 6.6; acidity developed'
FROM timeline_events e
JOIN perf_batch b ON b.BatchId = e.BatchId
WHERE e.Checkpoint = 'Lab' AND b.Status = 'Failed';

COMMIT;

DROP TEMPORARY TABLE perf_batch;

-- Checks. Expected for 1,000: 1000 batches, 1000 snapshots (980 resolved),
-- 2960 events, 100 deviations, 500 dispatch notes shared by two batches each.
--
--   SELECT
--       (SELECT COUNT(*) FROM batch_records WHERE BatchId LIKE 'PERF-%') AS batches,
--       (SELECT COUNT(*) FROM upstream_snapshots WHERE BatchId LIKE 'PERF-%') AS snapshots,
--       (SELECT COUNT(*) FROM upstream_snapshots WHERE BatchId LIKE 'PERF-%' AND ResolutionStatus = 'Resolved') AS resolved,
--       (SELECT COUNT(*) FROM timeline_events WHERE BatchId LIKE 'PERF-%') AS events,
--       (SELECT COUNT(*) FROM deviation_records WHERE BatchId LIKE 'PERF-%') AS deviations,
--       (SELECT COUNT(DISTINCT DispatchRef) FROM batch_records WHERE BatchId LIKE 'PERF-%') AS dispatches;
