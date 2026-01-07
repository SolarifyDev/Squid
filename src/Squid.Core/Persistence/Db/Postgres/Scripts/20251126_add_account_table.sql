CREATE TABLE deployment_account
(
    id             SERIAL PRIMARY KEY,
    space_id       INT          NOT NULL,
    name           VARCHAR(255) NOT NULL,
    description    TEXT,
    slug           VARCHAR(255) NOT NULL,
    environment_id TEXT,
    account_type   INT          NOT NULL,
    token          TEXT
);
