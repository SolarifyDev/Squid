ALTER TABLE "machine" DROP COLUMN machine_policy_id;
ALTER TABLE "machine" ADD COLUMN machine_policy_id INT;
ALTER TABLE "machine" DROP COLUMN space_id;
ALTER TABLE "machine" ADD COLUMN space_id INT;
