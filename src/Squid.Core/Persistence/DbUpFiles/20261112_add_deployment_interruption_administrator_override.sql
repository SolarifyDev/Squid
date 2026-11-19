-- Audit trail for the administrator approval override: who resolved a manual intervention on behalf of
-- its responsible teams without being a member. Nullable and additive: existing rows stay NULL (resolved
-- normally), so the migration cannot fail on existing data.
ALTER TABLE deployment_interruption
    ADD COLUMN IF NOT EXISTS administrator_override_user_id integer NULL;

ALTER TABLE deployment_interruption
    ADD COLUMN IF NOT EXISTS administrator_override_user_name text NULL;
