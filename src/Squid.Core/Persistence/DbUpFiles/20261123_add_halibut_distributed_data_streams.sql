-- Data streams of Halibut requests queued for another pod (package files, variables.json, encrypted sensitiveVariables.json), kept in the
-- shared database so the pod that holds an agent's polling connection can read what the pod that took the request stored.
-- A stream is deleted when its reader is done; one nobody collected is removed by the orphan sweep once created_date is older than the TTL.
-- Content is chunked and AES-256-GCM sealed with a per-store data key that is wrapped by the variable-encryption master key (protected_key).
-- Additive and re-runnable.
CREATE TABLE IF NOT EXISTS halibut_data_stream
(
    stream_id uuid NOT NULL PRIMARY KEY,
    length bigint NOT NULL,
    chunk_count integer NOT NULL,
    protected_key text NOT NULL,
    created_date timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_halibut_data_stream_created_date ON halibut_data_stream (created_date);

CREATE TABLE IF NOT EXISTS halibut_data_stream_chunk
(
    stream_id uuid NOT NULL REFERENCES halibut_data_stream (stream_id) ON DELETE CASCADE,
    chunk_index integer NOT NULL,
    content bytea NOT NULL,
    PRIMARY KEY (stream_id, chunk_index)
);

-- Chunks are already encrypted: TOAST compression would only spend CPU.
ALTER TABLE halibut_data_stream_chunk ALTER COLUMN content SET STORAGE EXTERNAL;
