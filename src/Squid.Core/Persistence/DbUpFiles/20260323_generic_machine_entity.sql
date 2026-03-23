-- Phase 1: Data migration — ensure endpoint JSON contains AgentVersion
UPDATE machine
SET endpoint = jsonb_set(endpoint::jsonb, '{AgentVersion}', to_jsonb(agent_version))::text
WHERE agent_version IS NOT NULL
  AND agent_version != ''
  AND endpoint IS NOT NULL
  AND endpoint != '';

-- Phase 2: Drop target-specific columns
ALTER TABLE machine DROP COLUMN IF EXISTS json;
ALTER TABLE machine DROP COLUMN IF EXISTS thumbprint;
ALTER TABLE machine DROP COLUMN IF EXISTS uri;
ALTER TABLE machine DROP COLUMN IF EXISTS has_latest_calamari;
ALTER TABLE machine DROP COLUMN IF EXISTS operating_system;
ALTER TABLE machine DROP COLUMN IF EXISTS shell_name;
ALTER TABLE machine DROP COLUMN IF EXISTS shell_version;
ALTER TABLE machine DROP COLUMN IF EXISTS polling_subscription_id;
ALTER TABLE machine DROP COLUMN IF EXISTS license_hash;
ALTER TABLE machine DROP COLUMN IF EXISTS agent_version;

-- Phase 3: Drop legacy columns (exist in DB but not in entity)
ALTER TABLE machine DROP COLUMN IF EXISTS fingerprint;
ALTER TABLE machine DROP COLUMN IF EXISTS deployment_target_type;

-- Phase 4: Functional index for SubscriptionId lookups via JSON
CREATE INDEX ix_machine_endpoint_subscription_id
    ON machine ((endpoint::jsonb ->> 'SubscriptionId'))
    WHERE endpoint::jsonb ->> 'SubscriptionId' IS NOT NULL;
