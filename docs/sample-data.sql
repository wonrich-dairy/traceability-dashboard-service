-- Sample data for the traceability dashboard.
--
-- Four batches covering the cases the dashboard and the FR7 backward trace need:
--   262-FM-A  clean pass, all four checkpoints
--   262-FM-B  lab failure with a traceable upstream cause (the demo batch)
--   263-FM-A  in progress, no Processing or Lab rows yet, outcome NULL
--   262-FM-C  second pass, so the pass rate is not 2-of-4 by coincidence
--
-- Batch code = day of year - product code - nth set of that product that day.
-- Day 262 is 2026-09-19 and day 263 is 2026-09-20, so the three completed
-- batches are yesterday's sets A, B and C and the in-progress one is today's
-- set A. The letter restarts each day, which is worth seeing in the data.
--
-- Enum columns store the C# enum *member name*, because the configurations use
-- HasConversion<string>(). Note 'Mcc', not 'MCC' -- it has to match
-- CheckpointName.Mcc exactly or EF will throw when it reads the row back.
--
-- Re-runnable: the deletes below clear these four batches first, and the FK
-- cascade on BatchId takes their sources and checkpoints with them. Id columns
-- are auto-increment and deliberately not specified.

START TRANSACTION;

DELETE FROM batch_traces
WHERE BatchId IN ('262-FM-A', '262-FM-B', '262-FM-C', '263-FM-A');

-- ---------------------------------------------------------------------------
-- Batches
-- ---------------------------------------------------------------------------
-- CreatedAt is the MCC collection time, UpdatedAt the last checkpoint recorded.

INSERT INTO batch_traces
    (BatchId, ProductionDate, CurrentStatus, FinalLabOutcome, CreatedAt, UpdatedAt)
VALUES
    -- Batch 1: clean pass.
    ('262-FM-A', '2026-09-19', 'Released', 'Pass',
     '2026-09-19 05:15:00', '2026-09-19 11:05:00'),

    -- Batch 2: rejected on the lab result. The cause is upstream, at MCC.
    ('262-FM-B', '2026-09-19', 'Rejected', 'Fail',
     '2026-09-19 05:40:00', '2026-09-19 11:30:00'),

    -- Batch 4: second pass.
    ('262-FM-C', '2026-09-19', 'Released', 'Pass',
     '2026-09-19 06:10:00', '2026-09-19 12:00:00'),

    -- Batch 3: in progress. Chilling happens at the MCC and factory intake has
    -- already been recorded, so this batch is past both and sitting at the
    -- processing stage -- hence 'Processing' rather than 'Chilling', even though
    -- there is no Processing checkpoint row yet. The checkpoint is written when
    -- the stage completes; CurrentStatus says which stage the batch is in now.
    -- FinalLabOutcome is genuinely unknown rather than a placeholder.
    ('263-FM-A', '2026-09-20', 'Processing', NULL,
     '2026-09-20 05:20:00', '2026-09-20 07:35:00');

-- ---------------------------------------------------------------------------
-- Source consignments
-- ---------------------------------------------------------------------------
-- A tank blends several village societies, so a batch has more than one source
-- and a society appears in more than one batch. VS014 feeds 262-FM-A and
-- 262-FM-C; VS022 feeds 262-FM-B and 263-FM-A. That overlap is what makes a
-- forward trace ("which batches did this society's milk reach?") non-trivial.

INSERT INTO batch_sources (BatchId, ConsignmentReference)
VALUES
    -- Batch 1: two consignments.
    ('262-FM-A', 'CON-20260919-VS014'),
    ('262-FM-A', 'CON-20260919-VS031'),

    -- Batch 2: three, so the backward trace has something to fan out to.
    ('262-FM-B', 'CON-20260919-VS009'),
    ('262-FM-B', 'CON-20260919-VS022'),
    ('262-FM-B', 'CON-20260919-VS045'),

    -- Batch 4.
    ('262-FM-C', 'CON-20260919-VS014'),
    ('262-FM-C', 'CON-20260919-VS038'),

    -- Batch 3.
    ('263-FM-A', 'CON-20260920-VS022'),
    ('263-FM-A', 'CON-20260920-VS031');

-- ---------------------------------------------------------------------------
-- Checkpoint records
-- ---------------------------------------------------------------------------
-- Stage order, which the timestamps below follow: milk is collected and chilled
-- at the MCC, then taken in at the factory, then processed, then lab tested.
--
-- Which measurements each checkpoint carries follows the entity comments:
-- Mcc and Intake take fat/SNF/CLR/temperature, Processing takes temperature
-- only, Lab takes pH and the five sensory grades. Everything a checkpoint does
-- not measure stays NULL.
--
-- Reference ranges for reading the numbers below: fat 3.5-4.5%, SNF 8.5-9.0%,
-- CLR 27-30, chilled milk at or below 4C, pasteurisation 72-75C, pH 6.6-6.8.

-- Batch 1 (262-FM-A): everything comfortably in range.
INSERT INTO checkpoint_records
    (BatchId, CheckpointName, RecordedAtTimestamp,
     FatPercentage, SnfPercentage, ClrReading, TemperatureCelsius, PH,
     Appearance, Texture, Taste, Colour, Smell)
VALUES
    ('262-FM-A', 'Mcc',        '2026-09-19 05:15:00',
     4.10, 8.72, 28.40, 3.80, NULL, NULL, NULL, NULL, NULL, NULL),
    ('262-FM-A', 'Intake',     '2026-09-19 07:40:00',
     4.05, 8.68, 28.20, 4.60, NULL, NULL, NULL, NULL, NULL, NULL),
    ('262-FM-A', 'Processing', '2026-09-19 09:20:00',
     NULL, NULL, NULL, 73.50, NULL, NULL, NULL, NULL, NULL, NULL),
    ('262-FM-A', 'Lab',        '2026-09-19 11:05:00',
     NULL, NULL, NULL, NULL, 6.68, 'Normal', 'Normal', 'Normal', 'Normal', 'Normal');

-- Batch 2 (262-FM-B): the demo batch. The chiller at the collection centre was
-- down, so the milk left MCC at 12.4C instead of under 4C and still arrived at
-- 9.8C. Pasteurisation itself ran normally -- which is the point: the deviation
-- is upstream of the step that looks fine. By the lab, acidity has developed
-- (pH 6.32) and taste, texture and smell are off, so the batch fails.
-- Opening this batch should show a QCO the temperature at Mcc as the first
-- thing out of range.
INSERT INTO checkpoint_records
    (BatchId, CheckpointName, RecordedAtTimestamp,
     FatPercentage, SnfPercentage, ClrReading, TemperatureCelsius, PH,
     Appearance, Texture, Taste, Colour, Smell)
VALUES
    ('262-FM-B', 'Mcc',        '2026-09-19 05:40:00',
     3.95, 8.55, 27.90, 12.40, NULL, NULL, NULL, NULL, NULL, NULL),
    ('262-FM-B', 'Intake',     '2026-09-19 08:05:00',
     3.90, 8.48, 27.60, 9.80, NULL, NULL, NULL, NULL, NULL, NULL),
    ('262-FM-B', 'Processing', '2026-09-19 09:55:00',
     NULL, NULL, NULL, 73.20, NULL, NULL, NULL, NULL, NULL, NULL),
    ('262-FM-B', 'Lab',        '2026-09-19 11:30:00',
     NULL, NULL, NULL, NULL, 6.32, 'Normal', 'Abnormal', 'Abnormal', 'Normal', 'Abnormal');

-- Batch 4 (262-FM-C): second clean pass.
INSERT INTO checkpoint_records
    (BatchId, CheckpointName, RecordedAtTimestamp,
     FatPercentage, SnfPercentage, ClrReading, TemperatureCelsius, PH,
     Appearance, Texture, Taste, Colour, Smell)
VALUES
    ('262-FM-C', 'Mcc',        '2026-09-19 06:10:00',
     4.22, 8.80, 28.70, 3.60, NULL, NULL, NULL, NULL, NULL, NULL),
    ('262-FM-C', 'Intake',     '2026-09-19 08:35:00',
     4.18, 8.76, 28.50, 4.20, NULL, NULL, NULL, NULL, NULL, NULL),
    ('262-FM-C', 'Processing', '2026-09-19 10:25:00',
     NULL, NULL, NULL, 74.10, NULL, NULL, NULL, NULL, NULL, NULL),
    ('262-FM-C', 'Lab',        '2026-09-19 12:00:00',
     NULL, NULL, NULL, NULL, 6.71, 'Normal', 'Normal', 'Normal', 'Normal', 'Normal');

-- Batch 3 (263-FM-A): in progress. Two checkpoints only -- no Processing row,
-- no Lab row. The dashboard has to render this batch without them.
INSERT INTO checkpoint_records
    (BatchId, CheckpointName, RecordedAtTimestamp,
     FatPercentage, SnfPercentage, ClrReading, TemperatureCelsius, PH,
     Appearance, Texture, Taste, Colour, Smell)
VALUES
    ('263-FM-A', 'Mcc',    '2026-09-20 05:20:00',
     4.02, 8.65, 28.10, 3.90, NULL, NULL, NULL, NULL, NULL, NULL),
    ('263-FM-A', 'Intake', '2026-09-20 07:35:00',
     3.98, 8.60, 27.95, 4.40, NULL, NULL, NULL, NULL, NULL, NULL);

COMMIT;

-- ---------------------------------------------------------------------------
-- Checks
-- ---------------------------------------------------------------------------
-- Expected: 4 batches, 9 sources, 14 checkpoints.
--
--   SELECT
--       (SELECT COUNT(*) FROM batch_traces)      AS batches,
--       (SELECT COUNT(*) FROM batch_sources)     AS sources,
--       (SELECT COUNT(*) FROM checkpoint_records) AS checkpoints;
--
-- Pass rate. These two disagree on purpose, and which number the dashboard
-- shows tells you whether the in-progress batch is being counted:
--   over tested batches only -> 66.67   (correct)
--   over every batch         -> 50.00   (263-FM-A wrongly in the denominator)
--
--   SELECT
--       ROUND(100.0 * SUM(FinalLabOutcome = 'Pass')
--             / COUNT(FinalLabOutcome), 2) AS pass_rate_tested,
--       ROUND(100.0 * SUM(FinalLabOutcome = 'Pass')
--             / COUNT(*), 2)               AS pass_rate_all
--   FROM batch_traces;
--
-- Backward trace for the failed batch: first measurement out of range.
--
--   SELECT CheckpointName, RecordedAtTimestamp, TemperatureCelsius, PH
--   FROM checkpoint_records
--   WHERE BatchId = '262-FM-B'
--   ORDER BY RecordedAtTimestamp;
