-- A package id names one package whatever its letter case, so a feed may hold only one version rule per package ignoring case ("Acme.Web" and "acme.web" are the same rule).
-- The first migration made (feed_id, package_id) unique, which still allowed both spellings. Rows that differ only in case are collapsed first, keeping the most recently
-- modified one, so the new unique index can be built. Additive and re-runnable.
DELETE FROM package_version_rule r
USING package_version_rule k
WHERE r.feed_id = k.feed_id
  AND lower(r.package_id) = lower(k.package_id)
  AND (r.last_modified_date, r.id) < (k.last_modified_date, k.id);

CREATE UNIQUE INDEX IF NOT EXISTS ux_package_version_rule_feed_lower_package_id ON package_version_rule (feed_id, lower(package_id));
