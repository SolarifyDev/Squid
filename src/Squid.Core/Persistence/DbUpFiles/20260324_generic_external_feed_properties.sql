ALTER TABLE external_feed ADD COLUMN IF NOT EXISTS properties TEXT;

UPDATE external_feed
SET properties = json_build_object(
    'ApiVersion', COALESCE(api_version, ''),
    'RegistryPath', COALESCE(registry_path, '')
)::text
WHERE (api_version IS NOT NULL AND api_version != '')
   OR (registry_path IS NOT NULL AND registry_path != '');

ALTER TABLE external_feed DROP COLUMN IF EXISTS api_version;
ALTER TABLE external_feed DROP COLUMN IF EXISTS registry_path;
