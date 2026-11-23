-- A small value each replica seals with its master key (the queue's key envelope around a constant). The other replicas try to open it: one that cannot is running
-- with a different master key, and every request that crosses between the two through Redis would be dropped as unreadable, with nothing said until now.
-- Additive and re-runnable; null for a replica that has no master key or has not computed it yet.
ALTER TABLE server_replica ADD COLUMN IF NOT EXISTS key_check text;
