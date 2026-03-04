CREATE TABLE IF NOT EXISTS environment
(
    id                              UUID         PRIMARY KEY,
    space_id                        UUID         NOT NULL,
    slug                            VARCHAR(255) NOT NULL,
    name                            VARCHAR(255) NOT NULL,
    description                     TEXT,
    sort_order                      INTEGER      NOT NULL DEFAULT 0,
    use_guided_failure              BOOLEAN      NOT NULL DEFAULT false,
    allow_dynamic_infrastructure    BOOLEAN      NOT NULL DEFAULT true,
    last_modified_on                TIMESTAMPTZ,
    last_modified_by                VARCHAR(255)
);
