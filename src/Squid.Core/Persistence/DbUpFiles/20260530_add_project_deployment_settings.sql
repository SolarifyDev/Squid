-- Project-level deployment settings (DeploymentSettingsDto) as a JSON blob — the
-- "Transient Deployment Targets" behaviour today, room to grow without a per-setting
-- migration. NULL = all defaults, which preserve today's runtime behaviour (unavailable
-- targets skipped, unhealthy targets excluded). Pre-existing projects get NULL.

ALTER TABLE project
    ADD COLUMN deployment_settings_json jsonb NULL;
