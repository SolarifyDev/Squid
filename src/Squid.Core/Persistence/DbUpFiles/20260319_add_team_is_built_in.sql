ALTER TABLE team ADD COLUMN IF NOT EXISTS is_built_in BOOLEAN NOT NULL DEFAULT FALSE;

UPDATE team SET is_built_in = TRUE
WHERE name IN ('Squid Administrators', 'Everyone') AND space_id = 0;
