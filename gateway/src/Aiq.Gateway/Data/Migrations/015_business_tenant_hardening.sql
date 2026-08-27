-- Phase 6: preserve existing business data while making tenant ownership
-- mandatory and tenant-scoping indexes/deduplication keys.

UPDATE vouchers v
SET tenant_id = u.tenant_id
FROM users u
WHERE v.tenant_id IS NULL AND v.user_id = u.id AND u.tenant_id IS NOT NULL;

DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM vouchers WHERE tenant_id IS NULL) THEN
    RAISE EXCEPTION 'Cannot harden vouchers: rows without an authoritative tenant remain';
  END IF;
END $$;

ALTER TABLE vouchers ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE vouchers DROP CONSTRAINT IF EXISTS vouchers_tenant_id_fkey;
ALTER TABLE vouchers ADD CONSTRAINT vouchers_tenant_id_fkey
  FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE RESTRICT;

CREATE INDEX IF NOT EXISTS idx_vouchers_tenant_user_status
  ON vouchers(tenant_id, user_id, status);
CREATE INDEX IF NOT EXISTS idx_voucher_lines_parent
  ON voucher_line_items(voucher_id, ordinal);
CREATE INDEX IF NOT EXISTS idx_voucher_journal_parent
  ON voucher_journal_suggestions(voucher_id, entry_no);
CREATE INDEX IF NOT EXISTS idx_book_pages_parent
  ON book_pages(book_id, page_number);

DROP INDEX IF EXISTS uq_notifications_dedupe;
CREATE UNIQUE INDEX IF NOT EXISTS uq_notifications_tenant_dedupe
  ON notifications(tenant_id, user_id, kind, ref_id)
  WHERE ref_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_notifications_tenant_user_unread
  ON notifications(tenant_id, user_id, created_at DESC)
  WHERE read_at IS NULL;

-- Parent/owner agreement is enforced for all new writes, while preserving
-- legacy rows. Users can only own business records inside their one tenant.
CREATE OR REPLACE FUNCTION enforce_business_user_tenant() RETURNS trigger AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM users u WHERE u.id=NEW.user_id AND u.tenant_id=NEW.tenant_id) THEN
    RAISE EXCEPTION 'business resource owner must belong to resource tenant';
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_vouchers_user_tenant ON vouchers;
CREATE TRIGGER trg_vouchers_user_tenant BEFORE INSERT OR UPDATE OF tenant_id,user_id ON vouchers
FOR EACH ROW EXECUTE FUNCTION enforce_business_user_tenant();

DROP TRIGGER IF EXISTS trg_notifications_user_tenant ON notifications;
CREATE TRIGGER trg_notifications_user_tenant BEFORE INSERT OR UPDATE OF tenant_id,user_id ON notifications
FOR EACH ROW EXECUTE FUNCTION enforce_business_user_tenant();
