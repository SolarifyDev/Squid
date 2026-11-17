-- Owner-qualified child source ids. Octopus copies step, action and variable ids verbatim when it clones a project, so a raw child
-- id cannot name one child: a project and its clone collide, and a clone imported later would link to the original's rows. The
-- application now writes "<OWNER>/<TOKEN>/<ID>" (OctopusImportChildSourceIds, tokens pinned to this file by a unit test); this
-- script brings the rows written before into that form. The owner is the one the Squid row really belongs to: the source-map row of
-- the project / library variable set / lifecycle that holds it (same session first, then the newest — the loader's newest-wins
-- rule). Re-runnable: only raw ids (no '/') and action fallbacks under a raw step id match. A row whose destination or owner is gone
-- stays as it is (it can never match a qualified lookup). last_modified_date is left alone, so the loader's order is unchanged.

-- 1. DeploymentStep: deployment_step -> project (deployment_process_id) -> Project row.
WITH owner AS (
    SELECT m.id AS row_id, o.source_id AS owner_id,
           row_number() OVER (PARTITION BY m.id ORDER BY (o.last_session_id IS NOT DISTINCT FROM m.last_session_id) DESC, o.last_modified_date DESC, o.id DESC) AS rank
    FROM octopus_import_source_map m
    JOIN deployment_step s ON s.id = m.destination_id
    JOIN project p ON p.deployment_process_id = s.process_id
    JOIN octopus_import_source_map o ON o.space_id = m.space_id AND o.source_type = 'Project' AND o.destination_id = p.id
    WHERE m.source_type = 'DeploymentStep' AND position('/' IN m.source_id) = 0)
UPDATE octopus_import_source_map m
SET source_id = owner.owner_id || '/STEP/' || m.source_id
FROM owner
WHERE m.id = owner.row_id AND owner.rank = 1
  AND length(owner.owner_id || '/STEP/' || m.source_id) <= 200
  AND NOT EXISTS (SELECT 1 FROM octopus_import_source_map x WHERE x.space_id = m.space_id AND x.source_type = m.source_type AND x.source_id = owner.owner_id || '/STEP/' || m.source_id);

-- 2. DeploymentAction: deployment_action -> deployment_step -> project -> Project row.
--    raw "<ID>"                          -> OWNER/ACTION/<ID>
--    fallback "<STEPID>/ACTION-<N>"      -> OWNER/STEP/<STEPID>/ACTION-<N>   (an action Octopus gave no id, under a raw step id)
WITH owner AS (
    SELECT m.id AS row_id,
           CASE WHEN position('/' IN m.source_id) = 0 THEN o.source_id || '/ACTION/' || m.source_id ELSE o.source_id || '/STEP/' || m.source_id END AS qualified,
           row_number() OVER (PARTITION BY m.id ORDER BY (o.last_session_id IS NOT DISTINCT FROM m.last_session_id) DESC, o.last_modified_date DESC, o.id DESC) AS rank
    FROM octopus_import_source_map m
    JOIN deployment_action a ON a.id = m.destination_id
    JOIN deployment_step s ON s.id = a.step_id
    JOIN project p ON p.deployment_process_id = s.process_id
    JOIN octopus_import_source_map o ON o.space_id = m.space_id AND o.source_type = 'Project' AND o.destination_id = p.id
    WHERE m.source_type = 'DeploymentAction' AND (position('/' IN m.source_id) = 0 OR m.source_id ~ '^[^/]+/ACTION-[0-9]+$'))
UPDATE octopus_import_source_map m
SET source_id = owner.qualified
FROM owner
WHERE m.id = owner.row_id AND owner.rank = 1
  AND length(owner.qualified) <= 200
  AND NOT EXISTS (SELECT 1 FROM octopus_import_source_map x WHERE x.space_id = m.space_id AND x.source_type = m.source_type AND x.source_id = owner.qualified);

-- 3. Variable: variable -> variable_set -> project.variable_set_id (Project row) | library_variable_set.variable_set_id (LibraryVariableSet row).
WITH owner AS (
    SELECT m.id AS row_id, o.source_id AS owner_id,
           row_number() OVER (PARTITION BY m.id ORDER BY (o.last_session_id IS NOT DISTINCT FROM m.last_session_id) DESC, o.last_modified_date DESC, o.id DESC) AS rank
    FROM octopus_import_source_map m
    JOIN variable v ON v.id = m.destination_id
    JOIN (SELECT id, variable_set_id, 'Project' AS owner_type FROM project
          UNION ALL
          SELECT id, variable_set_id, 'LibraryVariableSet' AS owner_type FROM library_variable_set) AS holder ON holder.variable_set_id = v.variable_set_id
    JOIN octopus_import_source_map o ON o.space_id = m.space_id AND o.source_type = holder.owner_type AND o.destination_id = holder.id
    WHERE m.source_type = 'Variable' AND position('/' IN m.source_id) = 0)
UPDATE octopus_import_source_map m
SET source_id = owner.owner_id || '/VARIABLE/' || m.source_id
FROM owner
WHERE m.id = owner.row_id AND owner.rank = 1
  AND length(owner.owner_id || '/VARIABLE/' || m.source_id) <= 200
  AND NOT EXISTS (SELECT 1 FROM octopus_import_source_map x WHERE x.space_id = m.space_id AND x.source_type = m.source_type AND x.source_id = owner.owner_id || '/VARIABLE/' || m.source_id);

-- 4. LifecyclePhase: phase -> lifecycle -> Lifecycle row.
WITH owner AS (
    SELECT m.id AS row_id, o.source_id AS owner_id,
           row_number() OVER (PARTITION BY m.id ORDER BY (o.last_session_id IS NOT DISTINCT FROM m.last_session_id) DESC, o.last_modified_date DESC, o.id DESC) AS rank
    FROM octopus_import_source_map m
    JOIN phase ph ON ph.id = m.destination_id
    JOIN octopus_import_source_map o ON o.space_id = m.space_id AND o.source_type = 'Lifecycle' AND o.destination_id = ph.lifecycle_id
    WHERE m.source_type = 'LifecyclePhase' AND position('/' IN m.source_id) = 0)
UPDATE octopus_import_source_map m
SET source_id = owner.owner_id || '/PHASE/' || m.source_id
FROM owner
WHERE m.id = owner.row_id AND owner.rank = 1
  AND length(owner.owner_id || '/PHASE/' || m.source_id) <= 200
  AND NOT EXISTS (SELECT 1 FROM octopus_import_source_map x WHERE x.space_id = m.space_id AND x.source_type = m.source_type AND x.source_id = owner.owner_id || '/PHASE/' || m.source_id);
