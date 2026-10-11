-- Removes what perf/seed-batches.sql inserts: every PERF- batch, and the aggregate rows
-- of their facility, FACTORY-PERF. The FK cascade takes the batches' timeline events,
-- snapshots and deviations. Leaves every other batch and facility alone, and is safe to
-- run twice.

DELETE FROM batch_records WHERE BatchId LIKE 'PERF-%';
DELETE FROM daily_quality_aggregates WHERE Facility = 'FACTORY-PERF';
DELETE FROM daily_reason_code_aggregates WHERE Facility = 'FACTORY-PERF';

-- Expected: 0, 0.
--
--   SELECT
--       (SELECT COUNT(*) FROM timeline_events WHERE BatchId LIKE 'PERF-%') AS events,
--       (SELECT COUNT(*) FROM daily_quality_aggregates WHERE Facility = 'FACTORY-PERF') AS aggregates;
