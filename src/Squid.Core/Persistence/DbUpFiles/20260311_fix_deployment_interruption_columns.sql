ALTER TABLE deployment_interruption RENAME COLUMN "Id" TO id;
ALTER TABLE deployment_interruption RENAME COLUMN "ServerTaskId" TO server_task_id;
ALTER TABLE deployment_interruption RENAME COLUMN "DeploymentId" TO deployment_id;
ALTER TABLE deployment_interruption RENAME COLUMN "StepDisplayOrder" TO step_display_order;
ALTER TABLE deployment_interruption RENAME COLUMN "StepName" TO step_name;
ALTER TABLE deployment_interruption RENAME COLUMN "ActionName" TO action_name;
ALTER TABLE deployment_interruption RENAME COLUMN "MachineName" TO machine_name;
ALTER TABLE deployment_interruption RENAME COLUMN "ErrorMessage" TO error_message;
ALTER TABLE deployment_interruption RENAME COLUMN "Resolution" TO resolution;
ALTER TABLE deployment_interruption RENAME COLUMN "CreatedAt" TO created_at;
ALTER TABLE deployment_interruption RENAME COLUMN "ResolvedAt" TO resolved_at;
ALTER TABLE deployment_interruption RENAME COLUMN "SpaceId" TO space_id;

DROP INDEX IF EXISTS ix_deployment_interruption_server_task_id;
CREATE INDEX IF NOT EXISTS ix_deployment_interruption_server_task_id ON deployment_interruption (server_task_id);
