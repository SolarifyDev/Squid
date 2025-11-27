CREATE TABLE machine_policy
(
    id                              SERIAL       PRIMARY KEY,
    space_id                        INT          NOT NULL,
    name                            VARCHAR(255) NOT NULL,
    description                     TEXT,
    is_default                      BOOLEAN      NOT NULL DEFAULT FALSE,
    machine_health_check_policy     TEXT,
    machine_connectivity_policy     TEXT,
    machine_cleanup_policy          TEXT,
    machine_update_policy           TEXT,
    machine_rpc_call_retry_policy   TEXT,
    polling_request_queue_timeout   TEXT,
    connection_retry_sleep_interval TEXT,
    connection_retry_count_limit    INT          NOT NULL DEFAULT 0,
    connection_retry_time_limit     TEXT,
    connection_connect_timeout      TEXT
);
