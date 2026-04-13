-- Convert machine.endpoint from text to jsonb for proper JSON querying via EF Core.
-- Enables LINQ queries with jsonb_extract_path_text instead of raw SQL.

ALTER TABLE machine
    ALTER COLUMN endpoint TYPE jsonb
    USING CASE
        WHEN endpoint IS NOT NULL AND endpoint != '' THEN endpoint::jsonb
        ELSE NULL
    END;

-- Recreate the subscription ID index without the unnecessary ::jsonb cast
DROP INDEX IF EXISTS ix_machine_endpoint_subscription_id;
CREATE INDEX ix_machine_endpoint_subscription_id
    ON machine USING btree ((endpoint ->> 'SubscriptionId'))
    WHERE (endpoint ->> 'SubscriptionId') IS NOT NULL;

-- Add index for URI lookups (listening tentacle registration)
CREATE INDEX ix_machine_endpoint_uri
    ON machine USING btree ((endpoint ->> 'Uri'))
    WHERE (endpoint ->> 'Uri') IS NOT NULL;
