ALTER TABLE server_task_log
    ADD COLUMN IF NOT EXISTS activity_node_id BIGINT;

ALTER TABLE server_task_log
    ADD COLUMN IF NOT EXISTS detail TEXT;

CREATE INDEX IF NOT EXISTS ix_server_task_log_task_node_seq
    ON server_task_log (server_task_id, activity_node_id, sequence_number);
