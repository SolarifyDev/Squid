-- Encrypted-at-rest (IAtRestSecretProtector) Octopus export password, retained only while the
-- session is active so confirmation can decrypt sensitive variables / feed passwords from the
-- uploaded archive. Cleared on every terminal state, on expiry and when the upload is purged.
ALTER TABLE octopus_import_session
    ADD COLUMN IF NOT EXISTS protected_export_password text NULL;
