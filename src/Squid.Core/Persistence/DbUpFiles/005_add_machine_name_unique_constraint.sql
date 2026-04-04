-- Prevent duplicate machine names within the same space
CREATE UNIQUE INDEX IF NOT EXISTS ix_machine_name_space_id ON machine (name, space_id);
