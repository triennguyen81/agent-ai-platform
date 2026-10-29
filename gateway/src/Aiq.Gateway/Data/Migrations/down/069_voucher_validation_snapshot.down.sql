-- DOWN for 069_voucher_validation_snapshot.sql — run by hand (DatabaseMigrator
-- never reads this folder). Drops only the two snapshot columns; the stored
-- results themselves stay. The columns are derived (recomputed on the next
-- voucher change), so no backup is kept.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f 069_voucher_validation_snapshot.down.sql
BEGIN;

ALTER TABLE IF EXISTS voucher_validations
    DROP CONSTRAINT IF EXISTS voucher_validations_fingerprint_check,
    DROP COLUMN IF EXISTS voucher_fingerprint,
    DROP COLUMN IF EXISTS voucher_updated_at;

DELETE FROM schema_migrations WHERE version = '069_voucher_validation_snapshot.sql';

COMMIT;
