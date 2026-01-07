CREATE TABLE IF NOT EXISTS "deployment"
(
    id                      UUID PRIMARY KEY,
    name                    VARCHAR(200) NOT NULL,
    task_id                 UUID,
    space_id                UUID         NOT NULL,
    channel_id              UUID         NOT NULL,
    project_id              UUID         NOT NULL,
    release_id              UUID         NOT NULL,
    environment_id          UUID         NOT NULL,
    json                    TEXT         NOT NULL,
    deployed_by             VARCHAR(200) NOT NULL,
    deployed_to_machine_ids TEXT         NOT NULL,
    created                 TIMESTAMPTZ  NOT NULL
);

CREATE TABLE IF NOT EXISTS "deployment_environment"
(
    id            UUID PRIMARY KEY,
    name          VARCHAR(200)                                 NOT NULL,
    sort_order    INT                                          NOT NULL,
    json          TEXT                                         NOT NULL,
    data_version  BYTEA                                        NOT NULL,
    space_id      UUID                                         NOT NULL,
    slug          VARCHAR(210)                                 NOT NULL,
    type          VARCHAR(50)                                  NOT NULL,
    last_modified TIMESTAMPTZ DEFAULT '0001-01-01 00:00:00+00' NOT NULL
);

CREATE TABLE IF NOT EXISTS "space"
(
    id                 UUID PRIMARY KEY,
    name               VARCHAR(20)           NOT NULL,
    slug               VARCHAR(20)           NOT NULL,
    is_default         BOOLEAN               NOT NULL,
    json               TEXT                  NOT NULL,
    task_queue_stopped BOOLEAN               NOT NULL,
    data_version       BYTEA                 NOT NULL,
    last_modified      TIMESTAMPTZ           NOT NULL,
    is_private         BOOLEAN DEFAULT FALSE NOT NULL
);

CREATE TABLE IF NOT EXISTS "release"
(
    id                                     UUID PRIMARY KEY,
    version                                VARCHAR(349) NOT NULL,
    assembled                              TIMESTAMPTZ  NOT NULL,
    project_id                             UUID         NOT NULL,
    project_variable_set_snapshot_id       UUID         NOT NULL,
    project_deployment_process_snapshot_id UUID         NOT NULL,
    json                                   TEXT         NOT NULL,
    channel_id                             UUID         NOT NULL,
    data_version                           BYTEA        NOT NULL,
    space_id                               UUID         NOT NULL,
    last_modified                          TIMESTAMPTZ  NOT NULL
);

CREATE TABLE IF NOT EXISTS "deployment_process"
(
    id        UUID PRIMARY KEY,
    owner_id  UUID    NOT NULL,
    is_frozen BOOLEAN NOT NULL,
    version   INT     NOT NULL,
    json      TEXT    NOT NULL,
    space_id  UUID    NOT NULL
);

CREATE TABLE IF NOT EXISTS "channel"
(
    id            UUID PRIMARY KEY,
    name          VARCHAR(200) NOT NULL,
    project_id    UUID         NOT NULL,
    lifecycle_id  UUID,
    json          TEXT         NOT NULL,
    data_version  BYTEA        NOT NULL,
    space_id      UUID         NOT NULL,
    slug          VARCHAR(210) NOT NULL,
    last_modified TIMESTAMPTZ  NOT NULL
);

CREATE TABLE IF NOT EXISTS "project"
(
    id                                UUID PRIMARY KEY,
    name                              VARCHAR(200)                                 NOT NULL,
    slug                              VARCHAR(210)                                 NOT NULL,
    is_disabled                       BOOLEAN                                      NOT NULL,
    variable_set_id                   UUID                                         NOT NULL,
    deployment_process_id             UUID                                         NOT NULL,
    project_group_id                  UUID                                         NOT NULL,
    lifecycle_id                      UUID                                         NOT NULL,
    auto_create_release               BOOLEAN                                      NOT NULL,
    json                              TEXT                                         NOT NULL,
    included_library_variable_set_ids TEXT                                         NOT NULL,
    discrete_channel_release          BOOLEAN                                      NOT NULL,
    data_version                      BYTEA                                        NOT NULL,
    cloned_from_project_id            UUID,
    space_id                          UUID                                         NOT NULL,
    last_modified                     TIMESTAMPTZ DEFAULT '0001-01-01 00:00:00+00' NOT NULL,
    allow_ignore_channel_rules        BOOLEAN     DEFAULT TRUE                     NOT NULL
);

CREATE TABLE IF NOT EXISTS "library_variable_set"
(
    id              UUID PRIMARY KEY,
    name            VARCHAR(200)  NOT NULL,
    variable_set_id UUID,
    content_type    VARCHAR(50)   NOT NULL,
    json            TEXT          NOT NULL,
    space_id        UUID          NOT NULL,
    version         INT DEFAULT 0 NOT NULL
);

CREATE TABLE IF NOT EXISTS "machine"
(
    id                     UUID PRIMARY KEY,
    name                   VARCHAR(200)                   NOT NULL,
    is_disabled            BOOLEAN                        NOT NULL,
    roles                  TEXT                           NOT NULL,
    environment_ids        TEXT                           NOT NULL,
    json                   TEXT                           NOT NULL,
    machine_policy_id      UUID,
    thumbprint             VARCHAR(50),
    fingerprint            VARCHAR(50),
    deployment_target_type VARCHAR(50)                    NOT NULL,
    data_version           BYTEA                          NOT NULL,
    space_id               UUID                           NOT NULL,
    operating_system       VARCHAR(400) DEFAULT 'Unknown' NOT NULL,
    shell_name             VARCHAR(400) DEFAULT 'Unknown' NOT NULL,
    shell_version          VARCHAR(400) DEFAULT 'Unknown' NOT NULL,
    license_hash           TEXT,
    slug                   VARCHAR(210)                   NOT NULL
);

CREATE TABLE IF NOT EXISTS "deployment_completion"
(
    id             UUID PRIMARY KEY,
    sequence_number BIGSERIAL,
    deployment_id   UUID        NOT NULL,
    state          VARCHAR(50) NOT NULL,
    completed_time  TIMESTAMPTZ NOT NULL,
    space_id        UUID        NOT NULL
);

CREATE TABLE IF NOT EXISTS "server_task"
(
    id                     UUID                                         NOT NULL PRIMARY KEY,
    name                   VARCHAR(50)                                  NOT NULL,
    description            TEXT                                         NOT NULL,
    queue_time             TIMESTAMPTZ                                  NOT NULL,
    start_time             TIMESTAMPTZ,
    completed_time         TIMESTAMPTZ,
    error_message          TEXT,
    concurrency_tag        VARCHAR(100),
    state                  VARCHAR(50)                                  NOT NULL,
    has_warnings_or_errors BOOLEAN                                      NOT NULL,
    server_node_id         UUID,
    project_id             UUID,
    environment_id         UUID,
    duration_seconds       INT                                          NOT NULL,
    data_version           BYTEA                                        NOT NULL,
    space_id               UUID,
    last_modified          TIMESTAMPTZ DEFAULT '0001-01-01 00:00:00+00' NOT NULL,
    business_process_state VARCHAR(10) DEFAULT 'InProgress'             NOT NULL,
    server_task_type       VARCHAR(50) DEFAULT 'ServerTask'             NOT NULL,
    parent_server_task_id  UUID,
    priority_time          TIMESTAMPTZ,
    state_order            INT         DEFAULT 9                        NOT NULL,
    weight                 INT         DEFAULT 0                        NOT NULL,
    batch_id               VARCHAR(50)
);

CREATE TABLE IF NOT EXISTS "lifecycle"
(
    id           UUID PRIMARY KEY,
    name         varchar(200) NOT NULL,
    json         text         NOT NULL,
    data_version bytea        NOT NULL,
    space_id     UUID         NOT NULL,
    slug         varchar(210) NOT NULL
);