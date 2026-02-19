ALTER TABLE deployment_process
    ADD COLUMN IF NOT EXISTS name VARCHAR(200),
    ADD COLUMN IF NOT EXISTS description TEXT,
    ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ DEFAULT NOW(),
    ADD COLUMN IF NOT EXISTS created_by VARCHAR(100),
    ADD COLUMN IF NOT EXISTS last_modified TIMESTAMPTZ DEFAULT NOW(),
    ADD COLUMN IF NOT EXISTS last_modified_by VARCHAR(100);

ALTER TABLE deployment_process RENAME COLUMN owner_id TO project_id;

ALTER TABLE deployment_process DROP COLUMN IF EXISTS json;
CREATE TABLE IF NOT EXISTS deployment_step (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id UUID NOT NULL,
    step_order INTEGER NOT NULL,
    name VARCHAR(200) NOT NULL,
    step_type VARCHAR(50) NOT NULL,
    condition TEXT,
    start_trigger VARCHAR(50),
    package_requirement VARCHAR(50),
    is_disabled BOOLEAN NOT NULL DEFAULT FALSE,
    is_required BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE TABLE IF NOT EXISTS deployment_step_property (
    step_id UUID NOT NULL,
    property_name VARCHAR(200) NOT NULL,
    property_value TEXT,
    
    PRIMARY KEY (step_id, property_name)
);
CREATE TABLE IF NOT EXISTS deployment_action (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    step_id UUID NOT NULL,
    action_order INTEGER NOT NULL,
    name VARCHAR(200) NOT NULL,
    action_type VARCHAR(100) NOT NULL,
    worker_pool_id UUID,
    is_disabled BOOLEAN NOT NULL DEFAULT FALSE,
    is_required BOOLEAN NOT NULL DEFAULT TRUE,
    can_be_used_for_project_versioning BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE TABLE IF NOT EXISTS deployment_action_property (
    action_id UUID NOT NULL,
    property_name VARCHAR(200) NOT NULL,
    property_value TEXT,
    
    PRIMARY KEY (action_id, property_name)
);
CREATE TABLE IF NOT EXISTS action_environments (
    action_id UUID NOT NULL,
    environment_id UUID NOT NULL,
    
    PRIMARY KEY (action_id, environment_id)
);
CREATE TABLE IF NOT EXISTS action_channels (
    action_id UUID NOT NULL,
    channel_id UUID NOT NULL,
    
    PRIMARY KEY (action_id, channel_id)
);
CREATE TABLE IF NOT EXISTS action_machine_roles (
    action_id UUID NOT NULL,
    machine_role VARCHAR(100) NOT NULL,
    
    PRIMARY KEY (action_id, machine_role)
);
CREATE INDEX IF NOT EXISTS ix_deployment_process_project_id ON deployment_process(project_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_deployment_process_project_version ON deployment_process(project_id, version);
CREATE INDEX IF NOT EXISTS ix_deployment_process_space_id ON deployment_process(space_id);

CREATE INDEX IF NOT EXISTS ix_deployment_step_process_id ON deployment_step(process_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_deployment_step_process_order ON deployment_step(process_id, step_order);

CREATE INDEX IF NOT EXISTS ix_deployment_step_property_step_id ON deployment_step_property(step_id);

CREATE INDEX IF NOT EXISTS ix_deployment_action_step_id ON deployment_action(step_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_deployment_action_step_order ON deployment_action(step_id, action_order);

CREATE INDEX IF NOT EXISTS ix_deployment_action_property_action_id ON deployment_action_property(action_id);
