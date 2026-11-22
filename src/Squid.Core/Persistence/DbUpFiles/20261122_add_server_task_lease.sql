-- Liveness lease of the process that runs a server task's pipeline: the owner (node/process id) and the last renewal, written on the
-- database clock so servers with skewed clocks agree. The stale-task reaper only recovers a leased Executing/Cancelling row whose renewal stopped.
-- Nullable, no backfill: a row from before this migration, or claimed by an older server during a rolling upgrade, has no lease and is never reaped.
ALTER TABLE server_task ADD COLUMN IF NOT EXISTS lease_owner varchar(200) NULL;
ALTER TABLE server_task ADD COLUMN IF NOT EXISTS lease_renewed_at timestamptz NULL;

CREATE INDEX IF NOT EXISTS ix_server_task_active_lease ON server_task (lease_renewed_at) WHERE lease_renewed_at IS NOT NULL AND state IN ('Executing', 'Cancelling');
