CREATE TABLE IF NOT EXISTS user_role (
    id SERIAL PRIMARY KEY,
    name VARCHAR(200) NOT NULL,
    description TEXT,
    is_built_in BOOLEAN NOT NULL DEFAULT FALSE,
    created_date TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by INT NOT NULL DEFAULT 0,
    last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_modified_by INT NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_user_role_name ON user_role (name);

CREATE TABLE IF NOT EXISTS user_role_permission (
    user_role_id INT NOT NULL REFERENCES user_role(id) ON DELETE CASCADE,
    permission VARCHAR(100) NOT NULL,
    CONSTRAINT pk_user_role_permission PRIMARY KEY (user_role_id, permission)
);

CREATE TABLE IF NOT EXISTS scoped_user_role (
    id SERIAL PRIMARY KEY,
    team_id INT NOT NULL REFERENCES team(id) ON DELETE CASCADE,
    user_role_id INT NOT NULL REFERENCES user_role(id) ON DELETE CASCADE,
    space_id INT,
    created_date TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by INT NOT NULL DEFAULT 0,
    last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_modified_by INT NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_scoped_user_role_team_id ON scoped_user_role (team_id);
CREATE INDEX IF NOT EXISTS ix_scoped_user_role_space_id ON scoped_user_role (space_id);

CREATE TABLE IF NOT EXISTS scoped_user_role_project (
    scoped_user_role_id INT NOT NULL REFERENCES scoped_user_role(id) ON DELETE CASCADE,
    project_id INT NOT NULL,
    CONSTRAINT pk_scoped_user_role_project PRIMARY KEY (scoped_user_role_id, project_id)
);

CREATE TABLE IF NOT EXISTS scoped_user_role_environment (
    scoped_user_role_id INT NOT NULL REFERENCES scoped_user_role(id) ON DELETE CASCADE,
    environment_id INT NOT NULL,
    CONSTRAINT pk_scoped_user_role_environment PRIMARY KEY (scoped_user_role_id, environment_id)
);

CREATE TABLE IF NOT EXISTS scoped_user_role_project_group (
    scoped_user_role_id INT NOT NULL REFERENCES scoped_user_role(id) ON DELETE CASCADE,
    project_group_id INT NOT NULL,
    CONSTRAINT pk_scoped_user_role_project_group PRIMARY KEY (scoped_user_role_id, project_group_id)
);
