-- A fingerprint of what a built-in package contains (sorted file paths and per-file content hashes), so pushing the same files packed another way (another zip tool,
-- a tar.gz instead of a zip) is recognised as the same content. sha256 stays what names the stored file and guards a deployment against an altered file.
-- Null for versions pushed before this existed; they are never treated as duplicates. Additive and re-runnable.
ALTER TABLE builtin_package ADD COLUMN IF NOT EXISTS content_fingerprint character varying(64) NULL;
