-- ---------------------------------------------------------------------------
-- Migration 19 — sim_ap_ledger + alter sim_suppliers/sim_purchase_orders
--
-- UC3 (Expense/AP) mirror của UC1+UC2 (AR side). Cần cho voucher_type ∈
--   {invoice_vat_in, payment, bank_transfer, expense_claim}.
--
-- Semantics kế toán VN (TT200):
--   Có 331 (credit) → tăng công nợ NCC (nhận HĐ NCC)
--   Nợ 331 (debit)  → giảm công nợ NCC (thanh toán qua TM/CK)
--
-- Idempotent. Áp dụng thủ công trên cosmos:
--   docker exec -i aiq-postgres psql -U aiq -d aiq -f /tmp/19_sim_ap_ledger.sql
--
-- Guarded ALTERs — sim_suppliers + sim_purchase_orders được tạo bởi
-- scripts/vietdang_simulator_{seed,phase2}.sql (không nằm trong init/).
-- ---------------------------------------------------------------------------

BEGIN;

-- 1. sim_ap_ledger — đối xứng sim_ar_ledger
CREATE TABLE IF NOT EXISTS sim_ap_ledger (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    supplier_id     UUID NOT NULL REFERENCES sim_suppliers(id),
    voucher_id      UUID,                              -- audit trace tới vouchers.id (nullable cho seed opening balance)
    document_date   DATE NOT NULL,
    document_number TEXT NOT NULL,                     -- Số HĐ NCC / phiếu chi / uỷ nhiệm chi
    document_type   TEXT NOT NULL,                     -- invoice / payment / discount / adjustment
    debit           NUMERIC(18,2) DEFAULT 0,           -- Nợ 331 → giảm phải trả (chi tiền)
    credit          NUMERIC(18,2) DEFAULT 0,           -- Có 331 → tăng phải trả (nhận HĐ NCC)
    balance_after   NUMERIC(18,2),
    due_date        DATE,
    reference_note  TEXT,
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_sim_ap_supplier_date ON sim_ap_ledger(supplier_id, document_date DESC);
CREATE INDEX IF NOT EXISTS idx_sim_ap_open ON sim_ap_ledger(supplier_id, due_date) WHERE debit = 0;
CREATE INDEX IF NOT EXISTS idx_sim_ap_voucher ON sim_ap_ledger(voucher_id) WHERE voucher_id IS NOT NULL;

-- 2. ALTER sim_suppliers — thêm payment_terms_days INT (backfill từ text)
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'sim_suppliers') THEN
        ALTER TABLE sim_suppliers ADD COLUMN IF NOT EXISTS payment_terms_days INT DEFAULT 0;

        UPDATE sim_suppliers SET payment_terms_days = 15
         WHERE payment_terms_days = 0 AND payment_terms ILIKE '%Net 15%';
        UPDATE sim_suppliers SET payment_terms_days = 30
         WHERE payment_terms_days = 0 AND payment_terms ILIKE '%Net 30%';
        UPDATE sim_suppliers SET payment_terms_days = 45
         WHERE payment_terms_days = 0 AND payment_terms ILIKE '%Net 45%';
        UPDATE sim_suppliers SET payment_terms_days = 60
         WHERE payment_terms_days = 0 AND (payment_terms ILIKE '%60 days%' OR payment_terms ILIKE '%LC 60%');
        -- T/T before shipment → 0 (trả trước, không phát sinh credit terms)
    END IF;
END$$;

-- 3. ALTER sim_purchase_orders — thêm total_amount + payment_terms_days + delivery_note_ref
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'sim_purchase_orders') THEN
        ALTER TABLE sim_purchase_orders ADD COLUMN IF NOT EXISTS total_amount NUMERIC(18,2);
        ALTER TABLE sim_purchase_orders ADD COLUMN IF NOT EXISTS payment_terms_days INT DEFAULT 0;
        ALTER TABLE sim_purchase_orders ADD COLUMN IF NOT EXISTS delivery_note_ref TEXT;

        -- total_amount = subtotal_vnd × (1 + import_duty% + vat_import%)
        UPDATE sim_purchase_orders
           SET total_amount = COALESCE(subtotal_vnd, 0) * (1
                            + COALESCE(import_duty_pct, 0) / 100.0
                            + COALESCE(vat_import_pct, 0)  / 100.0)
         WHERE total_amount IS NULL;

        UPDATE sim_purchase_orders SET payment_terms_days = 15
         WHERE payment_terms_days = 0 AND payment_terms ILIKE '%Net 15%';
        UPDATE sim_purchase_orders SET payment_terms_days = 30
         WHERE payment_terms_days = 0 AND payment_terms ILIKE '%Net 30%';
        UPDATE sim_purchase_orders SET payment_terms_days = 60
         WHERE payment_terms_days = 0 AND payment_terms ILIKE '%60 days%';

        UPDATE sim_purchase_orders SET delivery_note_ref = goods_received_note
         WHERE delivery_note_ref IS NULL AND goods_received_note IS NOT NULL;
    END IF;
END$$;

-- 4. Seed opening balances — khớp sim_suppliers.current_ap hiện có.
--    Skip nếu tenant vietdang chưa tồn tại (VD môi trường dev sạch).
DO $$
DECLARE
    v_tenant_id UUID;
BEGIN
    SELECT id INTO v_tenant_id FROM tenants WHERE code = 'vietdang' LIMIT 1;
    IF v_tenant_id IS NULL THEN RETURN; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'sim_suppliers') THEN
        RETURN;
    END IF;

    INSERT INTO sim_ap_ledger (tenant_id, supplier_id, document_date, document_number,
                               document_type, credit, balance_after, due_date, reference_note)
    SELECT v_tenant_id, s.id, x.document_date::date, x.document_number, 'invoice',
           x.credit, x.credit, x.due_date::date, 'Opening balance seed'
    FROM sim_suppliers s
    JOIN (VALUES
        -- KaVo DE 750M — HĐ tháng 11/2025, quá hạn 30/01
        ('NCC-DE-001', '2025-11-15', 'INV-KAV-2025-11-450', 750000000.00, '2026-01-30'),
        -- Sirona DE 420M — HĐ tháng 12/2025, quá hạn 15/02
        ('NCC-DE-002', '2025-12-05', 'INV-SIR-2025-12-380', 420000000.00, '2026-02-15'),
        -- VN suppliers — trong hạn
        ('NCC-VN-001', '2026-08-05', 'HDNCC-VN001-08-05',    85000000.00, '2026-09-04'),
        ('NCC-VN-002', '2026-08-01', 'HDNCC-VN002-08-01',    12500000.00, '2026-08-31'),
        ('NCC-VN-003', '2026-08-05', 'HDNCC-VN003-08-05',     6800000.00, '2026-08-20')
    ) AS x(supplier_code, document_date, document_number, credit, due_date)
      ON s.supplier_code = x.supplier_code AND s.tenant_id = v_tenant_id
    WHERE NOT EXISTS (
        SELECT 1 FROM sim_ap_ledger l
         WHERE l.supplier_id = s.id AND l.document_number = x.document_number
    );
END$$;

COMMIT;

-- ---------------------------------------------------------------------------
-- Verify
-- ---------------------------------------------------------------------------
\echo ''
\echo '=== sim_ap_ledger opening balances ==='
SELECT s.supplier_code, l.document_number, l.credit, l.due_date, l.reference_note
FROM sim_ap_ledger l JOIN sim_suppliers s ON s.id = l.supplier_id
ORDER BY s.supplier_code, l.document_date;

\echo ''
\echo '=== sim_suppliers payment_terms_days ==='
SELECT supplier_code, name, payment_terms, payment_terms_days
FROM sim_suppliers ORDER BY supplier_code;

\echo ''
\echo '=== sim_purchase_orders total_amount + terms ==='
SELECT po_number, supplier_code, subtotal_vnd, total_amount, payment_terms, payment_terms_days, delivery_note_ref
FROM sim_purchase_orders ORDER BY po_date;
