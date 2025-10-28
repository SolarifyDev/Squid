CREATE TABLE variable_set
(
    id                   SERIAL PRIMARY KEY,
    owner_type           INTEGER NOT NULL,
    owner_id             INTEGER NOT NULL,
    version              INTEGER NOT NULL DEFAULT 1,
    is_frozen            BOOLEAN NOT NULL DEFAULT FALSE,
    related_document_ids TEXT,
    space_id             INTEGER NOT NULL,
    content_hash         VARCHAR(64),
    last_modified        TIMESTAMPTZ      DEFAULT NOW()
);

CREATE TABLE variable
(
    id               SERIAL PRIMARY KEY,
    variable_set_id  INTEGER      NOT NULL,
    name             VARCHAR(200) NOT NULL,
    value            TEXT,
    description      TEXT,
    type             INTEGER      NOT NULL DEFAULT 1,
    is_sensitive     BOOLEAN      NOT NULL DEFAULT FALSE,
    sort_order       INTEGER      NOT NULL DEFAULT 0,
    last_modified_on TIMESTAMPTZ           DEFAULT NOW(),
    last_modified_by VARCHAR(255)
);

CREATE TABLE variable_scope
(
    id          SERIAL PRIMARY KEY,
    variable_id INTEGER      NOT NULL,
    scope_type  INTEGER      NOT NULL,
    scope_value VARCHAR(100) NOT NULL
);

CREATE TABLE variable_set_snapshot
(
    id                       SERIAL PRIMARY KEY,
    original_variable_set_id INTEGER      NOT NULL,
    version                  INTEGER      NOT NULL,
    snapshot_data            BYTEA        NOT NULL,
    content_hash             VARCHAR(64)  NOT NULL,
    compression_type         VARCHAR(20)  NOT NULL DEFAULT 'GZIP',
    uncompressed_size        INTEGER      NOT NULL,
    created_at               TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by               VARCHAR(100) NOT NULL
);


CREATE INDEX ix_variable_variable_set_name ON variable (variable_set_id, name);
CREATE INDEX ix_variable_sort_order ON variable (variable_set_id, sort_order, name);
CREATE INDEX ix_variable_scope_variable ON variable_scope (variable_id);
CREATE INDEX ix_variable_scope_type_value ON variable_scope (scope_type, scope_value);

CREATE INDEX ix_snapshot_original_hash ON variable_set_snapshot (original_variable_set_id, content_hash);
CREATE INDEX ix_snapshot_created ON variable_set_snapshot (created_at DESC);
CREATE INDEX ix_snapshot_content_hash ON variable_set_snapshot (content_hash);


CREATE INDEX ix_variable_set_owner ON variable_set (owner_type, owner_id);
CREATE INDEX ix_variable_set_space ON variable_set (space_id);
CREATE INDEX ix_variable_set_content_hash ON variable_set (content_hash);

-- ProcessSnapshot table
CREATE TABLE process_snapshot
(
    id                       SERIAL PRIMARY KEY,
    original_process_id      INTEGER      NOT NULL,
    version                  INTEGER      NOT NULL,
    snapshot_data            BYTEA        NOT NULL,
    content_hash             VARCHAR(64)  NOT NULL,
    compression_type         VARCHAR(20)  NOT NULL DEFAULT 'GZIP',
    uncompressed_size        INTEGER      NOT NULL,
    created_at               TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by               VARCHAR(100) NOT NULL
);

CREATE INDEX ix_process_snapshot_original_hash ON process_snapshot (original_process_id, content_hash);
CREATE INDEX ix_process_snapshot_created ON process_snapshot (created_at DESC);
CREATE INDEX ix_process_snapshot_content_hash ON process_snapshot (content_hash);
