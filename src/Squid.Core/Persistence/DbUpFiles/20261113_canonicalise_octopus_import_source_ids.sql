-- Canonical Octopus source ids. An Octopus export appends one constant 32-hex suffix to every id
-- ("Projects-643-<sfx>", also inside child ids "deploymentprocess-Projects-1-<sfx>/step-0"); the REST API
-- returns the bare id. Rows backfilled from earlier file imports carry the suffix, so file and API imports
-- of one resource would never meet. The application strips the suffix on every write and lookup
-- (OctopusImportSourceIds.ExportSuffixPattern, pinned to this file by a unit test); this script brings the
-- stored rows in line once. Re-runnable: every step is a no-op on canonical rows.

-- 1. Keep each stripped suffix as the space's export-source fingerprint (evidence of which export it came from).
INSERT INTO octopus_import_source_map (space_id, source_type, source_id, destination_id, outcome_state)
SELECT DISTINCT space_id, 'OctopusExportSource', upper(substring(source_id FROM '-([0-9A-Fa-f]{32})(?:$|/)')), space_id, 'Reused'
FROM octopus_import_source_map
WHERE source_type NOT IN ('OctopusInstallation', 'OctopusExportSource')
  AND source_id ~* '-[0-9A-F]{32}(?=$|/)'
ON CONFLICT (space_id, source_type, source_id) DO NOTHING;

-- 2. Where a suffixed row and its canonical twin both exist, the newest row wins.
DELETE FROM octopus_import_source_map m
USING (
    SELECT id, row_number() OVER (PARTITION BY space_id, source_type, canonical_id ORDER BY last_modified_date DESC, id DESC) AS rank
    FROM (
        SELECT id, space_id, source_type, last_modified_date, upper(regexp_replace(source_id, '-[0-9A-F]{32}(?=$|/)', '', 'gi')) AS canonical_id
        FROM octopus_import_source_map
    ) AS canonical
) AS ranked
WHERE m.id = ranked.id AND ranked.rank > 1;

-- 3. Rewrite the remaining suffixed ids to their canonical form.
UPDATE octopus_import_source_map
SET source_id = upper(regexp_replace(source_id, '-[0-9A-F]{32}(?=$|/)', '', 'gi'))
WHERE source_id ~* '-[0-9A-F]{32}(?=$|/)';
