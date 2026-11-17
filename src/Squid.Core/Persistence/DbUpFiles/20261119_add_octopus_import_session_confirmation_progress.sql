-- Live progress of the running (or last) confirmation attempt, written on a separate connection; never changes data_version.
-- Nullable, no backfill: a session confirmed by an older server simply has no progress to show.
ALTER TABLE octopus_import_session ADD COLUMN IF NOT EXISTS confirmation_progress_json jsonb NULL;
