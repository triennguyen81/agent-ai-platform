-- ============================================================================
-- Việt Đăng Simulator — Phase 2
--
-- Extends Phase 1 (sim_customers/suppliers/employees/ar_ledger) with:
--   1. sim_products              — catalog thiết bị nha khoa
--   2. sim_sales_orders + lines  — enables 3-way match (UC-ACC-001 §5.6)
--   3. sim_delivery_notes        — phiếu xuất kho
--   4. sim_contracts + milestones — long-term contracts với payment schedule
--   5. sim_bank_accounts + txns  — enables bank_statement voucher demo
--   6. sim_purchase_orders       — AP side, matches sim_suppliers
--
-- Data is stitched to Phase 1: sales orders reference existing customers +
-- products; delivery notes reference sales orders; contracts have milestones
-- that map to sim_ar_ledger invoices. This lets the demo show:
--   "Invoice 00001101 matches PO SO-2026-0087 + DN-2026-0091 for KaVo E80
--    sold to PK-001 Nha Xinh, part of contract HD-2026-042."
--
-- All operations idempotent. Requires Phase 1 seed already applied.
-- ============================================================================

BEGIN;

-- Guard: Phase 1 must exist
DO $$
DECLARE
    v_tenant_id UUID;
    v_customer_count INT;
BEGIN
    SELECT id INTO v_tenant_id FROM tenants WHERE code = 'vietdang';
    IF v_tenant_id IS NULL THEN
        RAISE EXCEPTION 'Tenant "vietdang" not found — run vietdang_accounting_setup.sql first';
    END IF;
    SELECT COUNT(*) INTO v_customer_count FROM sim_customers WHERE tenant_id = v_tenant_id;
    IF v_customer_count = 0 THEN
        RAISE EXCEPTION 'No sim_customers found — run vietdang_simulator_seed.sql (Phase 1) first';
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 1. sim_products — catalog
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sim_products (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    sku            TEXT NOT NULL,
    name           TEXT NOT NULL,
    category       TEXT NOT NULL,               -- ghe / xray / scan3d / autoclave / tay_khoan / vat_tu / phu_tung
    unit           TEXT NOT NULL DEFAULT 'cái',
    unit_price     NUMERIC(18,2) NOT NULL,      -- giá bán chưa VAT
    vat_rate       NUMERIC(5,2) DEFAULT 5,      -- 5% thiết bị y tế, 10% dịch vụ/VT không y tế
    cost_price     NUMERIC(18,2),                -- giá vốn tham khảo
    warranty_months INT DEFAULT 12,
    supplier_code  TEXT,                         -- link soft tới sim_suppliers
    stock_on_hand  INT DEFAULT 0,
    reorder_point  INT DEFAULT 0,
    extras         JSONB DEFAULT '{}'::jsonb,
    created_at     TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, sku)
);

CREATE INDEX IF NOT EXISTS idx_sim_products_category ON sim_products(tenant_id, category);

INSERT INTO sim_products (tenant_id, sku, name, category, unit, unit_price, vat_rate, cost_price, warranty_months, supplier_code, stock_on_hand, reorder_point)
SELECT (SELECT id FROM tenants WHERE code='vietdang'), p.*
FROM (VALUES
    -- Ghế nha khoa
    ('GHE-KAVO-E80',      'Ghế nha khoa KaVo Estetica E80',           'ghe',        'cái',  350000000, 5, 260000000, 24, 'NCC-DE-001',  8, 3),
    ('GHE-KAVO-E70',      'Ghế nha khoa KaVo Estetica E70',           'ghe',        'cái',  280000000, 5, 210000000, 24, 'NCC-DE-001', 12, 4),
    ('GHE-SIRONA-C8',     'Ghế nha khoa Sirona Teneo C8+',            'ghe',        'cái',  420000000, 5, 315000000, 24, 'NCC-DE-002',  5, 2),
    ('GHE-MORITA-SP',     'Ghế nha khoa Morita Signo Treasure SP',    'ghe',        'cái',  380000000, 5, 285000000, 24, 'NCC-JP-001',  6, 2),
    ('GHE-VN-STD',        'Ghế nha khoa phổ thông VN (nội địa)',       'ghe',        'cái',   95000000, 5,  68000000, 12, 'NCC-VN-001', 20, 5),

    -- Máy X-quang
    ('XRAY-KAVO-2D',      'Máy X-quang KaVo OP 2D',                    'xray',       'cái',  185000000, 5, 138000000, 12, 'NCC-DE-001',  4, 2),
    ('XRAY-SIRONA-CBCT',  'Máy X-quang Sirona ORTHOPHOS SL 3D CBCT',   'xray',       'cái',  650000000, 5, 480000000, 12, 'NCC-DE-002',  2, 1),

    -- Scan 3D
    ('SCAN-ITERO-5D',     'Máy Scan iTero Element 5D',                 'scan3d',     'cái',  520000000, 5, 390000000, 12, 'NCC-DE-002',  3, 2),
    ('SCAN-MEDIT-I700',   'Máy Scan Medit i700',                       'scan3d',     'cái',  380000000, 5, 285000000, 12, 'NCC-DE-002',  4, 2),

    -- Autoclave
    ('AUTO-MELAG-VACUKLAV', 'Autoclave Melag Vacuklav 44B+',           'autoclave',  'cái',   88000000, 5,  65000000, 24, 'NCC-DE-001',  6, 3),
    ('AUTO-TUTTNAUER-22L',  'Autoclave Tuttnauer 22L Class B',         'autoclave',  'cái',   32000000, 5,  23500000, 12, 'NCC-VN-001', 15, 5),

    -- Tay khoan
    ('KHOAN-NSK-Z95L',    'Tay khoan cao tốc NSK Ti-Max Z95L',         'tay_khoan',  'cái',   18500000, 5,  13800000, 12, 'NCC-JP-001', 25, 10),
    ('KHOAN-BIENAIR-HIGH','Tay khoan Bien Air Tornado',                'tay_khoan',  'cái',   22000000, 5,  16500000, 12, 'NCC-DE-002', 15,  8),

    -- Vật tư tiêu hao (VAT 5% do đa số y tế theo TT 43/2021)
    ('VT-GANG-TAY-M',     'Găng tay khám bệnh y tế size M (hộp 100)',  'vat_tu',     'hộp',      95000, 5,     72000,  0, 'NCC-VN-001', 500, 200),
    ('VT-KHAU-TRANG',     'Khẩu trang y tế 4 lớp (hộp 50)',            'vat_tu',     'hộp',      55000, 5,     42000,  0, 'NCC-VN-001', 800, 300),
    ('VT-KIM-GAY-TE',     'Kim gây tê nha khoa 30G (hộp 100)',         'vat_tu',     'hộp',     180000, 5,    135000,  0, 'NCC-VN-001', 200, 100),
    ('VT-COMPOSITE',      'Composite trám răng 3M Filtek Z350 (tuýp)', 'vat_tu',     'tuýp',    850000, 5,    640000,  0, 'NCC-VN-002', 100,  50),
    ('VT-VAT-LIEU-CO',    'Vật liệu cắm ghép implant Straumann',       'vat_tu',     'bộ',    12500000, 5,   9400000,  0, 'NCC-DE-002',  30,  10),

    -- Dịch vụ (VAT 10%)
    ('DV-LAP-DAT',        'Dịch vụ lắp đặt + hiệu chỉnh thiết bị',      'dich_vu',    'lần',    5000000,10,   3200000,  0, NULL,           0,   0),
    ('DV-DAO-TAO',        'Dịch vụ đào tạo sử dụng thiết bị (1 ngày)',  'dich_vu',    'ngày',   8000000,10,   4800000,  0, NULL,           0,   0),
    ('DV-BAO-TRI-YR',     'Gói bảo trì bảo dưỡng 12 tháng',             'dich_vu',    'gói',   15000000,10,   8500000,  0, NULL,           0,   0)
) AS p(sku, name, category, unit, unit_price, vat_rate, cost_price, warranty_months, supplier_code, stock_on_hand, reorder_point)
ON CONFLICT (tenant_id, sku) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 2. sim_sales_orders + lines
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sim_sales_orders (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    order_number    TEXT NOT NULL,               -- SO-2026-0087
    order_date      DATE NOT NULL,
    customer_id     UUID NOT NULL REFERENCES sim_customers(id),
    contract_id     UUID,                         -- optional link to sim_contracts
    salesperson_code TEXT,                        -- link to sim_employees.employee_code
    status          TEXT DEFAULT 'confirmed',     -- draft / confirmed / delivering / delivered / cancelled / invoiced
    expected_delivery_date DATE,
    payment_terms_days INT DEFAULT 30,
    subtotal        NUMERIC(18,2) NOT NULL,
    vat_amount      NUMERIC(18,2) NOT NULL,
    total_amount    NUMERIC(18,2) NOT NULL,
    invoiced_ref    TEXT,                          -- số hoá đơn đã xuất
    delivery_note_ref TEXT,                        -- số PXK đã xuất
    notes           TEXT,
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, order_number)
);

CREATE TABLE IF NOT EXISTS sim_sales_order_lines (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    order_id      UUID NOT NULL REFERENCES sim_sales_orders(id) ON DELETE CASCADE,
    ordinal       INT NOT NULL,
    product_id    UUID REFERENCES sim_products(id),
    sku           TEXT NOT NULL,                   -- snapshot at order time
    description   TEXT NOT NULL,
    unit          TEXT,
    quantity      NUMERIC(18,4) NOT NULL,
    unit_price    NUMERIC(18,2) NOT NULL,
    subtotal      NUMERIC(18,2) NOT NULL,
    vat_rate      NUMERIC(5,2) NOT NULL,
    vat_amount    NUMERIC(18,2) NOT NULL,
    total_amount  NUMERIC(18,2) NOT NULL,
    UNIQUE (order_id, ordinal)
);

CREATE INDEX IF NOT EXISTS idx_sim_so_customer ON sim_sales_orders(customer_id, order_date DESC);
CREATE INDEX IF NOT EXISTS idx_sim_so_invoiced ON sim_sales_orders(tenant_id, invoiced_ref);

-- Sales orders that match existing sim_ar_ledger invoices so 3-way match works
INSERT INTO sim_sales_orders (tenant_id, order_number, order_date, customer_id, salesperson_code, status, expected_delivery_date, payment_terms_days, subtotal, vat_amount, total_amount, invoiced_ref, delivery_note_ref, notes)
SELECT (SELECT id FROM tenants WHERE code='vietdang'),
       o.order_number, o.order_date::date, c.id, o.sp, o.status, o.expected::date, o.terms,
       o.subtotal, o.vat, o.total, o.invoiced_ref, o.dn, o.notes
FROM sim_customers c
JOIN LATERAL (VALUES
    -- Nha Xinh: SO cho ghế KaVo E80 → invoice 00001101 (350M + 17.5M VAT = 367.5M)
    ('PK-001', 'SO-2026-0087', '2026-07-05', 'NV-0007', 'invoiced', '2026-07-12', 30, 350000000, 17500000, 367500000, '00001101', 'DN-2026-0091', 'Order confirmed via email từ Bs. Trần Chủ Nha Xinh'),
    ('PK-001', 'SO-2026-0098', '2026-07-28', 'NV-0009', 'invoiced', '2026-08-01', 30,   3750000,   187500,   3937500, '00001167', 'DN-2026-0103', 'Vật tư tiêu hao tháng'),

    -- Kim Cương: SO 00001155
    ('PK-002', 'SO-2026-0095', '2026-07-25', 'NV-0007', 'invoiced', '2026-07-28', 30,  65000000,  3250000,  68250000, '00001155', 'DN-2026-0099', 'Máy X-quang Sirona 2D'),

    -- Việt Đức: SO
    ('PK-004', 'SO-2026-0110', '2026-08-01', 'NV-0009', 'invoiced', '2026-08-05', 30,  32000000,  1600000,  33600000, '00001178', 'DN-2026-0115', 'Autoclave Tuttnauer 22L'),

    -- An Nhiên: SO 00001080 (quá hạn)
    ('PK-006', 'SO-2026-0065', '2026-06-15', 'NV-0007', 'invoiced', '2026-06-20', 30,  45500000,  2275000,  47775000, '00001080', 'DN-2026-0068', 'Ghế phổ thông VN — khách khiếu nại chậm tt'),

    -- Ngọc Anh HN: SO 00001094 — máy scan iTero
    ('PK-007', 'SO-2026-0082', '2026-06-28', 'NV-0008', 'invoiced', '2026-07-05', 30, 156000000,  7800000, 163800000, '00001094', 'DN-2026-0085', 'Scan iTero 5D + đào tạo'),

    -- BV RHM: gói thầu lớn — nhiều SO cho 1 contract
    ('BV-001', 'SO-2026-0060', '2026-06-10', 'NV-0007', 'invoiced', '2026-06-15', 60, 1250000000, 62500000, 1312500000, '00001068', 'DN-2026-0063', 'Gói thầu 5 ghế KaVo E70 + 2 máy X-quang'),

    -- BV Nhi Đồng
    ('BV-002', 'SO-2026-0075', '2026-06-25', 'NV-0007', 'invoiced', '2026-07-01', 90,  875000000, 43750000,  918750000, '00001085', 'DN-2026-0078', 'Gói thầu ghế + vật tư 6 tháng'),

    -- DL Miền Bắc:
    ('DL-001', 'SO-2026-0090', '2026-07-15', 'NV-0008', 'invoiced', '2026-07-20', 45,  423000000, 21150000,  444150000, '00001112', 'DN-2026-0094', 'Đơn hàng Q3 phân phối miền Bắc'),

    -- Sài Gòn Dental
    ('DL-002', 'SO-2026-0108', '2026-07-28', 'NV-0007', 'invoiced', '2026-08-01', 45,   98000000,  4900000,  102900000, '00001165', 'DN-2026-0112', 'Bổ sung vật tư'),

    -- SO chưa xuất HĐ (dùng để demo case "invoice khớp SO chưa xuất")
    ('PK-003', 'SO-2026-0125', '2026-08-08', 'NV-0009', 'confirmed', '2026-08-15', 30,  32000000,  1600000,   33600000, NULL, NULL, 'Đang chờ giao hàng'),
    ('PK-005', 'SO-2026-0130', '2026-08-10', 'NV-0009', 'delivering', '2026-08-13', 30,  18500000,   925000,   19425000, NULL, 'DN-2026-0128', 'Đã xuất kho, chưa xuất HĐ')

) AS o(customer_code, order_number, order_date, sp, status, expected, terms, subtotal, vat, total, invoiced_ref, dn, notes)
ON c.customer_code = o.customer_code
WHERE c.tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ON CONFLICT (tenant_id, order_number) DO NOTHING;

-- Order lines (chỉ cho 3 SO quan trọng nhất — demo 3-way match)
INSERT INTO sim_sales_order_lines (order_id, ordinal, product_id, sku, description, unit, quantity, unit_price, subtotal, vat_rate, vat_amount, total_amount)
SELECT so.id, l.ordinal, p.id, p.sku, p.name, p.unit, l.qty, l.up, l.sub, l.vr, l.va, l.tot
FROM sim_sales_orders so
JOIN sim_products p ON p.tenant_id = so.tenant_id
JOIN LATERAL (VALUES
    -- SO-2026-0087 Nha Xinh 1 ghế KaVo E80 = 350M
    ('SO-2026-0087', 1, 'GHE-KAVO-E80',   1, 350000000, 350000000, 5, 17500000, 367500000),

    -- SO-2026-0060 BV RHM gói thầu: 5 ghế E70 + 2 XRAY 2D
    ('SO-2026-0060', 1, 'GHE-KAVO-E70',   5, 168000000, 840000000, 5, 42000000, 882000000),  -- giá gói thầu chiết khấu
    ('SO-2026-0060', 2, 'XRAY-KAVO-2D',   2, 205000000, 410000000, 5, 20500000, 430500000),

    -- SO-2026-0082 Ngọc Anh HN: 1 iTero Scan + đào tạo
    ('SO-2026-0082', 1, 'SCAN-ITERO-5D',  1, 148000000, 148000000, 5, 7400000, 155400000),
    ('SO-2026-0082', 2, 'DV-DAO-TAO',     1,   8000000,   8000000,10,  800000,   8800000),

    -- SO-2026-0090 DL Miền Bắc: đơn hàng vt hỗn hợp
    ('SO-2026-0090', 1, 'GHE-VN-STD',     3,  90000000, 270000000, 5, 13500000, 283500000),
    ('SO-2026-0090', 2, 'AUTO-TUTTNAUER-22L', 3, 30000000, 90000000, 5, 4500000, 94500000),
    ('SO-2026-0090', 3, 'VT-VAT-LIEU-CO', 5,  12500000,  63000000, 5, 3150000,  66150000)   -- gross-up khớp SO total
) AS l(order_number, ordinal, sku, qty, up, sub, vr, va, tot)
ON so.order_number = l.order_number AND p.sku = l.sku
WHERE so.tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ON CONFLICT (order_id, ordinal) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 3. sim_delivery_notes — phiếu xuất kho
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sim_delivery_notes (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    dn_number      TEXT NOT NULL,                  -- DN-2026-0091
    dn_date        DATE NOT NULL,
    customer_id    UUID NOT NULL REFERENCES sim_customers(id),
    sales_order_id UUID REFERENCES sim_sales_orders(id),
    warehouse_from TEXT DEFAULT 'Kho HCM',
    delivered_by   TEXT,
    received_by    TEXT,
    delivery_address TEXT,
    status         TEXT DEFAULT 'delivered',       -- pending / in_transit / delivered
    delivered_at   TIMESTAMPTZ,
    notes          TEXT,
    created_at     TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, dn_number)
);

INSERT INTO sim_delivery_notes (tenant_id, dn_number, dn_date, customer_id, sales_order_id, warehouse_from, delivered_by, received_by, delivery_address, status, delivered_at, notes)
SELECT so.tenant_id, d.dn_number, d.dn_date::date, so.customer_id, so.id, d.wh, d.dby, d.rby, d.addr, d.status, d.dat::timestamptz, d.notes
FROM sim_sales_orders so
JOIN LATERAL (VALUES
    ('SO-2026-0087', 'DN-2026-0091', '2026-07-10', 'Kho HCM', 'Ngô Văn Long', 'Trần Ngọc Bs.',    '123 Lê Lợi, Q1, TP.HCM',            'delivered', '2026-07-10 14:30', 'Giao + lắp đặt trong ngày'),
    ('SO-2026-0060', 'DN-2026-0063', '2026-06-14', 'Kho HCM', 'Ngô Văn Long', 'ĐD BV RHM',        '201 Phạm Ngũ Lão, Q1, TP.HCM',      'delivered', '2026-06-14 09:00', 'Gói thầu — 3 chuyến giao'),
    ('SO-2026-0082', 'DN-2026-0085', '2026-07-04', 'Kho HN',  'Kỹ thuật HN',   'Ngọc Anh Bs.',     '25 Bà Triệu, Hoàn Kiếm, Hà Nội',    'delivered', '2026-07-04 10:30', 'Có đào tạo kèm'),
    ('SO-2026-0090', 'DN-2026-0094', '2026-07-18', 'Kho HCM', 'Ngô Văn Long', 'ĐD DL Miền Bắc',   '68 Ngô Quyền, Hoàn Kiếm, Hà Nội',   'delivered', '2026-07-18 15:00', 'Vận chuyển Bắc'),
    ('SO-2026-0065', 'DN-2026-0068', '2026-06-19', 'Kho HCM', 'Bùi Minh Khoa', 'An Nhiên Bs.',     '234 Nguyễn Thái Học, Q1, HCM',      'delivered', '2026-06-19 11:00', NULL),
    ('SO-2026-0095', 'DN-2026-0099', '2026-07-27', 'Kho HCM', 'Ngô Văn Long', 'Kim Cương Bs.',    '45 Nguyễn Huệ, Q1, TP.HCM',         'delivered', '2026-07-27 14:00', NULL),
    ('SO-2026-0075', 'DN-2026-0078', '2026-06-30', 'Kho HCM', 'Ngô Văn Long', 'ĐD BV Nhi Đồng',   '15 Võ Trần Chí, BC, TP.HCM',        'delivered', '2026-06-30 08:30', 'Chia 4 chuyến'),
    ('SO-2026-0098', 'DN-2026-0103', '2026-07-31', 'Kho HCM', 'Bùi Minh Khoa', 'Nha Xinh Bs.',     '123 Lê Lợi, Q1, TP.HCM',            'delivered', '2026-07-31 16:00', NULL),
    ('SO-2026-0108', 'DN-2026-0112', '2026-07-31', 'Kho HCM', 'Bùi Minh Khoa', 'ĐD SG Dental',     '125 Nam Kỳ Khởi Nghĩa, Q3, HCM',    'delivered', '2026-07-31 13:00', NULL),
    ('SO-2026-0110', 'DN-2026-0115', '2026-08-04', 'Kho HCM', 'Ngô Văn Long', 'Việt Đức Bs.',     '156 CMT8, Q3, TP.HCM',              'delivered', '2026-08-04 09:00', NULL),
    ('SO-2026-0130', 'DN-2026-0128', '2026-08-11', 'Kho HCM', 'Bùi Minh Khoa', 'Đông Á Bs.',       '99 Điện Biên Phủ, Q3, TP.HCM',      'in_transit', NULL,                'Đang giao — chưa xuất HĐ')
) AS d(order_number, dn_number, dn_date, wh, dby, rby, addr, status, dat, notes)
ON so.order_number = d.order_number
WHERE so.tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ON CONFLICT (tenant_id, dn_number) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 4. sim_contracts + sim_contract_milestones
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sim_contracts (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    contract_number TEXT NOT NULL,               -- HD-2026-042
    contract_date  DATE NOT NULL,
    customer_id    UUID REFERENCES sim_customers(id),   -- outbound contract
    supplier_code  TEXT,                          -- inbound contract (mua từ NCC)
    contract_type  TEXT NOT NULL,                 -- sales / purchase / distribution / maintenance
    title          TEXT NOT NULL,
    contract_value NUMERIC(18,2) NOT NULL,
    currency       TEXT DEFAULT 'VND',
    valid_from     DATE NOT NULL,
    valid_to       DATE,
    payment_terms_days INT DEFAULT 30,
    revenue_recognition TEXT DEFAULT 'delivery',  -- delivery / poc (percentage of completion) / milestone
    vat_rate       NUMERIC(5,2) DEFAULT 5,
    warranty_period_months INT DEFAULT 12,
    penalty_late_pct_per_day NUMERIC(5,4) DEFAULT 0.05,
    status         TEXT DEFAULT 'active',         -- draft / active / expired / terminated
    signed_by_party_a TEXT,
    signed_by_party_b TEXT,
    notes          TEXT,
    extras         JSONB DEFAULT '{}'::jsonb,
    created_at     TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, contract_number)
);

CREATE TABLE IF NOT EXISTS sim_contract_milestones (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    contract_id    UUID NOT NULL REFERENCES sim_contracts(id) ON DELETE CASCADE,
    milestone_no   INT NOT NULL,
    description    TEXT NOT NULL,                 -- "Ký hợp đồng" / "Giao hàng 50%" / "Nghiệm thu"
    percentage     NUMERIC(5,2) NOT NULL,         -- % giá trị HĐ
    amount         NUMERIC(18,2) NOT NULL,
    expected_date  DATE NOT NULL,
    actual_date    DATE,
    status         TEXT DEFAULT 'pending',        -- pending / invoiced / paid / delayed
    invoice_ref    TEXT,                          -- số HĐ đã xuất tương ứng
    UNIQUE (contract_id, milestone_no)
);

INSERT INTO sim_contracts (tenant_id, contract_number, contract_date, customer_id, contract_type, title, contract_value, valid_from, valid_to, payment_terms_days, revenue_recognition, vat_rate, warranty_period_months, status, signed_by_party_a, signed_by_party_b, notes)
SELECT (SELECT id FROM tenants WHERE code='vietdang'),
       c.contract_number, c.contract_date::date, cust.id, c.ctype, c.title, c.value,
       c.valid_from::date, c.valid_to::date, c.terms, c.recognition, c.vat, c.warranty,
       c.status, c.signed_a, c.signed_b, c.notes
FROM sim_customers cust
JOIN LATERAL (VALUES
    -- BV RHM: gói thầu 3 giai đoạn (trùng SO-2026-0060 nhưng là 1 phần của contract lớn hơn)
    ('BV-001', 'HD-2026-042', '2026-05-15', 'sales', 'Cung cấp thiết bị nha khoa cho BV RHM TW HCM (đợt 1)',
        2500000000, '2026-05-15', '2026-12-31', 60, 'milestone', 5, 24,
        'active', 'Nguyễn Thanh Việt (TGĐ)', 'PGS.TS Bs. Nguyễn Văn Trung (GĐ BV)',
        'Gói thầu 2.5 tỷ chia 3 giai đoạn: 5 ghế + 2 XRAY (đợt 1), 3 máy Scan (đợt 2), gói VT 12 tháng (đợt 3)'),

    -- Ngọc Anh HN: hợp đồng phân phối độc quyền
    ('PK-007', 'HD-2026-018', '2026-01-10', 'distribution', 'Phân phối độc quyền dòng iTero Scan 3D khu vực miền Bắc',
        1200000000, '2026-01-10', '2026-12-31', 30, 'delivery', 5, 12,
        'active', 'Trần Thị Đăng (PTGĐ)', 'BS. Ngọc Anh (Chủ PK)',
        'Đại lý miền Bắc — cam kết mua tối thiểu 8 máy/năm với ck 12%'),

    -- DL Miền Bắc: hợp đồng phân phối
    ('DL-001', 'HD-2026-005', '2026-01-05', 'distribution', 'Hợp đồng phân phối cấp 2 khu vực miền Bắc',
        3600000000, '2026-01-05', '2026-12-31', 45, 'delivery', 5, 12,
        'active', 'Nguyễn Thanh Việt (TGĐ)', 'Nguyễn Văn Hải (GĐ DL Miền Bắc)',
        'Cam kết doanh số 3.6 tỷ/năm; ck 15% cho đơn > 200tr'),

    -- BV Nhi Đồng: gói thầu vật tư 6 tháng
    ('BV-002', 'HD-2026-031', '2026-04-20', 'sales', 'Cung cấp thiết bị + vật tư tiêu hao 6 tháng cho BV Nhi Đồng',
        1500000000, '2026-04-20', '2026-10-20', 90, 'delivery', 5, 12,
        'active', 'Trần Thị Đăng (PTGĐ)', 'BS. CK Nguyễn Thị Lan (PGĐ BV Nhi Đồng)',
        'Gói thầu ghế + vật tư tiêu hao trong 6 tháng, giao theo lịch')

) AS c(customer_code, contract_number, contract_date, ctype, title, value, valid_from, valid_to, terms, recognition, vat, warranty, status, signed_a, signed_b, notes)
ON cust.customer_code = c.customer_code
WHERE cust.tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ON CONFLICT (tenant_id, contract_number) DO NOTHING;

-- Milestones for BV RHM contract
INSERT INTO sim_contract_milestones (contract_id, milestone_no, description, percentage, amount, expected_date, actual_date, status, invoice_ref)
SELECT c.id, m.no, m.descr, m.pct, m.amt, m.expected::date, m.actual::date, m.status, m.inv_ref
FROM sim_contracts c
JOIN LATERAL (VALUES
    ('HD-2026-042', 1, 'Đợt 1: 5 ghế KaVo E70 + 2 máy X-quang',      50.0, 1250000000, '2026-06-15', '2026-06-14', 'invoiced', '00001068'),
    ('HD-2026-042', 2, 'Đợt 2: 3 máy Scan iTero 5D + đào tạo',        30.0,  750000000, '2026-09-30', NULL,          'pending',  NULL),
    ('HD-2026-042', 3, 'Đợt 3: Gói VT tiêu hao + bảo trì 12 tháng',   20.0,  500000000, '2026-12-15', NULL,          'pending',  NULL),
    ('HD-2026-031', 1, 'Đợt 1: 3 ghế nha khoa + máy autoclave',        45.0,  675000000, '2026-05-30', '2026-05-28', 'paid',     '00001045'),
    ('HD-2026-031', 2, 'Đợt 2: Vật tư tiêu hao tháng 6-8',             30.0,  450000000, '2026-07-01', '2026-06-30', 'invoiced', '00001085'),
    ('HD-2026-031', 3, 'Đợt 3: Vật tư tiêu hao tháng 9-10',            25.0,  375000000, '2026-09-01', NULL,          'pending',  NULL)
) AS m(contract_number, no, descr, pct, amt, expected, actual, status, inv_ref)
ON c.contract_number = m.contract_number
WHERE c.tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ON CONFLICT (contract_id, milestone_no) DO NOTHING;

-- Link SO-2026-0060 → contract HD-2026-042
UPDATE sim_sales_orders so
SET contract_id = c.id
FROM sim_contracts c
WHERE c.contract_number = 'HD-2026-042'
  AND so.order_number = 'SO-2026-0060'
  AND so.tenant_id = c.tenant_id;

-- Link SO-2026-0075 (BV Nhi Đồng) → contract HD-2026-031 (milestone 2)
UPDATE sim_sales_orders so
SET contract_id = c.id
FROM sim_contracts c
WHERE c.contract_number = 'HD-2026-031'
  AND so.order_number = 'SO-2026-0075'
  AND so.tenant_id = c.tenant_id;

-- Link SO-2026-0090 (DL Miền Bắc) → contract HD-2026-005
UPDATE sim_sales_orders so
SET contract_id = c.id
FROM sim_contracts c
WHERE c.contract_number = 'HD-2026-005'
  AND so.order_number = 'SO-2026-0090'
  AND so.tenant_id = c.tenant_id;

-- Link SO-2026-0082 (Ngọc Anh HN) → contract HD-2026-018
UPDATE sim_sales_orders so
SET contract_id = c.id
FROM sim_contracts c
WHERE c.contract_number = 'HD-2026-018'
  AND so.order_number = 'SO-2026-0082'
  AND so.tenant_id = c.tenant_id;

-- ---------------------------------------------------------------------------
-- 5. sim_bank_accounts + sim_bank_transactions
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sim_bank_accounts (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    account_number TEXT NOT NULL,
    bank_name      TEXT NOT NULL,
    branch         TEXT,
    account_holder TEXT NOT NULL,
    currency       TEXT DEFAULT 'VND',
    account_type   TEXT DEFAULT 'checking',        -- checking / savings / usd
    current_balance NUMERIC(18,2) DEFAULT 0,
    gl_account_code TEXT NOT NULL,                 -- TK 11211 / 11212 / 11221...
    is_primary     BOOLEAN DEFAULT FALSE,
    created_at     TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, account_number)
);

CREATE TABLE IF NOT EXISTS sim_bank_transactions (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id     UUID NOT NULL REFERENCES sim_bank_accounts(id) ON DELETE CASCADE,
    tx_date        DATE NOT NULL,
    tx_ref         TEXT,                            -- reference/description
    description    TEXT NOT NULL,
    debit          NUMERIC(18,2) DEFAULT 0,         -- tiền ra
    credit         NUMERIC(18,2) DEFAULT 0,         -- tiền vào
    balance_after  NUMERIC(18,2),
    counterparty_name TEXT,
    counterparty_account TEXT,
    matched_document_type TEXT,                     -- invoice / expense / advance / bank_transfer
    matched_document_number TEXT,
    suggested_gl_debit  TEXT,
    suggested_gl_credit TEXT,
    reconciled     BOOLEAN DEFAULT FALSE,
    created_at     TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_sim_bank_tx_date ON sim_bank_transactions(account_id, tx_date DESC);

INSERT INTO sim_bank_accounts (tenant_id, account_number, bank_name, branch, account_holder, currency, account_type, current_balance, gl_account_code, is_primary)
SELECT (SELECT id FROM tenants WHERE code='vietdang'), a.*
FROM (VALUES
    ('0011000123456', 'Vietcombank',            'CN TP.HCM',       'CTCP THIẾT BỊ NHA KHOA VIỆT ĐĂNG', 'VND', 'checking',     5230000000, '11211', TRUE),
    ('1120100987654', 'BIDV',                    'CN Bình Thạnh',   'CTCP THIẾT BỊ NHA KHOA VIỆT ĐĂNG', 'VND', 'checking',     2150000000, '11212', FALSE),
    ('0011370456789', 'Vietcombank',            'CN TP.HCM',       'CTCP THIẾT BỊ NHA KHOA VIỆT ĐĂNG', 'USD', 'usd',              85000, '11221', FALSE),
    ('0011370456790', 'Vietcombank',            'CN TP.HCM',       'CTCP THIẾT BỊ NHA KHOA VIỆT ĐĂNG', 'EUR', 'usd',              42000, '11222', FALSE)
) AS a(account_number, bank_name, branch, account_holder, currency, account_type, current_balance, gl_account_code, is_primary)
ON CONFLICT (tenant_id, account_number) DO NOTHING;

INSERT INTO sim_bank_transactions (account_id, tx_date, tx_ref, description, debit, credit, balance_after, counterparty_name, counterparty_account, matched_document_type, matched_document_number, suggested_gl_debit, suggested_gl_credit, reconciled)
SELECT ba.id, t.tx_date::date, t.tx_ref, t.description, t.debit, t.credit, t.balance,
       t.cp_name, t.cp_acc, t.match_type, t.match_num, t.gl_dr, t.gl_cr, t.reconciled
FROM sim_bank_accounts ba
JOIN LATERAL (VALUES
    -- VCB VND — sao kê tháng 8
    ('0011000123456', '2026-08-01', 'IBFT202608010001', 'TT HD 00001101 tra 50% cua KH PK-001 Nha Xinh',        0, 183750000, 5150000000, 'CTY TNHH NHA XINH',              '0311000111222', 'receipt',       '00001101',    '11211', '131', FALSE),
    ('0011000123456', '2026-08-02', 'IBFT202608020015', 'TT HD 00001085 BV NHI DONG',                            0, 875000000, 6025000000, 'BENH VIEN NHI DONG TP',           '0311999001',    'receipt',       '00001085',    '11211', '131', FALSE),
    ('0011000123456', '2026-08-03', 'DEB202608030002', 'TT HD 00005678 NCC VN NCC-VN-001 vat tu y te',           45000000, 0, 5980000000, 'CTY TNHH VAT TU Y TE VIET NAM',   '0311777001',    'bank_transfer', 'UNC-08-001',  '331',   '11211', TRUE),
    ('0011000123456', '2026-08-05', 'IBFT202608050031', 'TT HD 00001178 PK-004 Viet Duc',                        0,  33600000, 6013600000, 'PHONG KHAM VIET DUC',             '0311234570',    'receipt',       '00001178',    '11211', '131', FALSE),
    ('0011000123456', '2026-08-05', 'IBFT202608050037', 'Hoan cong tac phi Tran Van Duc thang 7',                8000000, 0, 6005600000, 'TRAN VAN DUC',                    '0111333001',    'expense',       'HC-2026-07-089','141',  '11211', TRUE),
    ('0011000123456', '2026-08-08', 'DEB202608080012', 'TT dien nuoc thang 7',                                    3500000, 0, 6002100000, 'CTY DIEN LUC HCM',                '0300777123',    'payment',       'PC-08-005',   '6428',  '11211', TRUE),
    ('0011000123456', '2026-08-08', 'FEE202608080012', 'Phi chuyen tien lien ngan hang',                             22000, 0, 6002078000, NULL,                              NULL,            'other',         NULL,          '6425',  '11211', TRUE),
    ('0011000123456', '2026-08-10', 'IBFT202608100055', 'TT HD 00001155 PK-002 Kim Cuong',                        0,  68250000, 6070328000, 'PHONG KHAM KIM CUONG',            '0311234568',    'receipt',       '00001155',    '11211', '131', FALSE),
    ('0011000123456', '2026-08-10', 'IBFT202608100060', 'TT HD 00001112 DL Mien Bac 50%',                         0, 222075000, 6292403000, 'CTY TM DL MIEN BAC',              '0101888001',    'receipt',       '00001112',    '11211', '131', FALSE),
    ('0011000123456', '2026-08-11', 'ADV202608110001', 'Tam ung cong tac phi Vu Thi Huong T8',                   5000000, 0, 6287403000, 'VU THI HUONG',                    '0111333002',    'advance',       'TU-08-023',   '141',   '11211', TRUE),
    ('0011000123456', '2026-08-12', 'DEB202608120018', 'TT luong nhan vien thang 7 - VP',                       420000000, 0, 5867403000, 'BULK PAYROLL',                    NULL,            'payment',       'PL-2026-07',  '334',   '11211', TRUE),

    -- BIDV VND
    ('1120100987654', '2026-08-03', 'IBFT202608030099', 'TT HD 00001165 SG Dental',                              0, 102900000, 2252900000, 'CTP NHA KHOA SAI GON',            '0311888001',    'receipt',       '00001165',    '11212', '131', FALSE),
    ('1120100987654', '2026-08-07', 'DEB202608070022', 'TT tien thue thang 7 - hoa don GTGT dau ra',              75000000, 0, 2177900000, 'KHO BAC NHA NUOC HCM',            NULL,            'payment',       'PL-VAT-07',   '33311', '11212', TRUE),

    -- VCB USD
    ('0011370456789', '2026-08-04', 'SWIFT202608040001', 'TT L/C KaVo Dental GmbH inv KAV-2026-0812',              30000, 0,      55000, 'KAVO DENTAL GMBH',                'DE89370400440532013000', 'bank_transfer', 'UNC-USD-08-001','331', '11221', TRUE)

) AS t(account_number, tx_date, tx_ref, description, debit, credit, balance, cp_name, cp_acc, match_type, match_num, gl_dr, gl_cr, reconciled)
ON ba.account_number = t.account_number
WHERE ba.tenant_id = (SELECT id FROM tenants WHERE code='vietdang');

-- ---------------------------------------------------------------------------
-- 6. sim_purchase_orders + lines (AP side)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sim_purchase_orders (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    po_number      TEXT NOT NULL,                  -- PO-2026-0032
    po_date        DATE NOT NULL,
    supplier_code  TEXT NOT NULL,                  -- link sim_suppliers.supplier_code
    order_type     TEXT DEFAULT 'goods',           -- goods / service / import
    status         TEXT DEFAULT 'confirmed',       -- draft / confirmed / partial / received / cancelled
    expected_delivery DATE,
    currency       TEXT DEFAULT 'VND',
    exchange_rate  NUMERIC(18,4) DEFAULT 1,
    subtotal_foreign  NUMERIC(18,2),
    subtotal_vnd   NUMERIC(18,2) NOT NULL,
    import_duty_pct NUMERIC(5,2),
    vat_import_pct NUMERIC(5,2),
    incoterm       TEXT,                            -- CIF / FOB / EXW / DDP
    payment_terms  TEXT,
    goods_received_note TEXT,                       -- GRN number khi hàng về
    matched_invoice_number TEXT,
    notes          TEXT,
    created_at     TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, po_number)
);

INSERT INTO sim_purchase_orders (tenant_id, po_number, po_date, supplier_code, order_type, status, expected_delivery, currency, exchange_rate, subtotal_foreign, subtotal_vnd, import_duty_pct, vat_import_pct, incoterm, payment_terms, goods_received_note, matched_invoice_number, notes)
SELECT (SELECT id FROM tenants WHERE code='vietdang'),
       p.po_number, p.po_date::date, p.supplier_code, p.order_type, p.status, p.expected_delivery::date,
       p.currency, p.exchange_rate, p.subtotal_foreign, p.subtotal_vnd, p.import_duty_pct, p.vat_import_pct,
       p.incoterm, p.payment_terms, p.goods_received_note, p.matched_invoice_number, p.notes
FROM (VALUES
    ('PO-2026-0032', '2026-06-10', 'NCC-DE-001', 'import',  'received',  '2026-07-25', 'EUR', 27500.0000,   50000.0, 1375000000.0, 5.0, 5.0, 'CIF', 'T/T 30% + 70% B/L copy', 'GRN-2026-078', 'KAV-2026-0812', 'Ghế KaVo E80 x 1 - hàng đã về kho ngày 25/07'),
    ('PO-2026-0035', '2026-06-15', 'NCC-DE-002', 'import',  'received',  '2026-07-30', 'EUR', 27500.0000,   80000.0, 2200000000.0, 5.0, 5.0, 'CIF', 'LC 60 days',             'GRN-2026-081', 'SIR-2026-0567', 'Máy X-quang Sirona CBCT x 1'),
    ('PO-2026-0041', '2026-07-05', 'NCC-JP-001', 'import',  'partial',   '2026-08-30', 'JPY',   162.0000,10000000.0, 1620000000.0, 5.0, 5.0, 'FOB', 'T/T 100% before shipment', NULL,           NULL,             'Ghế Morita SP + tay khoan NSK - 60% giao, 40% giao 30/08'),
    ('PO-2026-0055', '2026-07-25', 'NCC-VN-001', 'goods',   'received',  '2026-07-28', 'VND',     1.0000,     NULL,     85000000.0, NULL, 10.0, 'DDP', 'Net 30',                'GRN-2026-095', '00005678',       'Vật tư y tế nội địa - đã có HĐ 00005678'),
    ('PO-2026-0062', '2026-08-01', 'NCC-VN-002', 'goods',   'confirmed', '2026-08-15', 'VND',     1.0000,     NULL,     45000000.0, NULL, 10.0, 'DDP', 'Net 30',                NULL,           NULL,             'Composite + vật liệu implant'),
    ('PO-2026-0065', '2026-08-05', 'NCC-VN-003', 'service', 'confirmed', '2026-08-10', 'VND',     1.0000,     NULL,      8500000.0, NULL, 10.0, 'DDP', 'Net 15',                NULL,           NULL,             'Vận chuyển lô hàng Hải Phòng - HCM')
) AS p(po_number, po_date, supplier_code, order_type, status, expected_delivery, currency, exchange_rate, subtotal_foreign, subtotal_vnd, import_duty_pct, vat_import_pct, incoterm, payment_terms, goods_received_note, matched_invoice_number, notes)
ON CONFLICT (tenant_id, po_number) DO NOTHING;

COMMIT;

-- ---------------------------------------------------------------------------
-- Verify
-- ---------------------------------------------------------------------------
\echo ''
\echo '=== Products (top 5 giá trị) ==='
SELECT sku, name, unit_price, stock_on_hand
FROM sim_products WHERE tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ORDER BY unit_price DESC LIMIT 5;

\echo ''
\echo '=== Sales Orders (all) ==='
SELECT so.order_number, so.order_date, c.customer_code, c.name AS customer,
       so.status, so.total_amount, so.invoiced_ref, so.delivery_note_ref,
       CASE WHEN so.contract_id IS NOT NULL THEN
           (SELECT contract_number FROM sim_contracts WHERE id=so.contract_id)
       ELSE '-' END AS contract
FROM sim_sales_orders so
JOIN sim_customers c ON c.id = so.customer_id
WHERE so.tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ORDER BY so.order_date DESC;

\echo ''
\echo '=== Contracts + milestones ==='
SELECT c.contract_number, c.title, c.contract_value,
       m.milestone_no, m.description AS milestone, m.percentage, m.amount, m.status, m.invoice_ref
FROM sim_contracts c
LEFT JOIN sim_contract_milestones m ON m.contract_id = c.id
WHERE c.tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ORDER BY c.contract_number, m.milestone_no NULLS FIRST;

\echo ''
\echo '=== Bank accounts ==='
SELECT account_number, bank_name, currency, current_balance, gl_account_code
FROM sim_bank_accounts WHERE tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ORDER BY is_primary DESC;

\echo ''
\echo '=== Bank txns (top 10 recent) ==='
SELECT ba.bank_name, t.tx_date, t.tx_ref, t.description,
       t.debit, t.credit, t.reconciled
FROM sim_bank_transactions t
JOIN sim_bank_accounts ba ON ba.id = t.account_id
WHERE ba.tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ORDER BY t.tx_date DESC LIMIT 10;

\echo ''
\echo '=== Purchase Orders ==='
SELECT po_number, po_date, supplier_code, order_type, status, currency, subtotal_vnd, matched_invoice_number
FROM sim_purchase_orders WHERE tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ORDER BY po_date DESC;
