ALTER TABLE "deployment_account" ADD COLUMN IF NOT EXISTS credentials TEXT;

ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS token;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS username;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS password;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS private_key_file;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS private_key_passphrase;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS client_certificate_data;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS client_certificate_key_data;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS access_key;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS secret_key;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS assume_role_arn;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS subscription_number;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS client_id;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS tenant_id;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS key;
ALTER TABLE "deployment_account" DROP COLUMN IF EXISTS json;
