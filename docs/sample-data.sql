-- Sample data for the traceability dashboard.
--
-- 24 batches over five days (2026-09-16 to 2026-09-20), four for each product
-- line: FM, FLM, SY, SK, DY and CD, all at facility FACTORY-01. Between them they
-- cover every status, a trace that is complete and one with gaps, each upstream
-- resolution status, and every deviation reason the rules below produce. Four
-- batches carry the stories the dashboard and the FR7 backward trace are
-- demonstrated with:
--   262-FM-A  clean pass, all four checkpoints
--   262-FM-B  lab failure with a traceable upstream cause (the demo batch)
--   263-FM-A  in progress, no Processing or Lab events yet, upstream not fetched yet
--   262-FM-C  second pass, so FM's pass rate is not 2-of-4 by coincidence
--
-- Batch code = day of year - product code - nth set of that product that day.
-- Day 259 is 2026-09-16 and day 263 is 2026-09-20, the "today" of this data:
-- its batches are the ones still in progress.
--
-- How it is built: one row per batch in seed_batch holds its checkpoint times
-- and readings, one row per consignment in seed_consignment its sources. Every
-- table is then filled from those two, so the numbers cannot disagree:
--   - timeline events are generated from the readings;
--   - deviations are found by applying the reference limits to the events'
--     payloads, and IsDeviation is set from them;
--   - Completeness and the daily aggregates are counted from the rows inserted.
--
-- Reference limits, which the deviation rules apply:
--   MCC at or below 4C, factory intake at or below 5C,
--   pasteurisation 72-75C, pH 6.6-6.8, all five sensory grades Normal.
-- Composition is in range for every batch: fat 3.5-4.5%, SNF 8.5-9.0%, CLR 27-30.
--
-- Enum columns store the C# enum *member name*, because the configurations use
-- HasConversion<string>(). Note 'Mcc', not 'MCC' -- it has to match
-- Checkpoint.Mcc exactly or EF will throw when it reads the row back.
--
-- EventType values for MCC and Intake are placeholders until those services
-- publish their contracts. ProcessingCompleted and ProcessingHoldRaised are
-- Processing's event names, BatchCleared and BatchFailed Quality Lab's.
--
-- deviation_records.Limit is a reserved word in MySQL, so it is quoted below.
--
-- processed_messages and audit_entries are left empty: they record what the
-- consumers and users did, and nothing here went through either.
--
-- Re-runnable: the deletes below clear these batches first, and the FK cascade
-- on BatchId takes their timeline events, snapshots and deviations with them.
-- Aggregate rows for the facility, days and product lines seeded here are
-- replaced with the sample batches' figures. Id columns are auto-increment and
-- deliberately not specified.

DROP TEMPORARY TABLE IF EXISTS seed_batch, seed_consignment;

CREATE TEMPORARY TABLE seed_batch (
    BatchId          varchar(50) PRIMARY KEY,
    DispatchRef      varchar(50) NOT NULL,
    Status           varchar(20) NOT NULL,
    Facility         varchar(50) NOT NULL DEFAULT 'FACTORY-01',
    -- When each checkpoint reported. NULL = not reached yet.
    MccAt            datetime NOT NULL,
    IntakeAt         datetime NULL,
    ProcessingAt     datetime NULL,
    LabAt            datetime NULL,
    -- Composition at MCC. Intake reads slightly lower, see the timeline insert.
    Fat              decimal(4,2) NOT NULL,
    Snf              decimal(4,2) NOT NULL,
    Clr              decimal(4,2) NOT NULL,
    MccTemp          decimal(4,2) NOT NULL,
    IntakeTemp       decimal(4,2) NULL,
    PasteuriserTemp  decimal(4,2) NULL,
    Ph               decimal(4,2) NULL,
    -- Sensory grades the lab marked Abnormal, comma-separated. '' = all Normal.
    SensoryFaults    varchar(100) NULL,
    -- How the upstream lookup went. Resolved at the first attempt unless set below.
    SnapshotStatus   varchar(20) NOT NULL DEFAULT 'Resolved',
    SnapshotAttempts int NOT NULL DEFAULT 1,
    SnapshotFailure  varchar(500) NULL,
    ProductLine      varchar(10)
        AS (SUBSTRING_INDEX(SUBSTRING_INDEX(BatchId, '-', 2), '-', -1)) STORED,
    ProductionDate   date
        AS (MAKEDATE(2026, CAST(SUBSTRING_INDEX(BatchId, '-', 1) AS UNSIGNED))) STORED
);

CREATE TEMPORARY TABLE seed_consignment (
    BatchId      varchar(50) NOT NULL,
    Society      varchar(10) NOT NULL,
    VolumeLitres int NOT NULL,
    -- The society's quality panel at the MCC; filled in below.
    Fat          decimal(4,2) NULL,
    Snf          decimal(4,2) NULL,
    Clr          decimal(4,2) NULL,
    PRIMARY KEY (BatchId, Society)
);

-- ---------------------------------------------------------------------------
-- Batches
-- ---------------------------------------------------------------------------
-- Status counts: 12 Cleared, 4 Failed, 2 OnHold, 2 AwaitingLab, 4 Processing.

INSERT INTO seed_batch
    (BatchId, DispatchRef, Status,
     MccAt, IntakeAt, ProcessingAt, LabAt,
     Fat, Snf, Clr, MccTemp, IntakeTemp, PasteuriserTemp, Ph, SensoryFaults)
VALUES
    -- Day 259, 2026-09-16 ----------------------------------------------------
    ('259-SY-A',  'DN-20260916-01', 'Cleared',
     '2026-09-16 05:05', '2026-09-16 07:30', '2026-09-16 09:15', '2026-09-16 10:55',
     4.15, 8.70, 28.30, 3.70, 4.50, 73.80, 6.70, ''),
    ('259-SK-A',  'DN-20260916-02', 'Cleared',
     '2026-09-16 05:30', '2026-09-16 07:55', '2026-09-16 09:40', '2026-09-16 11:20',
     3.88, 8.62, 27.80, 3.90, 4.80, 74.20, 6.66, ''),
    ('259-CD-A',  'DN-20260916-03', 'Cleared',
     '2026-09-16 06:00', '2026-09-16 08:25', '2026-09-16 10:10', '2026-09-16 11:50',
     4.30, 8.84, 28.90, 3.50, 4.30, 73.10, 6.74, ''),

    -- Day 260, 2026-09-17 ----------------------------------------------------
    ('260-FLM-A', 'DN-20260917-01', 'Cleared',
     '2026-09-17 05:10', '2026-09-17 07:35', '2026-09-17 09:20', '2026-09-17 11:00',
     4.05, 8.66, 28.20, 3.80, 4.60, 73.60, 6.69, ''),
    ('260-SY-A',  'DN-20260917-02', 'Cleared',
     '2026-09-17 05:35', '2026-09-17 08:00', '2026-09-17 09:45', '2026-09-17 11:25',
     4.20, 8.78, 28.60, 3.60, 4.40, 74.00, 6.72, ''),
    -- Exactly 4.00C at MCC: on the limit, not over it, so no deviation.
    ('260-DY-A',  'DN-20260917-03', 'Cleared',
     '2026-09-17 06:05', '2026-09-17 08:30', '2026-09-17 10:15', '2026-09-17 11:55',
     3.92, 8.58, 27.70, 4.00, 4.90, 72.80, 6.65, ''),
    -- Fails on appearance and texture alone, with every reading upstream in
    -- range: the contrast to 262-FM-B, a failure the trace cannot explain.
    ('260-CD-A',  'DN-20260917-04', 'Failed',
     '2026-09-17 06:30', '2026-09-17 08:55', '2026-09-17 10:40', '2026-09-17 12:20',
     4.12, 8.71, 28.35, 3.80, 4.70, 73.40, 6.68, 'appearance,texture'),

    -- Day 261, 2026-09-18 ----------------------------------------------------
    ('261-FLM-A', 'DN-20260918-01', 'Cleared',
     '2026-09-18 05:00', '2026-09-18 07:25', '2026-09-18 09:10', '2026-09-18 10:50',
     4.08, 8.69, 28.25, 3.60, 4.50, 73.70, 6.71, ''),
    -- Pasteuriser ended at 70.6C, under the 72C minimum, so Processing raised a
    -- hold. No Lab event: a held batch is not sent for testing.
    ('261-SY-A',  'DN-20260918-02', 'OnHold',
     '2026-09-18 05:25', '2026-09-18 07:50', '2026-09-18 09:35', NULL,
     4.18, 8.75, 28.55, 3.70, 4.60, 70.60, NULL, NULL),
    ('261-SK-A',  'DN-20260918-03', 'Cleared',
     '2026-09-18 05:50', '2026-09-18 08:15', '2026-09-18 10:00', '2026-09-18 11:40',
     3.95, 8.64, 28.00, 3.90, 4.80, 73.90, 6.67, ''),
    -- Left MCC cold but warmed to 6.2C in transit; by the lab pH has dropped and
    -- the smell is off. Second set of SK that day, hence B.
    ('261-SK-B',  'DN-20260918-04', 'Failed',
     '2026-09-18 06:15', '2026-09-18 08:40', '2026-09-18 10:25', '2026-09-18 12:05',
     3.90, 8.56, 27.75, 3.95, 6.20, 73.30, 6.45, 'smell'),
    ('261-DY-A',  'DN-20260918-05', 'Cleared',
     '2026-09-18 06:40', '2026-09-18 09:05', '2026-09-18 10:50', '2026-09-18 12:30',
     4.25, 8.82, 28.80, 3.40, 4.20, 74.40, 6.73, ''),

    -- Day 262, 2026-09-19 ----------------------------------------------------
    -- Batch 1: clean pass.
    ('262-FM-A',  'DN-20260919-01', 'Cleared',
     '2026-09-19 05:15', '2026-09-19 07:40', '2026-09-19 09:20', '2026-09-19 11:05',
     4.10, 8.72, 28.40, 3.80, 4.60, 73.50, 6.68, ''),
    -- Batch 2: the demo batch. The chiller at the collection centre was down, so
    -- the milk left MCC at 12.4C instead of under 4C and still arrived at 9.8C.
    -- Pasteurisation itself ran normally -- which is the point: the deviation is
    -- upstream of the step that looks fine. By the lab, acidity has developed
    -- (pH 6.32) and taste, texture and smell are off, so the batch fails.
    -- Opening this batch should show a QCO the temperature at Mcc as the first
    -- thing out of range.
    ('262-FM-B',  'DN-20260919-02', 'Failed',
     '2026-09-19 05:40', '2026-09-19 08:05', '2026-09-19 09:55', '2026-09-19 11:30',
     3.95, 8.55, 27.90, 12.40, 9.80, 73.20, 6.32, 'texture,taste,smell'),
    -- Batch 4: second pass.
    ('262-FM-C',  'DN-20260919-03', 'Cleared',
     '2026-09-19 06:10', '2026-09-19 08:35', '2026-09-19 10:25', '2026-09-19 12:00',
     4.22, 8.80, 28.70, 3.60, 4.20, 74.10, 6.71, ''),
    -- pH too high and the colour off: a dosing problem at the factory, not upstream.
    ('262-FLM-A', 'DN-20260919-04', 'Failed',
     '2026-09-19 06:35', '2026-09-19 09:00', '2026-09-19 10:45', '2026-09-19 12:25',
     4.02, 8.63, 28.05, 3.70, 4.50, 73.80, 6.95, 'colour'),
    -- Processed yesterday, still waiting for the lab.
    ('262-SK-A',  'DN-20260919-05', 'AwaitingLab',
     '2026-09-19 07:00', '2026-09-19 09:25', '2026-09-19 11:10', NULL,
     4.00, 8.60, 27.90, 3.80, 4.70, 73.60, NULL, NULL),
    ('262-DY-A',  'DN-20260919-06', 'Cleared',
     '2026-09-19 07:20', '2026-09-19 09:45', '2026-09-19 11:30', '2026-09-19 13:10',
     4.14, 8.74, 28.45, 3.70, 4.40, 74.30, 6.70, ''),
    -- Pasteuriser overshot to 76.1C: held, the opposite failure to 261-SY-A.
    ('262-CD-A',  'DN-20260919-07', 'OnHold',
     '2026-09-19 07:45', '2026-09-19 10:10', '2026-09-19 11:55', NULL,
     4.28, 8.86, 28.95, 3.60, 4.30, 76.10, NULL, NULL),

    -- Day 263, 2026-09-20 (today) ---------------------------------------------
    -- Batch 3: in progress. Chilling happens at the MCC and factory intake has
    -- already been recorded, so this batch is past both and sitting at the
    -- processing stage -- hence 'Processing' rather than 'Chilling', even though
    -- there is no Processing event yet. The dashboard has to render it without
    -- Processing and Lab events.
    ('263-FM-A',  'DN-20260920-01', 'Processing',
     '2026-09-20 05:20', '2026-09-20 07:35', NULL, NULL,
     4.02, 8.65, 28.10, 3.90, 4.40, NULL, NULL, NULL),
    ('263-FLM-A', 'DN-20260920-02', 'AwaitingLab',
     '2026-09-20 05:45', '2026-09-20 08:10', '2026-09-20 09:55', NULL,
     4.06, 8.67, 28.15, 3.80, 4.60, 73.70, NULL, NULL),
    -- Left MCC at 4.6C: an early warning on a batch that has not failed anything yet.
    ('263-SY-A',  'DN-20260920-03', 'Processing',
     '2026-09-20 06:10', '2026-09-20 08:35', NULL, NULL,
     4.16, 8.73, 28.40, 4.60, 4.90, NULL, NULL, NULL),
    ('263-DY-A',  'DN-20260920-04', 'Processing',
     '2026-09-20 06:35', '2026-09-20 09:00', NULL, NULL,
     3.98, 8.61, 27.95, 3.70, 4.50, NULL, NULL, NULL),
    ('263-CD-A',  'DN-20260920-05', 'Processing',
     '2026-09-20 07:00', '2026-09-20 09:25', NULL, NULL,
     4.20, 8.79, 28.65, 3.80, 4.60, NULL, NULL, NULL);

-- Four of today's lookups to the MCC and Intake Service did not resolve, one of
-- each case the resolver can be in. They have no consignments below: the lookup
-- is what would have revealed them.
UPDATE seed_batch SET SnapshotStatus = 'PendingRetry', SnapshotAttempts = 0
WHERE BatchId = '263-FM-A';   -- not fetched yet

UPDATE seed_batch SET SnapshotStatus = 'PendingRetry', SnapshotAttempts = 2,
    SnapshotFailure = 'MCC and Intake Service returned 503 Service Unavailable'
WHERE BatchId = '263-FLM-A';  -- failed twice, will retry

UPDATE seed_batch SET SnapshotStatus = 'Unavailable', SnapshotAttempts = 5,
    SnapshotFailure = 'MCC and Intake Service returned 503 Service Unavailable on all 5 attempts'
WHERE BatchId = '263-DY-A';   -- retries ran out

UPDATE seed_batch SET SnapshotStatus = 'Unavailable', SnapshotAttempts = 1,
    SnapshotFailure = 'MCC and Intake Service has no dispatch note DN-20260920-05'
WHERE BatchId = '263-CD-A';   -- upstream has no such dispatch

-- ---------------------------------------------------------------------------
-- Source consignments
-- ---------------------------------------------------------------------------
-- A tank blends several village societies, so a batch has more than one source
-- and a society appears in more than one batch. VS014's milk reaches six
-- batches across four product lines, which is what makes a forward trace
-- ("which batches did this society's milk reach?") non-trivial.

INSERT INTO seed_consignment (BatchId, Society, VolumeLitres)
VALUES
    ('259-SY-A',  'VS003', 360), ('259-SY-A',  'VS014', 420),
    ('259-SK-A',  'VS009', 310), ('259-SK-A',  'VS027', 390),
    ('259-CD-A',  'VS031', 400), ('259-CD-A',  'VS045', 270),

    ('260-FLM-A', 'VS014', 430), ('260-FLM-A', 'VS022', 340),
    ('260-SY-A',  'VS003', 350), ('260-SY-A',  'VS038', 380),
    ('260-DY-A',  'VS009', 300), ('260-DY-A',  'VS019', 410),
    ('260-CD-A',  'VS027', 370), ('260-CD-A',  'VS045', 260),

    ('261-FLM-A', 'VS014', 415), ('261-FLM-A', 'VS031', 385),
    ('261-SY-A',  'VS003', 355), ('261-SY-A',  'VS022', 345),
    ('261-SK-A',  'VS009', 305), ('261-SK-A',  'VS038', 395),
    ('261-SK-B',  'VS019', 405), ('261-SK-B',  'VS027', 365), ('261-SK-B', 'VS045', 255),
    ('261-DY-A',  'VS014', 425), ('261-DY-A',  'VS031', 375),

    ('262-FM-A',  'VS014', 420), ('262-FM-A',  'VS031', 380),
    -- Three sources, so the backward trace for the demo batch has something to fan out to.
    ('262-FM-B',  'VS009', 300), ('262-FM-B',  'VS022', 350), ('262-FM-B',  'VS045', 260),
    ('262-FM-C',  'VS014', 410), ('262-FM-C',  'VS038', 390),
    ('262-FLM-A', 'VS003', 365), ('262-FLM-A', 'VS019', 400),
    ('262-SK-A',  'VS027', 360), ('262-SK-A',  'VS038', 385),
    ('262-DY-A',  'VS009', 310), ('262-DY-A',  'VS031', 370),
    ('262-CD-A',  'VS022', 335), ('262-CD-A',  'VS045', 265),

    ('263-SY-A',  'VS003', 350), ('263-SY-A',  'VS045', 270);

-- Each society's panel is the batch's MCC reading moved by a small offset that
-- depends on the society, so the blend is close to what each society delivered.
UPDATE seed_consignment c
JOIN seed_batch b ON b.BatchId = c.BatchId
SET c.Fat = b.Fat + (CAST(SUBSTRING(c.Society, 3) AS SIGNED) % 5 - 2) * 0.04,
    c.Snf = b.Snf + (CAST(SUBSTRING(c.Society, 3) AS SIGNED) % 3 - 1) * 0.03,
    c.Clr = b.Clr + (CAST(SUBSTRING(c.Society, 3) AS SIGNED) % 3 - 1) * 0.10;

START TRANSACTION;

DELETE FROM batch_records
WHERE BatchId IN (SELECT BatchId FROM seed_batch);

DELETE FROM daily_quality_aggregates
WHERE (Facility, Date, ProductLine) IN (SELECT Facility, ProductionDate, ProductLine FROM seed_batch);

DELETE FROM daily_reason_code_aggregates
WHERE (Facility, Date, ProductLine) IN (SELECT Facility, ProductionDate, ProductLine FROM seed_batch);

-- ---------------------------------------------------------------------------
-- Batch records
-- ---------------------------------------------------------------------------
-- Complete once the Lab has reported and the upstream snapshot is resolved.

INSERT INTO batch_records
    (BatchId, DispatchRef, ProductLine, Facility, Status, Completeness, FirstEventAt, LastEventAt)
SELECT b.BatchId, b.DispatchRef, b.ProductLine, b.Facility, b.Status,
       IF(b.LabAt IS NOT NULL AND b.SnapshotStatus = 'Resolved', 'Complete', 'Partial'),
       b.MccAt,
       COALESCE(b.LabAt, b.ProcessingAt, b.IntakeAt, b.MccAt)
FROM seed_batch b;

-- ---------------------------------------------------------------------------
-- Upstream snapshots
-- ---------------------------------------------------------------------------
-- One per batch. When resolved, Payload is the whole tree the MCC and Intake
-- Service returned:
--   dispatchNote { number, dispatchedAt }
--   tanks[]      { tankCode, quantityDrawnLitres,
--                  consignments[] { reference, society, volumeLitres,
--                                   panel { fatPercentage, snfPercentage, clrReading } } }
-- Here a society's milk goes to tank T1 or T2 by whether its number is odd.

INSERT INTO upstream_snapshots
    (BatchId, ResolutionStatus, Payload, AttemptCount, LastAttemptAt, ResolvedAt, FailureReason)
SELECT b.BatchId,
       b.SnapshotStatus,
       IF(b.SnapshotStatus = 'Resolved',
          JSON_OBJECT(
              'dispatchNote', JSON_OBJECT(
                  'number', b.DispatchRef,
                  'dispatchedAt', DATE_FORMAT(b.MccAt, '%Y-%m-%dT%H:%i:%sZ')),
              'tanks', t.Tanks),
          NULL),
       b.SnapshotAttempts,
       IF(b.SnapshotAttempts = 0, NULL, b.MccAt + INTERVAL b.SnapshotAttempts MINUTE),
       IF(b.SnapshotStatus = 'Resolved', b.MccAt + INTERVAL 1 MINUTE, NULL),
       b.SnapshotFailure
FROM seed_batch b
LEFT JOIN (
    SELECT tank.BatchId,
           JSON_ARRAYAGG(JSON_OBJECT(
               'tankCode', tank.TankCode,
               'quantityDrawnLitres', tank.Litres,
               'consignments', tank.Consignments)) AS Tanks
    FROM (
        SELECT c.BatchId,
               CONCAT('T', 2 - CAST(SUBSTRING(c.Society, 3) AS SIGNED) % 2) AS TankCode,
               SUM(c.VolumeLitres) AS Litres,
               JSON_ARRAYAGG(JSON_OBJECT(
                   'reference', CONCAT('CON-',
                       DATE_FORMAT(MAKEDATE(2026, CAST(SUBSTRING_INDEX(c.BatchId, '-', 1) AS UNSIGNED)), '%Y%m%d'),
                       '-', c.Society),
                   'society', c.Society,
                   'volumeLitres', c.VolumeLitres,
                   'panel', JSON_OBJECT('fatPercentage', c.Fat, 'snfPercentage', c.Snf,
                                        'clrReading', c.Clr))) AS Consignments
        FROM seed_consignment c
        GROUP BY c.BatchId, TankCode
    ) tank
    GROUP BY tank.BatchId
) t ON t.BatchId = b.BatchId;

-- ---------------------------------------------------------------------------
-- Timeline events
-- ---------------------------------------------------------------------------
-- One event per checkpoint the batch has reached, in stage order: milk is
-- collected and chilled at the MCC, then taken in at the factory, then
-- processed, then lab tested. Mcc and Intake report fat/SNF/CLR/temperature,
-- Processing the pasteuriser temperature, Lab the result, pH and the five
-- sensory grades. Intake reads a little lower than MCC on composition, as the
-- same milk measured again after transport.
--
-- IsDeviation starts at 0 and is set from the deviations found below, so the
-- flag and the deviation rows come from the same rules.
--
-- SourceEventId is fixed so the script stays re-runnable:
--   ...-<day><product><set>000000<n>
--   product 1 FM, 2 FLM, 3 SY, 4 SK, 5 DY, 6 CD;  set a-d;
--   n 1 Mcc, 2 Intake, 3 Processing, 4 Lab.
-- e.g. 262-FM-B's Lab event is 00000000-0000-4000-8000-2621b0000004.

INSERT INTO timeline_events
    (BatchId, Checkpoint, EventType, OccurredAt, RecordedBy, IsDeviation,
     Payload, SourceEventId, ContractVersion)
SELECT b.BatchId,
       ELT(c.n, 'Mcc', 'Intake', 'Processing', 'Lab'),
       CASE c.n
           WHEN 1 THEN 'MccDispatchCreated'
           WHEN 2 THEN 'FactoryIntakeRecorded'
           WHEN 3 THEN IF(b.Status = 'OnHold', 'ProcessingHoldRaised', 'ProcessingCompleted')
           ELSE IF(b.Status = 'Failed', 'BatchFailed', 'BatchCleared')
       END,
       CASE c.n WHEN 1 THEN b.MccAt WHEN 2 THEN b.IntakeAt WHEN 3 THEN b.ProcessingAt ELSE b.LabAt END,
       ELT(c.n, 'mcc.manager', 'intake.officer', 'processing.tech', 'quality.analyst'),
       0,
       CASE c.n
           WHEN 1 THEN JSON_OBJECT('fatPercentage', b.Fat, 'snfPercentage', b.Snf,
                                   'clrReading', b.Clr, 'temperatureC', b.MccTemp)
           WHEN 2 THEN JSON_OBJECT('fatPercentage', b.Fat - 0.05, 'snfPercentage', b.Snf - 0.05,
                                   'clrReading', b.Clr - 0.20, 'temperatureC', b.IntakeTemp)
           WHEN 3 THEN JSON_OBJECT('stageType', 'Pasteuriser', 'endTemperatureC', b.PasteuriserTemp)
           ELSE JSON_OBJECT('result', b.Status, 'ph', b.Ph,
                            'appearance', IF(FIND_IN_SET('appearance', b.SensoryFaults), 'Abnormal', 'Normal'),
                            'texture',    IF(FIND_IN_SET('texture',    b.SensoryFaults), 'Abnormal', 'Normal'),
                            'taste',      IF(FIND_IN_SET('taste',      b.SensoryFaults), 'Abnormal', 'Normal'),
                            'colour',     IF(FIND_IN_SET('colour',     b.SensoryFaults), 'Abnormal', 'Normal'),
                            'smell',      IF(FIND_IN_SET('smell',      b.SensoryFaults), 'Abnormal', 'Normal'))
       END,
       CONCAT('00000000-0000-4000-8000-',
              SUBSTRING_INDEX(b.BatchId, '-', 1),
              FIELD(b.ProductLine, 'FM', 'FLM', 'SY', 'SK', 'DY', 'CD'),
              LOWER(SUBSTRING_INDEX(b.BatchId, '-', -1)),
              '000000', c.n),
       'v1'
FROM seed_batch b
CROSS JOIN (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4) c
WHERE CASE c.n WHEN 1 THEN b.MccAt WHEN 2 THEN b.IntakeAt WHEN 3 THEN b.ProcessingAt ELSE b.LabAt END
      IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Deviations
-- ---------------------------------------------------------------------------
-- The reference limits applied to each event's payload, one row per limit
-- broken, with the reading and the limit as exact decimals. A Lab event can
-- break more than one (262-FM-B: pH and sensory). Sensory grades have no number,
-- so their ObservedValue and Limit are NULL.

INSERT INTO deviation_records
    (BatchId, TimelineEventId, Checkpoint, OccurredAt,
     ReasonCode, Parameter, ObservedValue, `Limit`, Description)
SELECT d.BatchId, d.Id, d.Checkpoint, d.OccurredAt,
       d.ReasonCode, d.Parameter, d.ObservedValue, d.LimitValue, d.Description
FROM (
    SELECT e.BatchId, e.Id, e.Checkpoint, e.OccurredAt,
           'TemperatureHigh' AS ReasonCode, 'TemperatureC' AS Parameter,
           CAST(e.Payload->>'$.temperatureC' AS DECIMAL(5,2)) AS ObservedValue,
           4.00 AS LimitValue,
           'Above 4C leaving the MCC' AS Description
    FROM timeline_events e
    WHERE e.Checkpoint = 'Mcc'
      AND CAST(e.Payload->>'$.temperatureC' AS DECIMAL(5,2)) > 4.00
    UNION ALL
    SELECT e.BatchId, e.Id, e.Checkpoint, e.OccurredAt,
           'TemperatureHigh', 'TemperatureC',
           CAST(e.Payload->>'$.temperatureC' AS DECIMAL(5,2)), 5.00,
           'Above 5C at factory intake'
    FROM timeline_events e
    WHERE e.Checkpoint = 'Intake'
      AND CAST(e.Payload->>'$.temperatureC' AS DECIMAL(5,2)) > 5.00
    UNION ALL
    SELECT e.BatchId, e.Id, e.Checkpoint, e.OccurredAt,
           'TemperatureLow', 'EndTemperatureC',
           CAST(e.Payload->>'$.endTemperatureC' AS DECIMAL(5,2)), 72.00,
           'Below the 72-75C pasteurisation range'
    FROM timeline_events e
    WHERE e.Checkpoint = 'Processing'
      AND CAST(e.Payload->>'$.endTemperatureC' AS DECIMAL(5,2)) < 72.00
    UNION ALL
    SELECT e.BatchId, e.Id, e.Checkpoint, e.OccurredAt,
           'TemperatureHigh', 'EndTemperatureC',
           CAST(e.Payload->>'$.endTemperatureC' AS DECIMAL(5,2)), 75.00,
           'Above the 72-75C pasteurisation range'
    FROM timeline_events e
    WHERE e.Checkpoint = 'Processing'
      AND CAST(e.Payload->>'$.endTemperatureC' AS DECIMAL(5,2)) > 75.00
    UNION ALL
    SELECT e.BatchId, e.Id, e.Checkpoint, e.OccurredAt,
           'PhLow', 'PH', CAST(e.Payload->>'$.ph' AS DECIMAL(5,2)), 6.60,
           'Below 6.6; acidity developed'
    FROM timeline_events e
    WHERE e.Checkpoint = 'Lab'
      AND CAST(e.Payload->>'$.ph' AS DECIMAL(5,2)) < 6.60
    UNION ALL
    SELECT e.BatchId, e.Id, e.Checkpoint, e.OccurredAt,
           'PhHigh', 'PH', CAST(e.Payload->>'$.ph' AS DECIMAL(5,2)), 6.80,
           'Above 6.8'
    FROM timeline_events e
    WHERE e.Checkpoint = 'Lab'
      AND CAST(e.Payload->>'$.ph' AS DECIMAL(5,2)) > 6.80
    UNION ALL
    SELECT e.BatchId, e.Id, e.Checkpoint, e.OccurredAt,
           'SensoryAbnormal', 'Sensory', NULL, NULL,
           CONCAT('Abnormal: ', CONCAT_WS(', ',
               IF(e.Payload->>'$.appearance' = 'Abnormal', 'appearance', NULL),
               IF(e.Payload->>'$.texture'    = 'Abnormal', 'texture',    NULL),
               IF(e.Payload->>'$.taste'      = 'Abnormal', 'taste',      NULL),
               IF(e.Payload->>'$.colour'     = 'Abnormal', 'colour',     NULL),
               IF(e.Payload->>'$.smell'      = 'Abnormal', 'smell',      NULL)))
    FROM timeline_events e
    WHERE e.Checkpoint = 'Lab'
      AND 'Abnormal' IN (e.Payload->>'$.appearance', e.Payload->>'$.texture',
                         e.Payload->>'$.taste', e.Payload->>'$.colour', e.Payload->>'$.smell')
) d
-- Joined once, outside the UNION: MySQL cannot open a temporary table twice in
-- one statement.
JOIN seed_batch s ON s.BatchId = d.BatchId;

UPDATE timeline_events e
JOIN deviation_records d ON d.TimelineEventId = e.Id
JOIN seed_batch s ON s.BatchId = d.BatchId
SET e.IsDeviation = 1;

-- ---------------------------------------------------------------------------
-- Daily aggregates
-- ---------------------------------------------------------------------------
-- What the consumer would have counted from the rows above, per facility,
-- production day and product line. Pending is anything not yet decided and not
-- held: Chilling, Processing or AwaitingLab.

INSERT INTO daily_quality_aggregates
    (Date, ProductLine, Facility, BatchCount,
     PendingCount, ClearedCount, FailedCount, OnHoldCount, DeviationCount, UpdatedAt)
SELECT s.ProductionDate, r.ProductLine, r.Facility,
       COUNT(*),
       SUM(r.Status IN ('Chilling', 'Processing', 'AwaitingLab')),
       SUM(r.Status = 'Cleared'),
       SUM(r.Status = 'Failed'),
       SUM(r.Status = 'OnHold'),
       COALESCE(SUM(dv.Deviations), 0),
       MAX(r.LastEventAt)
FROM batch_records r
JOIN seed_batch s ON s.BatchId = r.BatchId
LEFT JOIN (
    SELECT BatchId, COUNT(*) AS Deviations FROM deviation_records GROUP BY BatchId
) dv ON dv.BatchId = r.BatchId
GROUP BY s.ProductionDate, r.ProductLine, r.Facility;

-- The same deviations broken down by reason. A deviation with no reason would be
-- counted as 'Unspecified' (DailyReasonCodeAggregate.Unspecified); the sample has none.
INSERT INTO daily_reason_code_aggregates
    (Date, ProductLine, Facility, ReasonCode, DeviationCount, UpdatedAt)
SELECT s.ProductionDate, r.ProductLine, r.Facility,
       COALESCE(d.ReasonCode, 'Unspecified'),
       COUNT(*),
       MAX(r.LastEventAt)
FROM deviation_records d
JOIN batch_records r ON r.BatchId = d.BatchId
JOIN seed_batch s ON s.BatchId = d.BatchId
GROUP BY s.ProductionDate, r.ProductLine, r.Facility, COALESCE(d.ReasonCode, 'Unspecified');

COMMIT;

DROP TEMPORARY TABLE seed_batch, seed_consignment;

-- ---------------------------------------------------------------------------
-- Checks
-- ---------------------------------------------------------------------------
-- Expected: 24 batches, 24 snapshots, 84 events, 13 deviations,
-- 21 daily aggregates, 12 reason-code aggregates.
--
--   SELECT
--       (SELECT COUNT(*) FROM batch_records)                AS batches,
--       (SELECT COUNT(*) FROM upstream_snapshots)           AS snapshots,
--       (SELECT COUNT(*) FROM timeline_events)              AS events,
--       (SELECT COUNT(*) FROM deviation_records)            AS deviations,
--       (SELECT COUNT(*) FROM daily_quality_aggregates)     AS aggregates,
--       (SELECT COUNT(*) FROM daily_reason_code_aggregates) AS reason_aggregates;
--
-- Batches per product line. Expected: 4 each.
--
--   SELECT ProductLine, COUNT(*) AS batches
--   FROM batch_records
--   GROUP BY ProductLine;
--
-- Batches by status for the facility. Expected: pending 6, cleared 12,
-- failed 4, on hold 2.
--
--   SELECT SUM(PendingCount) AS pending, SUM(ClearedCount) AS cleared,
--          SUM(FailedCount) AS failed, SUM(OnHoldCount) AS on_hold
--   FROM daily_quality_aggregates
--   WHERE Facility = 'FACTORY-01';
--
-- Pass rate. These two disagree on purpose, and which number the dashboard
-- shows tells you whether undecided batches are being counted:
--   over decided batches only -> 75.00   (12 of 16; correct)
--   over every batch          -> 50.00   (12 of 24; the 8 pending or held
--                                         wrongly in the denominator)
--
--   SELECT
--       ROUND(100.0 * SUM(ClearedCount)
--             / SUM(ClearedCount + FailedCount), 2) AS pass_rate_decided,
--       ROUND(100.0 * SUM(ClearedCount)
--             / SUM(BatchCount), 2)                 AS pass_rate_all
--   FROM daily_quality_aggregates
--   WHERE Facility = 'FACTORY-01';
--
-- Deviations by reason. Expected: TemperatureHigh 5, SensoryAbnormal 4,
-- PhLow 2, PhHigh 1, TemperatureLow 1.
--
--   SELECT ReasonCode, SUM(DeviationCount) AS deviations
--   FROM daily_reason_code_aggregates
--   WHERE Facility = 'FACTORY-01'
--   GROUP BY ReasonCode
--   ORDER BY deviations DESC;
--
-- Backward trace for the failed batch: first event flagged as a deviation, then
-- the deviation with its reading and limit.
--
--   SELECT Checkpoint, OccurredAt, IsDeviation,
--          Payload->>'$.temperatureC' AS temperatureC, Payload->>'$.ph' AS ph
--   FROM timeline_events
--   WHERE BatchId = '262-FM-B'
--   ORDER BY OccurredAt;
--
--   SELECT Checkpoint, ReasonCode, ObservedValue, `Limit`
--   FROM deviation_records
--   WHERE BatchId = '262-FM-B'
--   ORDER BY OccurredAt;
--
-- Forward trace: which batches did society VS014's milk reach? Expected: 6.
-- The consignments are inside the snapshot's JSON, so this reads every resolved
-- snapshot rather than an index.
--
--   SELECT s.BatchId, c.reference
--   FROM upstream_snapshots s,
--        JSON_TABLE(s.Payload, '$.tanks[*].consignments[*]'
--            COLUMNS (reference varchar(100) PATH '$.reference',
--                     society   varchar(10)  PATH '$.society')) c
--   WHERE c.society = 'VS014'
--   ORDER BY s.BatchId;
