-- DOWN for 071_accounting_periods.sql — run by hand (DatabaseMigrator never
-- reads this folder).
--   1. keeps a dated copy of accounting_periods and accounting_period_events;
--   2. drops exactly what 071 created. vouchers.accounting_date belongs to 070
--      and stays.
-- After this, closed periods no longer block anything.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f 071_accounting_periods.down.sql
BEGIN;

DO $$
DECLARE suffix TEXT := to_char(clock_timestamp(), 'YYYYMMDDHH24MISSUS');
BEGIN
  IF to_regclass('public.accounting_periods') IS NOT NULL THEN
    EXECUTE format('CREATE TABLE %I AS TABLE accounting_periods', 'accounting_periods_backup_' || suffix);
  END IF;
  IF to_regclass('public.accounting_period_events') IS NOT NULL THEN
    EXECUTE format('CREATE TABLE %I AS TABLE accounting_period_events', 'accounting_period_events_backup_' || suffix);
  END IF;
END $$;

DROP TRIGGER IF EXISTS trg_vouchers_period_lock ON vouchers;
DROP TRIGGER IF EXISTS trg_journal_period_lock ON voucher_journal_suggestions;
DROP FUNCTION IF EXISTS vouchers_period_lock();
DROP FUNCTION IF EXISTS journal_period_lock();
DROP FUNCTION IF EXISTS voucher_period_exempt_columns();
DROP FUNCTION IF EXISTS accounting_period_assert_open_pair(UUID, UUID, DATE, DATE);
DROP FUNCTION IF EXISTS accounting_period_assert_open(UUID, UUID, DATE);
DROP FUNCTION IF EXISTS voucher_flow_v2_enabled(UUID);

-- DROP TABLE is not blocked by the row / TRUNCATE guards.
DROP TABLE IF EXISTS accounting_period_events;
DROP TABLE IF EXISTS accounting_periods;
DROP FUNCTION IF EXISTS accounting_period_history_guard();

DELETE FROM schema_migrations WHERE version = '071_accounting_periods.sql';

COMMIT;
