CREATE INDEX IF NOT EXISTS ix_server_task_concurrency_tag_state
    ON server_task (concurrency_tag, state)
    WHERE concurrency_tag IS NOT NULL;
