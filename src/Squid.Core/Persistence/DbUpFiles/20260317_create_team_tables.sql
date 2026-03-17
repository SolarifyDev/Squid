CREATE TABLE IF NOT EXISTS team (
    id SERIAL PRIMARY KEY,
    name VARCHAR(200) NOT NULL,
    description TEXT,
    space_id INT NOT NULL DEFAULT 1,
    created_date TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by INT NOT NULL DEFAULT 0,
    last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_modified_by INT NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS team_member (
    team_id INT NOT NULL REFERENCES team(id) ON DELETE CASCADE,
    user_id INT NOT NULL,
    CONSTRAINT pk_team_member PRIMARY KEY (team_id, user_id)
);
CREATE INDEX IF NOT EXISTS ix_team_member_user_id ON team_member (user_id);

ALTER TABLE deployment_interruption
    ADD COLUMN IF NOT EXISTS responsible_team_ids TEXT;
