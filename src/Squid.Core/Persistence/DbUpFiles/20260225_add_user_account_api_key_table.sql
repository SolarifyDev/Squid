CREATE TABLE IF NOT EXISTS user_account_api_key
(
    id             SERIAL PRIMARY KEY,
    user_account_id INT                         NOT NULL,
    api_key        VARCHAR(128)                 NOT NULL,
    description    VARCHAR(256),
    is_disabled    BOOLEAN      DEFAULT FALSE   NOT NULL,
    created_at_utc TIMESTAMPTZ                  NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_user_account_api_key_api_key
    ON user_account_api_key (api_key);

CREATE INDEX IF NOT EXISTS ix_user_account_api_key_user_account_id
    ON user_account_api_key (user_account_id);
