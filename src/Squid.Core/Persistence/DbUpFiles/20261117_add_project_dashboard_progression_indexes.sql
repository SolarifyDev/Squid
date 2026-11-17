-- Indexes for the project dashboard (GET /projects/{id}/progression).
--
-- The dashboard returns only a recent window of releases per channel plus the releases that
-- hold the current / previous-successful deployment of each environment. An imported Octopus
-- project can carry thousands of releases and deployments, and neither table had any index on
-- these access paths, so every query was a full table scan:
--   1. Most recent N releases of a channel        → (project_id, channel_id, created_date DESC, id DESC)
--   2. Newest deployment per environment          → (project_id, environment_id, created_date DESC, id DESC)
--   3. Deployments of the selected releases       → (release_id)
CREATE INDEX IF NOT EXISTS ix_release_project_channel_created ON release (project_id, channel_id, created_date DESC, id DESC);

CREATE INDEX IF NOT EXISTS ix_deployment_project_environment_created ON deployment (project_id, environment_id, created_date DESC, id DESC);

CREATE INDEX IF NOT EXISTS ix_deployment_release_id ON deployment (release_id);
