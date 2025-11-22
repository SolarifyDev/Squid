ALTER TABLE project DROP COLUMN cloned_from_project_id;
ALTER TABLE project ADD COLUMN cloned_from_project_id INTEGER;