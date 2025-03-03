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

CREATE TABLE IF NOT EXISTS "deployment_completion"
(
    Id             VARCHAR(50) PRIMARY KEY,
    SequenceNumber BIGSERIAL,
    DeploymentId   VARCHAR(50) NOT NULL,
    State          VARCHAR(50) NOT NULL,
    CompletedTime  TIMESTAMPTZ NOT NULL,
    SpaceId        VARCHAR(50) NOT NULL
);

CREATE TABLE IF NOT EXISTS "server_task"
(
    Id                   VARCHAR(50)                                  NOT NULL PRIMARY KEY,
    Name                 VARCHAR(50)                                  NOT NULL,
    Description          TEXT                                         NOT NULL,
    QueueTime            TIMESTAMPTZ                                  NOT NULL,
    StartTime            TIMESTAMPTZ,
    CompletedTime        TIMESTAMPTZ,
    ErrorMessage         TEXT,
    ConcurrencyTag       VARCHAR(100),
    State                VARCHAR(50)                                  NOT NULL,
    HasWarningsOrErrors  BOOLEAN                                      NOT NULL,
    ServerNodeId         VARCHAR(250),
    ProjectId            VARCHAR(50),
    EnvironmentId        VARCHAR(50),
    DurationSeconds      INT                                          NOT NULL,
    JSON                 TEXT                                         NOT NULL,
    DataVersion          BYTEA                                        NOT NULL,
    SpaceId              VARCHAR(50),
    LastModified          TIMESTAMPTZ DEFAULT '0001-01-01 00:00:00+00' NOT NULL,
    BusinessProcessState VARCHAR(10) DEFAULT 'InProgress'             NOT NULL,
    ServerTaskType       VARCHAR(50) DEFAULT 'ServerTask'             NOT NULL,
    ParentServerTaskId   VARCHAR(50),
    PriorityTime         TIMESTAMPTZ,
    StateOrder           INT         DEFAULT 9                        NOT NULL,
    Weight               INT         DEFAULT 0                        NOT NULL,
    BatchId              VARCHAR(50)
);

CREATE TABLE IF NOT EXISTS "lifecycle"
(
    Id          varchar(50)  PRIMARY KEY,
    Name        varchar(200) NOT NULL,
    JSON        text         NOT NULL,
    DataVersion bytea        NOT NULL,
    SpaceId     varchar(50)  NOT NULL,
    Slug        varchar(210) NOT NULL
);