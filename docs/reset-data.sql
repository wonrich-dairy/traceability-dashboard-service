-- Tears down what docs/sample-data.sql inserts.
--
-- Children first, parents last: deviation_records hangs off timeline_events, and
-- timeline_events and upstream_snapshots hang off batch_records.BatchId, so they
-- go before it. The FK cascades would take them anyway, but deleting them
-- explicitly means this script does not depend on the cascades staying
-- configured the way they are today.
--
-- Scoped to the 24 sample batches and the aggregate rows (both aggregate tables)
-- for their facility, days and product lines, so it leaves any other data
-- alone. Safe to run when the rows are already gone, and safe to run twice. The
-- batch list and facility must match seed_batch in docs/sample-data.sql.

DROP TEMPORARY TABLE IF EXISTS sample_batch;

CREATE TEMPORARY TABLE sample_batch (
    BatchId        varchar(50) PRIMARY KEY,
    Facility       varchar(50) NOT NULL DEFAULT 'FACTORY-01',
    ProductLine    varchar(10)
        AS (SUBSTRING_INDEX(SUBSTRING_INDEX(BatchId, '-', 2), '-', -1)) STORED,
    ProductionDate date
        AS (MAKEDATE(2026, CAST(SUBSTRING_INDEX(BatchId, '-', 1) AS UNSIGNED))) STORED
);

INSERT INTO sample_batch (BatchId)
VALUES
    ('259-SY-A'), ('259-SK-A'), ('259-CD-A'),
    ('260-FLM-A'), ('260-SY-A'), ('260-DY-A'), ('260-CD-A'),
    ('261-FLM-A'), ('261-SY-A'), ('261-SK-A'), ('261-SK-B'), ('261-DY-A'),
    ('262-FM-A'), ('262-FM-B'), ('262-FM-C'), ('262-FLM-A'), ('262-SK-A'), ('262-DY-A'), ('262-CD-A'),
    ('263-FM-A'), ('263-FLM-A'), ('263-SY-A'), ('263-DY-A'), ('263-CD-A');

START TRANSACTION;

DELETE FROM deviation_records
WHERE BatchId IN (SELECT BatchId FROM sample_batch);

DELETE FROM timeline_events
WHERE BatchId IN (SELECT BatchId FROM sample_batch);

DELETE FROM upstream_snapshots
WHERE BatchId IN (SELECT BatchId FROM sample_batch);

DELETE FROM batch_records
WHERE BatchId IN (SELECT BatchId FROM sample_batch);

DELETE FROM daily_quality_aggregates
WHERE (Facility, Date, ProductLine) IN (SELECT Facility, ProductionDate, ProductLine FROM sample_batch);

DELETE FROM daily_reason_code_aggregates
WHERE (Facility, Date, ProductLine) IN (SELECT Facility, ProductionDate, ProductLine FROM sample_batch);

COMMIT;

DROP TEMPORARY TABLE sample_batch;

-- ---------------------------------------------------------------------------
-- Checks
-- ---------------------------------------------------------------------------
-- Expected: all 0 if the sample batches were the only data present.
--
--   SELECT
--       (SELECT COUNT(*) FROM batch_records)                AS batches,
--       (SELECT COUNT(*) FROM upstream_snapshots)           AS snapshots,
--       (SELECT COUNT(*) FROM timeline_events)              AS events,
--       (SELECT COUNT(*) FROM deviation_records)            AS deviations,
--       (SELECT COUNT(*) FROM daily_quality_aggregates)     AS aggregates,
--       (SELECT COUNT(*) FROM daily_reason_code_aggregates) AS reason_aggregates;
--
-- To clear the tables completely rather than just the sample data, swap the
-- statements above for these -- same order, no WHERE clause:
--
--   DELETE FROM deviation_records;
--   DELETE FROM timeline_events;
--   DELETE FROM upstream_snapshots;
--   DELETE FROM batch_records;
--   DELETE FROM daily_quality_aggregates;
--   DELETE FROM daily_reason_code_aggregates;
--
-- processed_messages and audit_entries are not touched: the sample data adds
-- nothing to them, and clearing processed_messages would let consumers apply
-- already-handled events a second time.
--
-- Neither form resets AUTO_INCREMENT, so reloading the sample data gives the
-- same rows with higher Ids. Nothing keys off Id except deviation_records,
-- which the sample script fills by joining to the events it has just inserted
-- -- but it is worth knowing if you are eyeballing Ids between runs.
