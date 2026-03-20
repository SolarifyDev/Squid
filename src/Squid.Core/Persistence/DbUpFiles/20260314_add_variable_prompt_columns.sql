ALTER TABLE variable ADD COLUMN prompt_label TEXT NULL;
ALTER TABLE variable ADD COLUMN prompt_description TEXT NULL;
ALTER TABLE variable ADD COLUMN prompt_required BOOLEAN NOT NULL DEFAULT FALSE;
