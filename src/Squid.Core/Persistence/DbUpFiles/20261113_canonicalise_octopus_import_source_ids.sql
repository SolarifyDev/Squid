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

-- 2. Rows of one canonical id from exports with DIFFERENT suffixes come from different Octopus servers imported into one
--    space (ids like Projects-1 collide across servers): none of them may answer for the canonical id, so all are dropped.
--    The Squid rows stay; a later import matches them as a first import would and never overwrites them.
DELETE FROM octopus_import_source_map m
USING (
    SELECT space_id, source_type, upper(regexp_replace(source_id, '-[0-9A-F]{32}(?=$|/)', '', 'gi')) AS canonical_id
    FROM octopus_import_source_map
    WHERE source_type NOT IN ('OctopusInstallation', 'OctopusExportSource')
    GROUP BY 1, 2, 3
    HAVING count(DISTINCT upper(substring(source_id FROM '-([0-9A-Fa-f]{32})(?:$|/)'))) > 1
) AS ambiguous
WHERE m.space_id = ambiguous.space_id
  AND m.source_type = ambiguous.source_type
  AND upper(regexp_replace(m.source_id, '-[0-9A-F]{32}(?=$|/)', '', 'gi')) = ambiguous.canonical_id;

-- 3. Where a suffixed row and its canonical twin (one export source) both exist, the newest row wins.
DELETE FROM octopus_import_source_map m
USING (
    SELECT id, row_number() OVER (PARTITION BY space_id, source_type, canonical_id ORDER BY last_modified_date DESC, id DESC) AS rank
    FROM (
        SELECT id, space_id, source_type, last_modified_date, upper(regexp_replace(source_id, '-[0-9A-F]{32}(?=$|/)', '', 'gi')) AS canonical_id
        FROM octopus_import_source_map
    ) AS canonical
) AS ranked
WHERE m.id = ranked.id AND ranked.rank > 1;

-- 4. Rewrite the remaining suffixed ids to their canonical form.
UPDATE octopus_import_source_map
SET source_id = upper(regexp_replace(source_id, '-[0-9A-F]{32}(?=$|/)', '', 'gi'))
WHERE source_id ~* '-[0-9A-F]{32}(?=$|/)';
