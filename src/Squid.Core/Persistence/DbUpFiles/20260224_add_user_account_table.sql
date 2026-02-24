CREATE TABLE IF NOT EXISTS user_account
(
    id                            SERIAL PRIMARY KEY,
    user_name                     VARCHAR(200)               NOT NULL,
    normalized_user_name          VARCHAR(200)               NOT NULL,
    display_name                  VARCHAR(200)               NOT NULL,
    password_hash                 TEXT                       NOT NULL,
    is_disabled                   BOOLEAN      DEFAULT FALSE NOT NULL,
    is_system                     BOOLEAN      DEFAULT FALSE NOT NULL,
    created_at_utc                TIMESTAMPTZ                NOT NULL,
    updated_at_utc                TIMESTAMPTZ                NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_user_account_normalized_user_name
    ON user_account (normalized_user_name);
