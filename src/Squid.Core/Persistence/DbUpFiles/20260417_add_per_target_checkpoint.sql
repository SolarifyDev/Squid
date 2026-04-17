-- Per-target checkpoint: the existing LastCompletedBatchIndex only tracks whole-
-- batch completion. If the server crashes mid-batch (e.g. 3 of 5 targets done),
-- the batch is re-executed in full on resume, re-running the 3 already-completed
-- targets against their machines. This migration adds target-level granularity
-- so resume only re-runs incomplete targets.
--
-- Columns are JSONB so the shape of per-target state can evolve without further
-- migrations:
--   batch_states         — { "<batchIndex>": { "completedMachineIds": [int], "failedMachineIds": [int] } }
--   in_flight_scripts    — { "<machineId>": "<scriptTicket>" }
ALTER TABLE deployment_execution_checkpoint
    ADD COLUMN IF NOT EXISTS batch_states jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN IF NOT EXISTS in_flight_scripts jsonb NOT NULL DEFAULT '{}'::jsonb;
