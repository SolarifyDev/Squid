CREATE TABLE IF NOT EXISTS project_group (
    id SERIAL PRIMARY KEY,
    name VARCHAR(200) NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    space_id INTEGER NOT NULL DEFAULT 1,
    slug VARCHAR(210) NOT NULL,
    data_version BYTEA NOT NULL DEFAULT ''
);
