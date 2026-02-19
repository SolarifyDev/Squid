CREATE TABLE IF NOT EXISTS "phase"
(
    id                                    UUID PRIMARY KEY,
    lifecycle_id                          UUID         NOT NULL,
    Name                                  varchar(200) NOT NULL,
    automatic_deployment_targets          text         NOT NULL,
    optional_deployment_targets           text         NOT NULL,
    minimum_environments_before_promotion INT          NOT NULL,
    is_optional_phase                     BOOLEAN      NOT NULL,
    is_priority_phase                     BOOLEAN      NOT NULL,
    release_retention_policy_id           UUID         NOT NULL,
    tentacle_retention_policy_id          UUID         NOT NULL
);

CREATE TABLE IF NOT EXISTS "retention_policy"
(
    id                  UUID PRIMARY KEY,
    unit                INT     NOT NULL,
    quantity_to_keep    INT     NOT NULL,
    should_keep_forever BOOLEAN NOT NULL
);

ALTER TABLE "lifecycle" ADD COLUMN release_retention_policy_id UUID;
ALTER TABLE "lifecycle" ADD COLUMN tentacle_retention_policy_id UUID;
ALTER TABLE "lifecycle" DROP COLUMN json;