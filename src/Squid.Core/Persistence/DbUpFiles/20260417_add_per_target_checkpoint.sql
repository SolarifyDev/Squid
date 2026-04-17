-- Per-target checkpoint: the existing LastCompletedBatchIndex only tracks whole-
-- batch completion. If the server crashes mid-batch (e.g. 3 of 5 targets done),
-- the batch is re-executed in full on resume, re-running the 3 already-completed
-- targets against their machines. This migration adds target-level granularity
-- so resume only re-runs incomplete targets.
--
-- Column names end in _json to match the existing EF Core snake_case convention
-- for the sibling output_variables_json column (PascalCase property + Json
-- suffix → snake_case column + _json suffix).
--
-- Columns are JSONB so the shape of per-target state can evolve without further
-- migrations:
--   batch_states_json        — { "<batchIndex>": { "completedMachineIds": [int], "failedMachineIds": [int] } }
--   in_flight_scripts_json   — { "<machineId>": "<scriptTicket>" }
--
-- The DO block handles the (dev-only) case where an earlier version of this
-- migration was applied with the wrong names — rename in place so no data
-- is lost. In CI and production the ADD COLUMN paths run unconditionally.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'deployment_execution_checkpoint' AND column_name = 'batch_states')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name = 'deployment_execution_checkpoint' AND column_name = 'batch_states_json')
    THEN
        ALTER TABLE deployment_execution_checkpoint RENAME COLUMN batch_states TO batch_states_json;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'deployment_execution_checkpoint' AND column_name = 'in_flight_scripts')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name = 'deployment_execution_checkpoint' AND column_name = 'in_flight_scripts_json')
    THEN
        ALTER TABLE deployment_execution_checkpoint RENAME COLUMN in_flight_scripts TO in_flight_scripts_json;
    END IF;
END $$;

ALTER TABLE deployment_execution_checkpoint
    ADD COLUMN IF NOT EXISTS batch_states_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN IF NOT EXISTS in_flight_scripts_json jsonb NOT NULL DEFAULT '{}'::jsonb;
