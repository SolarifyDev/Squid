ALTER TABLE deployment_interruption ADD COLUMN interruption_type INTEGER NOT NULL DEFAULT 1;
ALTER TABLE deployment_interruption ADD COLUMN form_json TEXT;
ALTER TABLE deployment_interruption ADD COLUMN submitted_values_json TEXT;
ALTER TABLE deployment_interruption ADD COLUMN responsible_user_id TEXT;
ALTER TABLE server_task ADD COLUMN has_pending_interruptions BOOLEAN NOT NULL DEFAULT FALSE;
