-- Machine policy cleanup enforcement: track when a deployment target FIRST went
-- unavailable, so the policy's "delete unavailable targets after N" grace period
-- can be measured against a stable instant rather than the last-checked time
-- (which a recurring health check keeps refreshing).
--
-- Set when HealthStatus transitions into Unavailable, cleared when it next reports
-- Healthy, preserved while it stays Unavailable. NULL = not currently
-- known-unavailable. Pre-existing machines get NULL and are never eligible for
-- auto-cleanup until their next unavailable transition records a go-bad instant.

ALTER TABLE machine
    ADD COLUMN unavailable_since timestamptz NULL;
