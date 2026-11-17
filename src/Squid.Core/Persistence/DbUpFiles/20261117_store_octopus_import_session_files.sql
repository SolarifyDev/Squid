-- Octopus import session files (export upload, history bundle, staged REST documents, task details) kept in the database, so any
-- Squid replica can serve any request of a session and a restart loses nothing; a server's disk only caches the large files.
-- Rows live only while the session's temporary upload does: the cleanup deletes them together with the session's file key in one
-- transaction. Content is chunked (OctopusImportSessionFileCodec.ChunkBytes) and, when the master key is available, encrypted with a
-- per-session key (protected_file_key, wrapped by the variable-encryption master key). Additive and re-runnable.
ALTER TABLE octopus_import_session
    ADD COLUMN IF NOT EXISTS protected_file_key text NULL;

CREATE TABLE IF NOT EXISTS octopus_import_session_file
(
    session_id uuid NOT NULL REFERENCES octopus_import_session (session_id) ON DELETE CASCADE,
    path varchar(512) COLLATE "C" NOT NULL,
    length bigint NOT NULL,
    sha256 char(64) NOT NULL,
    chunk_count integer NOT NULL,
    encoding varchar(16) NOT NULL,
    encryption varchar(16) NOT NULL,
    created_date timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (session_id, path)
);

CREATE TABLE IF NOT EXISTS octopus_import_session_file_chunk
(
    session_id uuid NOT NULL,
    path varchar(512) COLLATE "C" NOT NULL,
    chunk_index integer NOT NULL,
    content bytea NOT NULL,
    PRIMARY KEY (session_id, path, chunk_index),
    FOREIGN KEY (session_id, path) REFERENCES octopus_import_session_file (session_id, path) ON DELETE CASCADE
);

-- Chunks are already compressed or encrypted: TOAST compression would only spend CPU.
ALTER TABLE octopus_import_session_file_chunk ALTER COLUMN content SET STORAGE EXTERNAL;
