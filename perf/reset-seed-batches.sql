-- Removes what perf/seed-batches.sql inserts: every PERF- batch. The FK cascade takes
-- their timeline events, snapshots and deviations. Leaves every other batch alone, and
-- is safe to run twice.

DELETE FROM batch_records WHERE BatchId LIKE 'PERF-%';

-- Expected: 0.
--
--   SELECT COUNT(*) FROM timeline_events WHERE BatchId LIKE 'PERF-%';
