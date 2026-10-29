-- DOWN for 070_voucher_lifecycle.sql — run by hand (DatabaseMigrator never
-- reads this folder).
--   1. keeps a dated copy of voucher_events and tenant_feature_flags;
--   2. puts back status = 'journalized' on the rows 070 converted (from their
--      legacy_backfill events), without touching updated_at;
--   3. drops exactly what 070 created and restores the original updated_at trigger.
-- Soft-deleted vouchers (deleted_at) become visible again: review them before
-- running this on a tenant that has the v2 flag on.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f 070_voucher_lifecycle.down.sql
BEGIN;

-- 071 (accounting periods) reads tenant_feature_flags from its triggers: undo it first.
DO $$
BEGIN
  IF to_regclass('public.accounting_periods') IS NOT NULL THEN
    RAISE EXCEPTION 'run down/071_accounting_periods.down.sql before this script';
  END IF;
END $$;

DO $$
DECLARE suffix TEXT := to_char(clock_timestamp(), 'YYYYMMDDHH24MISSUS');
BEGIN
  IF to_regclass('public.voucher_events') IS NOT NULL THEN
    EXECUTE format('CREATE TABLE %I AS TABLE voucher_events', 'voucher_events_backup_' || suffix);
  END IF;
  IF to_regclass('public.tenant_feature_flags') IS NOT NULL THEN
    EXECUTE format('CREATE TABLE %I AS TABLE tenant_feature_flags', 'tenant_feature_flags_backup_' || suffix);
  END IF;
END $$;

DROP TRIGGER IF EXISTS trg_vouchers_lifecycle_event ON vouchers;
DROP TRIGGER IF EXISTS trg_vouchers_lifecycle_sync ON vouchers;

SELECT set_config('aiq.backfill', 'on', true);
DO $$
BEGIN
  IF to_regclass('public.voucher_events') IS NOT NULL THEN
    UPDATE vouchers v SET status = 'journalized'
      FROM voucher_events e
     WHERE e.voucher_id = v.id AND e.event_type = 'legacy_backfill' AND e.before ->> 'status' = 'journalized';
  END IF;
END $$;

CREATE OR REPLACE FUNCTION vouchers_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP FUNCTION IF EXISTS vouchers_lifecycle_event();
DROP FUNCTION IF EXISTS vouchers_lifecycle_sync();
DROP FUNCTION IF EXISTS voucher_status_from_lifecycle(TEXT);
DROP FUNCTION IF EXISTS voucher_lifecycle_from_status(TEXT);

DROP TABLE IF EXISTS voucher_events;
DROP FUNCTION IF EXISTS voucher_events_append_only();
DROP TABLE IF EXISTS tenant_feature_flags;

DROP INDEX IF EXISTS idx_vouchers_lifecycle;
ALTER TABLE vouchers
    DROP CONSTRAINT IF EXISTS chk_vouchers_lifecycle_state,
    DROP COLUMN IF EXISTS supersedes_id,
    DROP COLUMN IF EXISTS deleted_by,
    DROP COLUMN IF EXISTS deleted_at,
    DROP COLUMN IF EXISTS reversal_reason,
    DROP COLUMN IF EXISTS reversed_at,
    DROP COLUMN IF EXISTS reversed_by,
    DROP COLUMN IF EXISTS accounting_date,
    DROP COLUMN IF EXISTS posted_at,
    DROP COLUMN IF EXISTS posted_by,
    DROP COLUMN IF EXISTS approved_at,
    DROP COLUMN IF EXISTS approved_by,
    DROP COLUMN IF EXISTS submitted_at,
    DROP COLUMN IF EXISTS submitted_by,
    DROP COLUMN IF EXISTS lifecycle_version,
    DROP COLUMN IF EXISTS lifecycle_state;

DELETE FROM schema_migrations WHERE version = '070_voucher_lifecycle.sql';

COMMIT;
