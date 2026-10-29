-- DOWN for 068_voucher_validation.sql — run by hand (DatabaseMigrator never
-- reads this folder). Keeps a dated copy of the tables that hold data entered
-- by people (vat_rates, tenant_voucher_settings) before dropping them;
-- voucher_validations is derived and is recomputed by GET /vouchers/{id}/validation.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f 068_voucher_validation.down.sql
BEGIN;

DO $$
DECLARE suffix TEXT := to_char(NOW(), 'YYYYMMDDHH24MISS');
BEGIN
  IF to_regclass('public.vat_rates') IS NOT NULL THEN
    EXECUTE format('CREATE TABLE %I AS TABLE vat_rates', 'vat_rates_backup_' || suffix);
  END IF;
  IF to_regclass('public.tenant_voucher_settings') IS NOT NULL THEN
    EXECUTE format('CREATE TABLE %I AS TABLE tenant_voucher_settings', 'tenant_voucher_settings_backup_' || suffix);
  END IF;
END $$;

DROP TABLE IF EXISTS voucher_validations;
DROP TABLE IF EXISTS tenant_voucher_settings;
DROP TABLE IF EXISTS vat_rates;

DELETE FROM schema_migrations WHERE version = '068_voucher_validation.sql';

COMMIT;
