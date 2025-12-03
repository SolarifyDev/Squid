ALTER TABLE deployment
ALTER COLUMN deployed_by TYPE integer USING deployed_by::INTEGER;
ALTER TABLE deployment ADD COLUMN machine_id INT NOT NULL;

ALTER TABLE machine DROP COLUMN operating_system;
ALTER TABLE machine ADD COLUMN operating_system INTEGER;

ALTER TABLE machine DROP COLUMN IF EXISTS fingerprint;
ALTER TABLE machine DROP COLUMN IF EXISTS deployment_target_type;
ALTER TABLE machine ADD COLUMN IF NOT EXISTS uri TEXT;
ALTER TABLE machine ADD COLUMN IF NOT EXISTS has_latest_calamari BOOLEAN DEFAULT FALSE;
ALTER TABLE machine ADD COLUMN IF NOT EXISTS endpoint TEXT;

ALTER TABLE variable_set_snapshot DROP COLUMN original_variable_set_id;
ALTER TABLE variable_set_snapshot DROP COLUMN version;

ALTER TABLE variable_set DROP COLUMN content_hash;

ALTER TABLE deployment_action ADD COLUMN IF NOT EXISTS feed_id INTEGER;
ALTER TABLE deployment_action ADD COLUMN IF NOT EXISTS package_id VARCHAR(500);

-- DeploymentProcess schema update to match new entity:
--   C#: Squid.Message.Domain.Deployments.DeploymentProcess
--   Fields: Id (PK), Version, SpaceId, LastModified, LastModifiedBy
--   Relation: Project.DeploymentProcessId -> DeploymentProcess.Id (1:1)

ALTER TABLE deployment_process
    DROP COLUMN IF EXISTS name,
    DROP COLUMN IF EXISTS description,
    DROP COLUMN IF EXISTS created_at,
    DROP COLUMN IF EXISTS created_by,
    DROP COLUMN IF EXISTS project_id;

DROP INDEX IF EXISTS ix_deployment_process_project_id;
DROP INDEX IF EXISTS ix_deployment_process_project_version;

ALTER TABLE deployment_process
    ALTER COLUMN version SET NOT NULL,
    ALTER COLUMN version SET DEFAULT 1;

ALTER TABLE deployment_process
    ALTER COLUMN last_modified SET NOT NULL,
    ALTER COLUMN last_modified SET DEFAULT NOW();
