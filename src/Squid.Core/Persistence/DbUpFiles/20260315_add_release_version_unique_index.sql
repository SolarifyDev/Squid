CREATE UNIQUE INDEX IF NOT EXISTS ix_release_project_channel_version
    ON "release" (project_id, channel_id, version);
