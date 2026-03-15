-- Phase 2: Add IAuditable to remaining 8 entities

-- ==========================================
-- Rename existing columns
-- ==========================================

-- variable_set_snapshot: CreatedAt → CreatedDate, DROP string CreatedBy → re-add as INT
ALTER TABLE variable_set_snapshot RENAME COLUMN created_at TO created_date;
ALTER TABLE variable_set_snapshot DROP COLUMN created_by;

-- deployment_process_snapshot: same pattern
ALTER TABLE deployment_process_snapshot RENAME COLUMN created_at TO created_date;
ALTER TABLE deployment_process_snapshot DROP COLUMN created_by;

-- deployment_execution_checkpoint: CreatedAt → CreatedDate
ALTER TABLE deployment_execution_checkpoint RENAME COLUMN created_at TO created_date;

-- deployment_interruption: CreatedAt → CreatedDate
ALTER TABLE deployment_interruption RENAME COLUMN created_at TO created_date;

-- deployment_environment: LastModified → LastModifiedDate
ALTER TABLE deployment_environment RENAME COLUMN last_modified TO last_modified_date;

-- ==========================================
-- Add missing audit columns
-- ==========================================

-- variable_set_snapshot (has created_date already after rename)
ALTER TABLE variable_set_snapshot ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE variable_set_snapshot ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE variable_set_snapshot ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

-- deployment_process_snapshot (has created_date already after rename)
ALTER TABLE deployment_process_snapshot ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE deployment_process_snapshot ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE deployment_process_snapshot ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

-- deployment_execution_checkpoint (has created_date already after rename)
ALTER TABLE deployment_execution_checkpoint ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE deployment_execution_checkpoint ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE deployment_execution_checkpoint ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

-- deployment_interruption (has created_date already after rename)
ALTER TABLE deployment_interruption ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE deployment_interruption ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE deployment_interruption ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

-- deployment_completion (ALL 4 new)
ALTER TABLE deployment_completion ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE deployment_completion ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE deployment_completion ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE deployment_completion ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

-- activity_log (ALL 4 new)
ALTER TABLE activity_log ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE activity_log ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE activity_log ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE activity_log ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

-- server_task_log (ALL 4 new)
ALTER TABLE server_task_log ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE server_task_log ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE server_task_log ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE server_task_log ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

-- deployment_environment (has last_modified_date already after rename)
ALTER TABLE deployment_environment ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE deployment_environment ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE deployment_environment ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

-- ==========================================
-- Recreate indexes on renamed columns
-- ==========================================
DROP INDEX IF EXISTS ix_snapshot_created;
CREATE INDEX ix_snapshot_created ON variable_set_snapshot (created_date DESC);

DROP INDEX IF EXISTS ix_process_snapshot_created;
CREATE INDEX ix_process_snapshot_created ON deployment_process_snapshot (created_date DESC);
