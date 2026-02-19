ALTER TABLE "channel" ADD COLUMN description VARCHAR(200);
ALTER TABLE "channel" ADD COLUMN is_default boolean;
ALTER TABLE "channel" DROP COLUMN json;
ALTER TABLE "channel" DROP COLUMN last_modified;