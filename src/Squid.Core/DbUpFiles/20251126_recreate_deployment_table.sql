DROP TABLE IF EXISTS deployment;

CREATE TABLE deployment
(
    id                       SERIAL PRIMARY KEY,
    name                     VARCHAR(200) NOT NULL,
    task_id                  INT,
    space_id                 INT          NOT NULL,
    channel_id               INT          NOT NULL,
    project_id               INT          NOT NULL,
    release_id               INT          NOT NULL,
    environment_id           INT          NOT NULL,
    machine_id               INT          NOT NULL,
    json                     TEXT         NOT NULL,
    deployed_by              INT          NOT NULL,
    deployed_to_machine_ids  TEXT         NOT NULL,
    process_snapshot_id      INT,
    variable_set_snapshot_id INT,
    created                  TIMESTAMPTZ  NOT NULL
);

