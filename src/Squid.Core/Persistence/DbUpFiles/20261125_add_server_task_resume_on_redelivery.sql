ALTER TABLE server_task
    ADD COLUMN IF NOT EXISTS resume_on_redelivery boolean NOT NULL DEFAULT false;
