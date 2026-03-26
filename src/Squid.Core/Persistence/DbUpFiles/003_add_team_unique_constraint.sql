-- Prevent duplicate team names within the same space
CREATE UNIQUE INDEX IF NOT EXISTS ix_team_name_space_id ON team (name, space_id);
