-- Add job_id column to server_task for Hangfire job tracking
ALTER TABLE "server_task" ADD COLUMN IF NOT EXISTS job_id VARCHAR(100);

-- Extend deployment_account with multi-auth support
-- UsernamePassword
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS username TEXT;
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS password TEXT;

-- SshKeyPair
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS private_key_file TEXT;
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS private_key_passphrase TEXT;

-- ClientCertificate
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS client_certificate_data TEXT;
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS client_certificate_key_data TEXT;

-- AWS
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS access_key TEXT;
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS secret_key TEXT;
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS assume_role_arn TEXT;

-- Azure
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS subscription_number TEXT;
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS client_id TEXT;
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS tenant_id TEXT;
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS key TEXT;

-- Generic extension
ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS json TEXT;
