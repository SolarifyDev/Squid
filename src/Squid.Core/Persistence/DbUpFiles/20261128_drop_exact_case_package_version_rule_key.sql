-- The case-insensitive unique index on (feed_id, lower(package_id)) implies the exact-case constraint the first migration created, and the rule upsert's
-- ON CONFLICT names only the case-insensitive one. Keeping both made two concurrent saves of the same package id fail with a unique violation on the
-- exact-case key instead of updating the rule. Re-runnable.
ALTER TABLE package_version_rule DROP CONSTRAINT IF EXISTS uq_package_version_rule_feed_package;
