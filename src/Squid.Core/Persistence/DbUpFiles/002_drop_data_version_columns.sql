-- Remove data_version column from all tables except server_task
-- server_task retains data_version as a concurrency token

ALTER TABLE channel DROP COLUMN IF EXISTS data_version;
ALTER TABLE deployment_environment DROP COLUMN IF EXISTS data_version;
ALTER TABLE lifecycle DROP COLUMN IF EXISTS data_version;
ALTER TABLE machine DROP COLUMN IF EXISTS data_version;
ALTER TABLE project DROP COLUMN IF EXISTS data_version;
ALTER TABLE project_group DROP COLUMN IF EXISTS data_version;
ALTER TABLE release DROP COLUMN IF EXISTS data_version;
ALTER TABLE space DROP COLUMN IF EXISTS data_version;
