-- Tears down what docs/sample-data.sql inserts.
--
-- Children first, parents last: checkpoint_records and batch_sources both hang
-- off batch_traces.BatchId, so they go before it. The FK cascade would take
-- them anyway, but deleting them explicitly means this script does not depend
-- on the cascade staying configured the way it is today.
--
-- Scoped to the four sample batches so it leaves any other data alone. Safe to
-- run when the rows are already gone, and safe to run twice.

START TRANSACTION;

DELETE FROM checkpoint_records
WHERE BatchId IN ('262-FM-A', '262-FM-B', '262-FM-C', '263-FM-A');

DELETE FROM batch_sources
WHERE BatchId IN ('262-FM-A', '262-FM-B', '262-FM-C', '263-FM-A');

DELETE FROM batch_traces
WHERE BatchId IN ('262-FM-A', '262-FM-B', '262-FM-C', '263-FM-A');

COMMIT;

-- ---------------------------------------------------------------------------
-- Checks
-- ---------------------------------------------------------------------------
-- Expected: 0, 0, 0 if the sample batches were the only data present.
--
--   SELECT
--       (SELECT COUNT(*) FROM batch_traces)       AS batches,
--       (SELECT COUNT(*) FROM batch_sources)      AS sources,
--       (SELECT COUNT(*) FROM checkpoint_records) AS checkpoints;
--
-- To clear the tables completely rather than just the sample batches, swap the
-- three statements above for these -- same order, no WHERE clause:
--
--   DELETE FROM checkpoint_records;
--   DELETE FROM batch_sources;
--   DELETE FROM batch_traces;
--
-- Neither form resets AUTO_INCREMENT, so reloading the sample data gives the
-- same rows with higher Ids. Nothing keys off Id -- the tables join on BatchId
-- -- but it is worth knowing if you are eyeballing Ids between runs.
