-- ServerTaskLog: flat log persistence for deployment script output
CREATE TABLE IF NOT EXISTS server_task_log (
    id BIGSERIAL PRIMARY KEY,
    server_task_id INTEGER NOT NULL,
    category INTEGER NOT NULL,
    message_text TEXT,
    source VARCHAR(500),
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    sequence_number BIGINT NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_server_task_log_task_id ON server_task_log (server_task_id);
CREATE INDEX IF NOT EXISTS ix_server_task_log_task_seq ON server_task_log (server_task_id, sequence_number);

-- ActivityLog: structured tree for step/action execution tracking
CREATE TABLE IF NOT EXISTS activity_log (
    id BIGSERIAL PRIMARY KEY,
    server_task_id INTEGER NOT NULL,
    parent_id BIGINT,
    name VARCHAR(500),
    node_type INTEGER NOT NULL,
    category INTEGER,
    status INTEGER,
    log_text TEXT,
    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_at TIMESTAMPTZ,
    sort_order INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_activity_log_task_id ON activity_log (server_task_id);
CREATE INDEX IF NOT EXISTS ix_activity_log_parent_id ON activity_log (parent_id);
