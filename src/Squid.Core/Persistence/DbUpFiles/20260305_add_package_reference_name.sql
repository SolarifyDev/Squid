ALTER TABLE "release_selected_package"
ADD COLUMN IF NOT EXISTS package_reference_name VARCHAR(200) NOT NULL DEFAULT '';
