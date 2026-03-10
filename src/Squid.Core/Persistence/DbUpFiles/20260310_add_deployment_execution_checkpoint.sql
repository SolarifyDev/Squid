CREATE TABLE IF NOT EXISTS deployment_execution_checkpoint (
    id SERIAL PRIMARY KEY,
    server_task_id INTEGER NOT NULL,
    deployment_id INTEGER NOT NULL,
    last_completed_batch_index INTEGER NOT NULL,
    failure_encountered BOOLEAN NOT NULL DEFAULT FALSE,
    output_variables_json TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_deployment_execution_checkpoint_server_task_id
    ON deployment_execution_checkpoint (server_task_id);
