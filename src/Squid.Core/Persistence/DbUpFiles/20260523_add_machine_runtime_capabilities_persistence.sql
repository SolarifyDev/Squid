-- H2 — persist runtime capability cache to DB so server pod restarts don't
-- wipe the cache and force operators into the cold-cache UX trap fixed in H1.
--
-- Adds two columns to `machine`:
--   * runtime_capabilities_json — full MachineRuntimeCapabilities snapshot
--     as JSON (jsonb for efficient ->> queries from future filters).
--     NULL means "never health-checked" — cache stays empty until next probe.
--   * runtime_capabilities_updated_at — when the snapshot was taken. Used by
--     a future TTL-invalidation step (H2 itself doesn't gate on age; cache
--     remains valid until next active health-check overwrites or
--     IMachineRuntimeCapabilitiesPersistence.InvalidateAsync nulls the row).
--
-- Backward compatibility: pre-existing machines get NULL columns. The
-- MachineRuntimeCapabilitiesCacheHydrator IStartable skips NULL rows at
-- startup, so first-ever upgrade-info call returns NoOsDetected (H1) which
-- correctly points the operator at the health-check endpoint.

ALTER TABLE machine
    ADD COLUMN runtime_capabilities_json     jsonb       NULL,
    ADD COLUMN runtime_capabilities_updated_at timestamptz NULL;

-- Optional partial index for future "which machines were health-checked in
-- the last hour" admin queries. WHERE clause keeps it small for the common
-- case where most fleets have a small staleness window.
CREATE INDEX ix_machine_runtime_capabilities_updated_at
    ON machine USING btree (runtime_capabilities_updated_at DESC)
    WHERE runtime_capabilities_updated_at IS NOT NULL;
