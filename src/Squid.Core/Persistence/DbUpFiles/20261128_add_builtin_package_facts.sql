-- What inspection found in a pushed package (its file list and detected facts), stored as JSON next to the version. Additive and re-runnable: rows pushed
-- before this column existed keep NULL, which reads as "not inspected".
ALTER TABLE builtin_package ADD COLUMN IF NOT EXISTS facts jsonb NULL;
