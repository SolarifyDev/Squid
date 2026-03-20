-- Remove duplicate releases, keeping the one with the latest id per (project, channel, version).
-- Cascades to child tables: release_selected_package, deployment.
WITH duplicates AS (
    SELECT id
    FROM "release"
    WHERE id NOT IN (
        SELECT MAX(id)
        FROM "release"
        GROUP BY project_id, channel_id, version
    )
)
DELETE FROM release_selected_package
WHERE release_id IN (SELECT id FROM duplicates);

WITH duplicates AS (
    SELECT id
    FROM "release"
    WHERE id NOT IN (
        SELECT MAX(id)
        FROM "release"
        GROUP BY project_id, channel_id, version
    )
)
DELETE FROM deployment
WHERE release_id IN (SELECT id FROM duplicates);

DELETE FROM "release"
WHERE id NOT IN (
    SELECT MAX(id)
    FROM "release"
    GROUP BY project_id, channel_id, version
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_release_project_channel_version
    ON "release" (project_id, channel_id, version);
