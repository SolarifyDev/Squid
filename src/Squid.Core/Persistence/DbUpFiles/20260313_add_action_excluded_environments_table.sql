CREATE TABLE IF NOT EXISTS action_excluded_environments (
    action_id INTEGER NOT NULL,
    environment_id INTEGER NOT NULL,

    PRIMARY KEY (action_id, environment_id)
);
