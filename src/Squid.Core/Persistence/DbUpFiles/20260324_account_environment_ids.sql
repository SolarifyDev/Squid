-- Rename singular environment_id to plural environment_ids (JSON array of int[])
ALTER TABLE deployment_account RENAME COLUMN environment_id TO environment_ids;
