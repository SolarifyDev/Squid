-- Adds FeedId column to release_selected_package so the AcquirePackages synthetic step
-- can resolve the feed for each package without needing to look it up from action properties.

ALTER TABLE release_selected_package ADD COLUMN feed_id INTEGER NOT NULL DEFAULT 0;
