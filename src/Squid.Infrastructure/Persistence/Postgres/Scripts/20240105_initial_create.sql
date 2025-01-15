CREATE TABLE IF NOT EXISTS "deployment"
(
    id                      VARCHAR(50)  PRIMARY KEY,
    name                    VARCHAR(200) NOT NULL,
    task_id                 VARCHAR(50),
    space_id                VARCHAR(50)  NOT NULL,
    channel_id              VARCHAR(50)  NOT NULL,
    project_id              VARCHAR(50)  NOT NULL,
    release_id              VARCHAR(50)  NOT NULL,
    environment_id          VARCHAR(50)  NOT NULL,
    json                    TEXT         NOT NULL,
    deployed_by             VARCHAR(200) NOT NULL,
    deployed_to_machine_ids TEXT         NOT NULL,
    created                 TIMESTAMPTZ  NOT NULL
);

CREATE TABLE IF NOT EXISTS "deployment_environment"
(
    id            VARCHAR(50)                                  PRIMARY KEY,
    name          VARCHAR(200)                                 NOT NULL,
    sort_order    INT                                          NOT NULL,
    json          TEXT                                         NOT NULL,
    data_version  BYTEA                                        NOT NULL,
    space_id      VARCHAR(50)                                  NOT NULL,
    slug          VARCHAR(210)                                 NOT NULL,
    type          VARCHAR(50)                                  NOT NULL,
    last_modified  TIMESTAMPTZ DEFAULT '0001-01-01 00:00:00+00' NOT NULL
);

CREATE TABLE IF NOT EXISTS "space"
(
    id                 VARCHAR(50)           PRIMARY KEY,
    name               VARCHAR(20)           NOT NULL,
    slug               VARCHAR(20)           NOT NULL,
    is_default         BOOLEAN               NOT NULL,
    json               TEXT                  NOT NULL,
    task_queue_stopped BOOLEAN               NOT NULL,
    data_version       BYTEA                 NOT NULL,
    last_modified       TIMESTAMPTZ           NOT NULL,
    is_private         BOOLEAN DEFAULT FALSE NOT NULL
);

CREATE TABLE IF NOT EXISTS "release"
(
    id                                     VARCHAR(150) PRIMARY KEY,
    version                                VARCHAR(349) NOT NULL,
    assembled                              TIMESTAMPTZ  NOT NULL,
    project_id                             VARCHAR(150) NOT NULL,
    project_variable_set_snapshot_id       VARCHAR(150) NOT NULL,
    project_deployment_process_snapshot_id VARCHAR(150) NOT NULL,
    json                                   TEXT         NOT NULL,
    channel_id                             VARCHAR(50)  NOT NULL,
    data_version                           BYTEA        NOT NULL,
    space_id                               VARCHAR(50)  NOT NULL,
    provisioning_runbook_process_id        VARCHAR(150),
    deprovisioning_runbook_process_id      VARCHAR(150),
    last_modified                           TIMESTAMPTZ  NOT NULL
);

CREATE TABLE IF NOT EXISTS "deployment_process"
(
    id        VARCHAR(150) PRIMARY KEY,
    owner_id  VARCHAR(150) NOT NULL,
    is_frozen BOOLEAN      NOT NULL,
    version   INT          NOT NULL,
    json      TEXT         NOT NULL,
    space_id  VARCHAR(50)  NOT NULL
);

CREATE TABLE IF NOT EXISTS "space"
(
    id                 VARCHAR(50)           PRIMARY KEY,
    name               VARCHAR(20)           NOT NULL,
    slug               VARCHAR(20)           NOT NULL,
    is_default         BOOLEAN               NOT NULL,
    json               TEXT                  NOT NULL,
    task_queue_stopped BOOLEAN               NOT NULL,
    data_version       BYTEA                 NOT NULL,
    last_modified       TIMESTAMPTZ           NOT NULL,
    is_private         BOOLEAN DEFAULT FALSE NOT NULL
);

CREATE TABLE IF NOT EXISTS "channel"
(
    id            VARCHAR(50)  PRIMARY KEY,
    name          VARCHAR(200) NOT NULL,
    project_id    VARCHAR(50)  NOT NULL,
    lifecycle_id  VARCHAR(50),
    json          TEXT         NOT NULL,
    data_version  BYTEA        NOT NULL,
    space_id      VARCHAR(50)  NOT NULL,
    slug          VARCHAR(210) NOT NULL,
    last_modified  TIMESTAMPTZ  NOT NULL
);

CREATE TABLE IF NOT EXISTS "project"
(
    id                                VARCHAR(50)                                  PRIMARY KEY,
    name                              VARCHAR(200)                                 NOT NULL,
    slug                              VARCHAR(210)                                 NOT NULL,
    is_disabled                       BOOLEAN                                      NOT NULL,
    variable_set_id                   VARCHAR(150)                                 NOT NULL,
    deployment_process_id             VARCHAR(50)                                  NOT NULL,
    project_group_id                  VARCHAR(50)                                  NOT NULL,
    lifecycle_id                      VARCHAR(50)                                  NOT NULL,
    auto_create_release               BOOLEAN                                      NOT NULL,
    json                              TEXT                                         NOT NULL,
    included_library_variable_set_ids TEXT                                         NOT NULL,
    discrete_channel_release          BOOLEAN                                      NOT NULL,
    data_version                      BYTEA                                        NOT NULL,
    cloned_from_project_id            VARCHAR(50),
    space_id                          VARCHAR(50)                                  NOT NULL,
    last_modified                      TIMESTAMPTZ DEFAULT '0001-01-01 00:00:00+00' NOT NULL,
    allow_ignore_channel_rules        BOOLEAN     DEFAULT TRUE                     NOT NULL
);

CREATE TABLE IF NOT EXISTS "library_variable_set"
(
    id              VARCHAR(50)   PRIMARY KEY,
    name            VARCHAR(200)  NOT NULL,
    variable_set_id VARCHAR(150),
    content_type    VARCHAR(50)   NOT NULL,
    json            TEXT          NOT NULL,
    space_id        VARCHAR(50)   NOT NULL,
    version         INT DEFAULT 0 NOT NULL
);

CREATE TABLE IF NOT EXISTS "variable_set"
(
    id                   VARCHAR(150) PRIMARY KEY,
    owner_type           VARCHAR(20)  NOT NULL,
    owner_id             VARCHAR(150) NOT NULL,
    version              INT          NOT NULL,
    is_frozen            BOOLEAN      NOT NULL,
    json                 TEXT         NOT NULL,
    related_document_ids TEXT         NOT NULL,
    space_id             VARCHAR(50)  NOT NULL
);

CREATE TABLE IF NOT EXISTS "related_document"
(
    id                     VARCHAR(250) PRIMARY KEY,
    table_name             VARCHAR(40)  NOT NULL,
    related_document_id    VARCHAR(250) NOT NULL,
    related_document_table VARCHAR(40)  NOT NULL
);

CREATE TABLE IF NOT EXISTS "lifecycle"
(
    id           VARCHAR(50)  PRIMARY KEY,
    name         VARCHAR(200) NOT NULL,
    json         TEXT         NOT NULL,
    data_version BYTEA        NOT NULL,
    space_id     VARCHAR(50)  NOT NULL,
    slug         VARCHAR(210) NOT NULL
);

CREATE TABLE IF NOT EXISTS "machine"
(
    id                     VARCHAR(50)                    PRIMARY KEY,
    name                   VARCHAR(200)                   NOT NULL,
    is_disabled            BOOLEAN                        NOT NULL,
    roles                  TEXT                           NOT NULL,
    environment_ids        TEXT                           NOT NULL,
    json                   TEXT                           NOT NULL,
    machine_policy_id      VARCHAR(50),
    thumbprint             VARCHAR(50),
    fingerprint            VARCHAR(50),
    deployment_target_type VARCHAR(50)                    NOT NULL,
    data_version           BYTEA                          NOT NULL,
    space_id               VARCHAR(50)                    NOT NULL,
    operating_system       VARCHAR(400) DEFAULT 'Unknown' NOT NULL,
    shell_name             VARCHAR(400) DEFAULT 'Unknown' NOT NULL,
    shell_version          VARCHAR(400) DEFAULT 'Unknown' NOT NULL,
    license_hash           TEXT,
    slug                   VARCHAR(210)                   NOT NULL
);

CREATE TABLE IF NOT EXISTS "machine_policy"
(
    id           VARCHAR(50)  PRIMARY KEY,
    name         VARCHAR(200) NOT NULL,
    is_default   BOOLEAN      NOT NULL,
    json         TEXT         NOT NULL,
    data_version BYTEA        NOT NULL,
    space_id     VARCHAR(50)  NOT NULL
);

CREATE TABLE IF NOT EXISTS "project_group"
(
    id            VARCHAR(50)  PRIMARY KEY,
    space_id      VARCHAR(50)  NOT NULL,
    name          VARCHAR(200) NOT NULL,
    description   TEXT,
    data_version  BYTEA        NOT NULL,
    last_modified  TIMESTAMPTZ  NOT NULL
);
