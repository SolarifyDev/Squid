-- Align in_flight_scripts_json to the JSON ARRAY shape it actually carries.
--
-- The column was created (20260417_add_per_target_checkpoint.sql) with DEFAULT
-- '{}'::jsonb and documented as the object form { "<machineId>": "<scriptTicket>" }.
-- That object shape was abandoned: InFlightScriptMap serializes a List<Entry> —
-- an ARRAY keyed by dispatch slot:
--   [{ "m": machineId, "s": stepId, "a": actionId, "t": scriptTicket }]
-- and its Parse() throws (caught → treated as empty) on an object, so '{}' only
-- ever worked via that catch fallback.
--
-- This migration:
--   1. Re-points the column DEFAULT to the empty array '[]'::jsonb so the default
--      matches the EF model (HasDefaultValue("[]")) and the EnsureExistsAsync seed.
--   2. Rewrites EXISTING empty '{}' rows to '[]'. An empty object means "no
--      in-flight scripts" — identical in meaning to '[]' — so this is a pure
--      shape normalization with no behavioural change. Non-empty legacy object
--      rows (if any) are left untouched and remain handled by InFlightScriptMap's
--      Parse() catch (re-dispatch fresh), exactly as before.
--
-- Non-breaking: InFlightScriptMap tolerates any leftover legacy shape, and EF
-- always supplies an explicit value on insert, so the DB default is belt-and-braces.

ALTER TABLE deployment_execution_checkpoint
    ALTER COLUMN in_flight_scripts_json SET DEFAULT '[]'::jsonb;

UPDATE deployment_execution_checkpoint
    SET in_flight_scripts_json = '[]'::jsonb
    WHERE in_flight_scripts_json = '{}'::jsonb;
