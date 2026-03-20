CREATE TABLE IF NOT EXISTS channel_version_rules (
    id SERIAL PRIMARY KEY,
    channel_id INTEGER NOT NULL,
    action_names TEXT NOT NULL DEFAULT '',
    version_range TEXT NOT NULL DEFAULT '',
    pre_release_tag TEXT NOT NULL DEFAULT '',
    sort_order INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_channel_version_rules_channel_id ON channel_version_rules (channel_id);
