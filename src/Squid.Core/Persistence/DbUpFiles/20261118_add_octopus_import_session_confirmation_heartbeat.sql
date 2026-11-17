-- Fencing token of the confirmation attempt that holds an Importing session, its liveness heartbeat (written on a separate
-- connection, never changes data_version) and the last rolled-back or interrupted attempt the page can import again from.
-- Nullable, no backfill: an Importing row started by an older server has no heartbeat and falls under the legacy grace period.
ALTER TABLE octopus_import_session ADD COLUMN IF NOT EXISTS confirmation_attempt_id uuid NULL;
ALTER TABLE octopus_import_session ADD COLUMN IF NOT EXISTS import_heartbeat_at timestamptz NULL;
ALTER TABLE octopus_import_session ADD COLUMN IF NOT EXISTS last_confirmation_failure_json jsonb NULL;

CREATE INDEX IF NOT EXISTS ix_octopus_import_session_importing_heartbeat ON octopus_import_session (import_heartbeat_at) WHERE state = 'Importing';
