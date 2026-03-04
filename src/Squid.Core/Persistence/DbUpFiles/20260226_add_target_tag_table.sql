CREATE TABLE IF NOT EXISTS target_tag (
    id       SERIAL       PRIMARY KEY,
    name     VARCHAR(200) NOT NULL UNIQUE,
    space_id INTEGER      NOT NULL DEFAULT 1
);
