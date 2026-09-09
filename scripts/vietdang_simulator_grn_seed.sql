-- ---------------------------------------------------------------------------
-- Việt Đăng demo seed — sim_goods_received_notes (GRN) cho AP 3-way match.
--
-- Mirror của sim_delivery_notes (AR side). Cho phép match:
--   invoice_vat_in → sim_purchase_orders → sim_goods_received_notes
--     → (optional Sprint 5) sim_purchase_contracts
--
-- Schema is canonical in Gateway migration 020. This explicit script is
-- demo-only and must never run as docker-entrypoint initialization.
-- ---------------------------------------------------------------------------

BEGIN;

CREATE TABLE IF NOT EXISTS sim_goods_received_notes (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    grn_number      TEXT NOT NULL,                        -- GRN-2026-078
    grn_date        DATE NOT NULL,
    po_number       TEXT NOT NULL,                        -- FK bằng text tới sim_purchase_orders.po_number
    warehouse_to    TEXT,                                  -- kho nhận (HCM/HN/DN)
    delivered_by    TEXT,                                  -- forwarder / carrier / trực tiếp NCC
    received_by     TEXT,                                  -- thủ kho ký nhận
    status          TEXT DEFAULT 'received',              -- received / partial / rejected
    notes           TEXT,
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, grn_number)
);

CREATE INDEX IF NOT EXISTS idx_sim_grn_po ON sim_goods_received_notes(tenant_id, po_number);
CREATE INDEX IF NOT EXISTS idx_sim_grn_date ON sim_goods_received_notes(tenant_id, grn_date DESC);

-- Seed 6 GRN — 1 cho mỗi PO đã status='received' hoặc 'partial'.
DO $$
DECLARE
    v_tenant_id UUID;
BEGIN
    SELECT id INTO v_tenant_id FROM tenants WHERE code = 'vietdang' LIMIT 1;
    IF v_tenant_id IS NULL THEN RETURN; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'sim_purchase_orders') THEN
        RETURN;
    END IF;

    INSERT INTO sim_goods_received_notes (
        tenant_id, grn_number, grn_date, po_number,
        warehouse_to, delivered_by, received_by, status, notes)
    VALUES
        (v_tenant_id, 'GRN-2026-078', '2026-07-25', 'PO-2026-0032',
         'Kho HCM', 'DHL Global Forwarding VN', 'Nguyễn Văn Kho',
         'received', 'Ghế KaVo E80 x 1 — đủ hàng, không hư hại. Đã unbox + check S/N khớp E80-2026-1247.'),

        (v_tenant_id, 'GRN-2026-081', '2026-07-30', 'PO-2026-0035',
         'Kho HCM', 'Kuehne+Nagel VN', 'Nguyễn Văn Kho',
         'received', 'Máy X-quang Sirona CBCT x 1 — đủ. Đã test power-on OK.'),

        (v_tenant_id, 'GRN-2026-085', '2026-07-18', 'PO-2026-0041',
         'Kho HCM', 'Yamato Transport', 'Nguyễn Văn Kho',
         'partial', 'Ghế Morita SP x 3 (60% đơn) — đã về. Tay khoan NSK 40% còn ship sau 30/08.'),

        (v_tenant_id, 'GRN-2026-095', '2026-07-28', 'PO-2026-0055',
         'Kho HCM', 'NCC-VN-001 tự giao', 'Nguyễn Văn Kho',
         'received', 'Vật tư y tế nội địa — đủ theo PO. Có HĐ GTGT 00005678 kèm theo.'),

        (v_tenant_id, 'GRN-2026-101', '2026-08-15', 'PO-2026-0062',
         'Kho HCM', 'NCC-VN-002 tự giao', 'Nguyễn Văn Kho',
         'received', 'Composite + vật liệu implant — nhận đủ. Chờ HĐ NCC.'),

        (v_tenant_id, 'GRN-2026-107', '2026-08-10', 'PO-2026-0065',
         'Kho HCM', 'Cargo VN', 'Phạm Thị Nga',
         'received', 'Dịch vụ vận chuyển hoàn tất chuyến HP → HCM.')
    ON CONFLICT (tenant_id, grn_number) DO NOTHING;
END$$;

COMMIT;

-- Verify
\echo ''
\echo '=== sim_goods_received_notes ==='
SELECT grn_number, grn_date, po_number, warehouse_to, status
FROM sim_goods_received_notes
ORDER BY grn_date;
