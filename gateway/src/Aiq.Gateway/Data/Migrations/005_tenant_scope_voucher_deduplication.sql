-- Voucher document identity is tenant-local. A global uniqueness constraint can
-- cause one tenant's invoice import to update another tenant's voucher.
DROP INDEX IF EXISTS uniq_voucher_document;

CREATE UNIQUE INDEX uniq_voucher_document
    ON vouchers(tenant_id, seller_mst, document_number, document_date)
    WHERE tenant_id IS NOT NULL
      AND seller_mst IS NOT NULL
      AND document_number IS NOT NULL
      AND document_date IS NOT NULL;
