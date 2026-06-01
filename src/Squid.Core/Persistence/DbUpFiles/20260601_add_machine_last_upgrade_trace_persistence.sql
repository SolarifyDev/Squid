-- Durable upgrade trace — persist the LAST TERMINAL upgrade outcome (status +
-- event timeline + Phase B log) to the DB so a server pod restart no longer
-- erases an operator's view of how the most recent upgrade concluded.
--
-- Background: the per-machine upgrade timeline lives in a process-local
-- singleton (InMemoryUpgradeEventTimelineStore). That cache is intentionally
-- in-memory because writing it on EVERY Capabilities probe (every few seconds
-- during an active upgrade) would dominate the cost of the upgrade itself.
-- This migration adds a durable backstop that is written ONCE per upgrade —
-- only when the agent first reports a TERMINAL status (SUCCESS / FAILED /
-- ROLLED_BACK / ...). The in-memory store stays the hot read cache; these
-- columns are hydrated back into it at server startup.
--
-- Adds two columns to `machine`:
--   * last_upgrade_trace_json — full UpgradeTraceSnapshot (status payload +
--     event list + Phase B log) as JSON (jsonb for efficient ->> queries from
--     future admin filters). NULL means "no terminal upgrade ever observed" —
--     the cache stays empty for that machine until its next upgrade concludes.
--   * last_upgrade_trace_updated_at — when the terminal snapshot was persisted.
--
-- Backward compatibility: pre-existing machines get NULL columns. The
-- UpgradeTraceHydrator IStartable skips NULL rows at startup, so a machine
-- that never upgraded simply has an empty upgrade timeline (same as today).

ALTER TABLE machine
    ADD COLUMN last_upgrade_trace_json       jsonb       NULL,
    ADD COLUMN last_upgrade_trace_updated_at timestamptz NULL;

-- Optional partial index for future "which machines upgraded recently" admin
-- queries. WHERE clause keeps it small for the common case where most fleets
-- have only a handful of machines with a recorded terminal upgrade.
CREATE INDEX ix_machine_last_upgrade_trace_updated_at
    ON machine USING btree (last_upgrade_trace_updated_at DESC)
    WHERE last_upgrade_trace_updated_at IS NOT NULL;
