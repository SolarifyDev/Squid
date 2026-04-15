-- Rename Linux* CommunicationStyle values to Tentacle* (platform-agnostic naming).
-- Aligns with Octopus pattern where TentacleActive/TentaclePassive describe connection direction, not platform.
-- Windows/Linux Tentacles share the same server-side transport; only Agent-side IShell differs.

UPDATE machine
SET endpoint = jsonb_set(
    endpoint,
    '{CommunicationStyle}',
    '"TentaclePolling"'::jsonb
)
WHERE endpoint ->> 'CommunicationStyle' = 'LinuxPolling';

UPDATE machine
SET endpoint = jsonb_set(
    endpoint,
    '{CommunicationStyle}',
    '"TentacleListening"'::jsonb
)
WHERE endpoint ->> 'CommunicationStyle' = 'LinuxListening';
