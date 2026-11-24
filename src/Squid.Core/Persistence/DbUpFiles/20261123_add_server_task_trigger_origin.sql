ALTER TABLE server_task
    ADD COLUMN IF NOT EXISTS is_trigger_task boolean NOT NULL DEFAULT false;
