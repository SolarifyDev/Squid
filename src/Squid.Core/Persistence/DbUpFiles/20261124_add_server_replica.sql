-- One row per running Squid server replica, refreshed by that replica's heartbeat. The Halibut request queue reads it to learn whether another replica is alive:
-- only then does a request for a polling agent ever need to cross replicas through Redis. A replica that stops refreshing is simply no longer counted once
-- last_seen is older than the live window; its row is removed when it shuts down cleanly, and rows older than a day are pruned by the next heartbeat.
-- Additive and re-runnable.
CREATE TABLE IF NOT EXISTS server_replica
(
    replica_id uuid NOT NULL PRIMARY KEY,
    last_seen timestamptz NOT NULL DEFAULT now()
);
