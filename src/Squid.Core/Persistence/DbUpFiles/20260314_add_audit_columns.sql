-- ==========================================
-- Phase A: Fix TIMESTAMP → TIMESTAMPTZ
-- ==========================================
ALTER TABLE machine ALTER COLUMN health_last_checked TYPE TIMESTAMPTZ
    USING health_last_checked AT TIME ZONE 'UTC';

-- ==========================================
-- Phase B: Rename existing columns
-- ==========================================

-- user_account
ALTER TABLE user_account RENAME COLUMN created_at_utc TO created_date;
ALTER TABLE user_account RENAME COLUMN updated_at_utc TO last_modified_date;

-- project
ALTER TABLE project RENAME COLUMN last_modified TO last_modified_date;

-- deployment_process (drop VARCHAR last_modified_by, re-add as INT later)
ALTER TABLE deployment_process RENAME COLUMN last_modified TO last_modified_date;
ALTER TABLE deployment_process DROP COLUMN last_modified_by;

-- variable (nullable→non-null after backfill)
UPDATE variable SET last_modified_on = NOW() WHERE last_modified_on IS NULL;
ALTER TABLE variable ALTER COLUMN last_modified_on SET NOT NULL;
ALTER TABLE variable RENAME COLUMN last_modified_on TO last_modified_date;
ALTER TABLE variable DROP COLUMN last_modified_by;

-- variable_set (nullable→non-null)
UPDATE variable_set SET last_modified = NOW() WHERE last_modified IS NULL;
ALTER TABLE variable_set ALTER COLUMN last_modified SET NOT NULL;
ALTER TABLE variable_set RENAME COLUMN last_modified TO last_modified_date;

-- environment
UPDATE environment SET last_modified_on = NOW() WHERE last_modified_on IS NULL;
ALTER TABLE environment ALTER COLUMN last_modified_on SET NOT NULL;
ALTER TABLE environment RENAME COLUMN last_modified_on TO last_modified_date;
ALTER TABLE environment DROP COLUMN last_modified_by;

-- external_feed
UPDATE external_feed SET last_modified_on = NOW() WHERE last_modified_on IS NULL;
ALTER TABLE external_feed ALTER COLUMN last_modified_on SET NOT NULL;
ALTER TABLE external_feed RENAME COLUMN last_modified_on TO last_modified_date;
ALTER TABLE external_feed DROP COLUMN last_modified_by;

-- certificate
UPDATE certificate SET last_modified_on = NOW() WHERE last_modified_on IS NULL;
ALTER TABLE certificate ALTER COLUMN last_modified_on SET NOT NULL;
ALTER TABLE certificate RENAME COLUMN last_modified_on TO last_modified_date;
ALTER TABLE certificate DROP COLUMN last_modified_by;

-- release
ALTER TABLE release RENAME COLUMN assembled TO created_date;
ALTER TABLE release RENAME COLUMN last_modified TO last_modified_date;

-- deployment
ALTER TABLE deployment RENAME COLUMN created TO created_date;

-- deployment_step
ALTER TABLE deployment_step RENAME COLUMN created_at TO created_date;

-- deployment_action
ALTER TABLE deployment_action RENAME COLUMN created_at TO created_date;

-- space
ALTER TABLE space RENAME COLUMN last_modified TO last_modified_date;

-- server_task
ALTER TABLE server_task RENAME COLUMN last_modified TO last_modified_date;

-- user_account_api_key
ALTER TABLE user_account_api_key RENAME COLUMN created_at_utc TO created_date;

-- ==========================================
-- Phase C: Add missing audit columns
-- ==========================================

-- Tables that already had some audit fields (add the missing ones)
ALTER TABLE user_account ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE user_account ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE project ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE project ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE project ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE deployment_process ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE deployment_process ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE deployment_process ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE variable ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE variable ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE variable ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE variable_set ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE variable_set ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE variable_set ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE environment ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE environment ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE environment ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE external_feed ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE external_feed ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE external_feed ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE certificate ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE certificate ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE certificate ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE release ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE release ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE deployment ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE deployment ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE deployment ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE deployment_step ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE deployment_step ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE deployment_step ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE deployment_action ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE deployment_action ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE deployment_action ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE space ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE space ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE space ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE channel ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE channel ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE channel ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE channel ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE server_task ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE server_task ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE server_task ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE user_account_api_key ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE user_account_api_key ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE user_account_api_key ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

-- Tables with ALL 4 new columns
ALTER TABLE machine ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE machine ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE machine ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE machine ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE deployment_account ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE deployment_account ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE deployment_account ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE deployment_account ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE lifecycle ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE lifecycle ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE lifecycle ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE lifecycle ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE phase ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE phase ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE phase ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE phase ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE machine_policy ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE machine_policy ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE machine_policy ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE machine_policy ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE project_group ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE project_group ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE project_group ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE project_group ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;

ALTER TABLE library_variable_set ADD COLUMN created_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE library_variable_set ADD COLUMN created_by INT NOT NULL DEFAULT 8888;
ALTER TABLE library_variable_set ADD COLUMN last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE library_variable_set ADD COLUMN last_modified_by INT NOT NULL DEFAULT 8888;
