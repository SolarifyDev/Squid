-- Add health status columns to machine table
ALTER TABLE machine ADD COLUMN health_status VARCHAR(50) NOT NULL DEFAULT 'Unknown';
ALTER TABLE machine ADD COLUMN health_last_checked TIMESTAMP;
ALTER TABLE machine ADD COLUMN health_detail_json TEXT;

-- Seed default machine policy if not exists
INSERT INTO machine_policy (space_id, name, description, is_default, machine_health_check_policy, machine_connectivity_policy, machine_cleanup_policy, machine_update_policy)
SELECT 1, 'Default Machine Policy', 'The default machine policy', TRUE,
       '{"HealthCheckIntervalSeconds":3600,"ScriptPolicies":{}}',
       '{"MachineConnectivityBehavior":"ExpectedToBeOnline"}',
       '{"DeleteMachinesBehavior":"DoNotDelete","DeleteMachinesAfterSeconds":86400}',
       '{"CalamariUpdateBehavior":"UpdateOnDeployment","TentacleUpdateBehavior":"NeverUpdate"}'
WHERE NOT EXISTS (SELECT 1 FROM machine_policy WHERE is_default = TRUE);

-- Set default policy on machines that don't have one
UPDATE machine SET machine_policy_id = (SELECT id FROM machine_policy WHERE is_default = TRUE LIMIT 1)
WHERE machine_policy_id IS NULL;
