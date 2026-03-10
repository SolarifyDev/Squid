CREATE TABLE IF NOT EXISTS deployment_interruption (
    "Id" SERIAL PRIMARY KEY,
    "ServerTaskId" INT NOT NULL,
    "DeploymentId" INT NOT NULL,
    "StepDisplayOrder" INT NOT NULL DEFAULT 0,
    "StepName" TEXT,
    "ActionName" TEXT,
    "MachineName" TEXT,
    "ErrorMessage" TEXT,
    "Resolution" TEXT,
    "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    "ResolvedAt" TIMESTAMPTZ,
    "SpaceId" INT NOT NULL DEFAULT 1
);

CREATE INDEX IF NOT EXISTS ix_deployment_interruption_server_task_id ON deployment_interruption ("ServerTaskId");
