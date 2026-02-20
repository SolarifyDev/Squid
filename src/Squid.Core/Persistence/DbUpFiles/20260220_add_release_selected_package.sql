CREATE TABLE IF NOT EXISTS "release_selected_package"
(
    id          INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    release_id  INT          NOT NULL,
    action_name VARCHAR(200) NOT NULL,
    version     VARCHAR(200) NOT NULL
);
