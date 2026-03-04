-- Inline retention policy into lifecycle (non-nullable, default KeepForever=true)
ALTER TABLE "lifecycle" ADD COLUMN IF NOT EXISTS release_retention_unit INT NOT NULL DEFAULT 0;
ALTER TABLE "lifecycle" ADD COLUMN IF NOT EXISTS release_retention_quantity INT NOT NULL DEFAULT 0;
ALTER TABLE "lifecycle" ADD COLUMN IF NOT EXISTS release_retention_keep_forever BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE "lifecycle" ADD COLUMN IF NOT EXISTS tentacle_retention_unit INT NOT NULL DEFAULT 0;
ALTER TABLE "lifecycle" ADD COLUMN IF NOT EXISTS tentacle_retention_quantity INT NOT NULL DEFAULT 0;
ALTER TABLE "lifecycle" ADD COLUMN IF NOT EXISTS tentacle_retention_keep_forever BOOLEAN NOT NULL DEFAULT TRUE;

-- Inline retention policy into phase (nullable = inherit from lifecycle)
ALTER TABLE "phase" ADD COLUMN IF NOT EXISTS release_retention_unit INT;
ALTER TABLE "phase" ADD COLUMN IF NOT EXISTS release_retention_quantity INT;
ALTER TABLE "phase" ADD COLUMN IF NOT EXISTS release_retention_keep_forever BOOLEAN;
ALTER TABLE "phase" ADD COLUMN IF NOT EXISTS tentacle_retention_unit INT;
ALTER TABLE "phase" ADD COLUMN IF NOT EXISTS tentacle_retention_quantity INT;
ALTER TABLE "phase" ADD COLUMN IF NOT EXISTS tentacle_retention_keep_forever BOOLEAN;

-- Drop old FK columns
ALTER TABLE "lifecycle" DROP COLUMN IF EXISTS release_retention_policy_id;
ALTER TABLE "lifecycle" DROP COLUMN IF EXISTS tentacle_retention_policy_id;
ALTER TABLE "phase" DROP COLUMN IF EXISTS release_retention_policy_id;
ALTER TABLE "phase" DROP COLUMN IF EXISTS tentacle_retention_policy_id;

-- Drop retention_policy table
DROP TABLE IF EXISTS "retention_policy";

-- Make Channel.lifecycle_id nullable
ALTER TABLE "channel" ALTER COLUMN lifecycle_id DROP NOT NULL;
