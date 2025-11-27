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
