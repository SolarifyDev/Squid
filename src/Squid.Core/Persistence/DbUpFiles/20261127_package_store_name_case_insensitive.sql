-- Store names are unique ignoring case ("Local disk" and "local DISK" would be indistinguishable in a list). The exact-match index it replaces is implied.
-- A database that already holds two names differing only by case keeps the oldest as it is and renames the later ones by appending their id, so the
-- index can always be created. Re-runnable.
UPDATE package_store s
SET name = s.name || ' (' || s.id || ')'
WHERE EXISTS (SELECT 1 FROM package_store o WHERE lower(o.name) = lower(s.name) AND o.id < s.id);

CREATE UNIQUE INDEX IF NOT EXISTS ux_package_store_name_lower ON package_store (lower(name));

DROP INDEX IF EXISTS ux_package_store_name;
