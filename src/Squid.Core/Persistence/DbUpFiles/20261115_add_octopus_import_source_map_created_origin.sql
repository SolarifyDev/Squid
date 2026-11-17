-- The origin of the import that created each source-map row: its Octopus installation id and / or export fingerprint (export id
-- suffix). Ids like Projects-1 repeat across Octopus servers, so an import may link without proof only to rows its own origin
-- created; rows of the space's recorded origin need that origin proven, and rows of a third origin are never linked. Both NULL:
-- unknown (treated as the space's recorded origin). Additive; non-breaking.
ALTER TABLE octopus_import_source_map
    ADD COLUMN IF NOT EXISTS created_installation_id varchar(200) NULL;

ALTER TABLE octopus_import_source_map
    ADD COLUMN IF NOT EXISTS created_export_source_id varchar(200) NULL;

-- Rows backfilled from earlier export-file imports: the export suffix the session's own mapping carried (the canonicalising
-- migration stripped it from source_id; one canonical id never keeps rows of two suffixes). Re-runnable: only rows with no
-- recorded origin are touched.
UPDATE octopus_import_source_map m
SET created_export_source_id = upper(substring(e->>'sourceId' FROM '-([0-9A-Fa-f]{32})(?:$|/)'))
FROM octopus_import_session s
CROSS JOIN LATERAL jsonb_array_elements(
    CASE WHEN jsonb_typeof(s.result_json->'idMappings') = 'array' THEN s.result_json->'idMappings' ELSE '[]'::jsonb END) AS e
WHERE s.session_id = m.last_session_id
  AND m.created_installation_id IS NULL
  AND m.created_export_source_id IS NULL
  AND e->>'sourceType' = m.source_type
  AND e->>'sourceId' ~* '-[0-9A-F]{32}(?=$|/)'
  AND upper(regexp_replace(trim(e->>'sourceId'), '-[0-9A-F]{32}(?=$|/)', '', 'gi')) = m.source_id;
