ALTER TABLE octopus_import_session
    ADD COLUMN IF NOT EXISTS temporary_upload_path varchar(1024) NULL,
    ADD COLUMN IF NOT EXISTS temporary_upload_size_bytes bigint NULL,
    ADD COLUMN IF NOT EXISTS temporary_upload_cleanup_after timestamptz NULL,
    ADD COLUMN IF NOT EXISTS temporary_upload_cleaned_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS temporary_upload_cleanup_error varchar(1024) NULL;

CREATE INDEX IF NOT EXISTS ix_octopus_import_session_temporary_upload_cleanup_after
    ON octopus_import_session (temporary_upload_cleanup_after);
