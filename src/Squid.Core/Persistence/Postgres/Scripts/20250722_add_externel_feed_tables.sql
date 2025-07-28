CREATE TABLE IF NOT EXISTS external_feed
(
    id                                   UUID         PRIMARY KEY,
    feed_type                            VARCHAR(255) NOT NULL,
    api_version                          VARCHAR(255),
    registry_path                        TEXT,
    feed_uri                             TEXT         NOT NULL,
    username                             VARCHAR(255),
    password                             TEXT,
    name                                 VARCHAR(255) NOT NULL,
    slug                                 VARCHAR(255) NOT NULL,
    package_acquisition_location_options TEXT,
    space_id                             UUID         NOT NULL,
    last_modified_on                      TIMESTAMPTZ,
    last_modified_by                      VARCHAR(255)
);