ALTER TABLE "phase" ADD COLUMN IF NOT EXISTS sort_order INT NOT NULL DEFAULT 0;
ALTER TABLE "phase" DROP COLUMN IF EXISTS automatic_deployment_targets;
ALTER TABLE "phase" DROP COLUMN IF EXISTS optional_deployment_targets;

CREATE TABLE IF NOT EXISTS "phase_environment"
(
    phase_id       INT NOT NULL,
    environment_id INT NOT NULL,
    target_type    INT NOT NULL DEFAULT 0,
    PRIMARY KEY (phase_id, environment_id)
);
