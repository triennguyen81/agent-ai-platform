-- ============================================================================
-- Việt Đăng Simulator Seed — customer + employee + expense master data
--
-- Bootstraps mock master data so the demo can show:
-- 1. Invoice bán → auto lookup buyer_mst → khớp customer_code → suggest bút
--    toán N131 với đúng chi tiết công nợ.
-- 2. Expense claim của nhân viên → auto match employee_code + budget.
-- 3. Full A/R aging cho từng khách (giả lập một số phát sinh trong 60 ngày).
--
-- Simulator now = seed data. Later phase = MCP tool pulls from real HR/CRM/ERP.
-- All operations idempotent. Safe to re-run.
-- ============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- Tenant scope: vietdang
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    v_tenant_id UUID;
BEGIN
    SELECT id INTO v_tenant_id FROM tenants WHERE code = 'vietdang';
    IF v_tenant_id IS NULL THEN
        RAISE EXCEPTION 'Tenant "vietdang" not found — run vietdang_accounting_setup.sql first';
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 1. Master data tables (simulator only — will be replaced by MCP)
--
--    Design goal: keep schema simple + JSONB extras so future MCP adapter
--    can map real ERP fields without another migration.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS sim_customers (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    customer_code TEXT NOT NULL,           -- KH-001, PK-042, etc
    name          TEXT NOT NULL,
    mst           TEXT,                    -- MST — nullable cho khách cá nhân
    address       TEXT,
    phone         TEXT,
    email         TEXT,
    category      TEXT DEFAULT 'phong_kham', -- phong_kham / benh_vien / dai_ly / truong_hoc
    credit_limit  NUMERIC(18,2) DEFAULT 0,
    credit_terms_days INT DEFAULT 0,       -- 0 = trả ngay
    current_ar    NUMERIC(18,2) DEFAULT 0, -- công nợ hiện tại (mock)
    extras        JSONB DEFAULT '{}'::jsonb,
    created_at    TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, customer_code),
    UNIQUE (tenant_id, mst)
);

CREATE TABLE IF NOT EXISTS sim_suppliers (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    supplier_code TEXT NOT NULL,
    name          TEXT NOT NULL,
    mst           TEXT,
    country       TEXT DEFAULT 'VN',
    currency      TEXT DEFAULT 'VND',
    payment_terms TEXT,                    -- vd: "T/T 30% + 70% B/L copy"
    current_ap    NUMERIC(18,2) DEFAULT 0,
    extras        JSONB DEFAULT '{}'::jsonb,
    created_at    TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, supplier_code)
);

CREATE TABLE IF NOT EXISTS sim_employees (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    employee_code TEXT NOT NULL,
    full_name     TEXT NOT NULL,
    department    TEXT,
    position      TEXT,
    email         TEXT,
    phone         TEXT,
    monthly_expense_budget NUMERIC(18,2) DEFAULT 0,
    ytd_expense   NUMERIC(18,2) DEFAULT 0,
    outstanding_advance NUMERIC(18,2) DEFAULT 0,  -- tạm ứng chưa hoàn (TK 141)
    extras        JSONB DEFAULT '{}'::jsonb,
    created_at    TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, employee_code)
);

CREATE TABLE IF NOT EXISTS sim_expense_claims (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    document_number TEXT NOT NULL,
    document_date  DATE NOT NULL,
    employee_id    UUID NOT NULL REFERENCES sim_employees(id),
    purpose        TEXT,
    trip_from      DATE,
    trip_to        DATE,
    items          JSONB NOT NULL DEFAULT '[]'::jsonb,  -- [{category, description, amount, receipt}]
    total_claim    NUMERIC(18,2) NOT NULL,
    advance_taken  NUMERIC(18,2) DEFAULT 0,
    reimbursement_needed NUMERIC(18,2) GENERATED ALWAYS AS (total_claim - advance_taken) STORED,
    status         TEXT DEFAULT 'submitted',  -- submitted / approved / rejected / paid
    approved_by    TEXT,
    approved_at    TIMESTAMPTZ,
    paid_at        TIMESTAMPTZ,
    journal_entry_id UUID,
    created_at     TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, document_number)
);

CREATE TABLE IF NOT EXISTS sim_ar_ledger (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    customer_id     UUID NOT NULL REFERENCES sim_customers(id),
    document_date   DATE NOT NULL,
    document_number TEXT NOT NULL,        -- Số HĐ / phiếu thu
    document_type   TEXT NOT NULL,        -- invoice / receipt / adjustment
    debit           NUMERIC(18,2) DEFAULT 0,  -- ghi tăng công nợ (bán chịu)
    credit          NUMERIC(18,2) DEFAULT 0,  -- ghi giảm công nợ (thu tiền)
    balance_after   NUMERIC(18,2),
    due_date        DATE,
    -- days_overdue computed via view/query — Postgres won't let us store
    -- a generated column that references CURRENT_DATE (not immutable).
    reference_note  TEXT,
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_sim_customers_mst ON sim_customers(mst) WHERE mst IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_sim_employees_code ON sim_employees(employee_code);
CREATE INDEX IF NOT EXISTS idx_sim_ar_customer_date ON sim_ar_ledger(customer_id, document_date DESC);
CREATE INDEX IF NOT EXISTS idx_sim_expense_status ON sim_expense_claims(tenant_id, status, document_date DESC);

-- ---------------------------------------------------------------------------
-- 2. Seed customers — 8 phòng khám + 2 bệnh viện + 2 đại lý
-- ---------------------------------------------------------------------------
INSERT INTO sim_customers (tenant_id, customer_code, name, mst, address, phone, email, category, credit_limit, credit_terms_days, current_ar)
SELECT (SELECT id FROM tenants WHERE code='vietdang'), c.*
FROM (VALUES
    ('PK-001', 'Phòng khám Nha khoa Nha Xinh',            '0311234567', '123 Lê Lợi, Q1, TP.HCM',          '028-3822-1111', 'ketoan@nhaxinh.vn',     'phong_kham', 500000000,  30,  187500000),
    ('PK-002', 'Phòng khám Nha khoa Kim Cương',            '0311234568', '45 Nguyễn Huệ, Q1, TP.HCM',        '028-3822-2222', 'ketoan@kimcuong.vn',    'phong_kham', 500000000,  30,   65000000),
    ('PK-003', 'Phòng khám Nha khoa Sài Gòn Smile',        '0311234569', '78 Trần Hưng Đạo, Q5, TP.HCM',    '028-3822-3333', 'admin@sgsmile.vn',      'phong_kham', 500000000,  30,        0),
    ('PK-004', 'Phòng khám Nha khoa Việt Đức',             '0311234570', '156 CMT8, Q3, TP.HCM',            '028-3822-4444', 'ketoan@vietduc.vn',     'phong_kham', 500000000,  30,   32000000),
    ('PK-005', 'Phòng khám Nha khoa Đông Á',               '0311234571', '99 Điện Biên Phủ, Q3, TP.HCM',    '028-3822-5555', 'ketoan@dongadental.vn', 'phong_kham', 300000000,  30,        0),
    ('PK-006', 'Phòng khám Nha khoa An Nhiên',             '0311234572', '234 Nguyễn Thái Học, Q1, HCM',    '028-3822-6666', 'ketoan@annhien.vn',     'phong_kham', 200000000,  30,   45500000),
    ('PK-007', 'Phòng khám Nha khoa Ngọc Anh Hà Nội',      '0100234567', '25 Bà Triệu, Hoàn Kiếm, Hà Nội',   '024-3822-7777', 'ketoan@ngocanh.vn',     'phong_kham', 400000000,  30,  156000000),
    ('PK-008', 'Phòng khám Nha khoa Star Đà Nẵng',         '0400234567', '88 Lê Duẩn, Hải Châu, Đà Nẵng',    '0236-3822-888', 'ketoan@stardn.vn',      'phong_kham', 300000000,  30,        0),
    ('BV-001', 'Bệnh viện Răng Hàm Mặt TW TP.HCM',         '0311999001', '201 Phạm Ngũ Lão, Q1, TP.HCM',    '028-3830-1111', 'ketoan@bvrhm.vn',       'benh_vien',  2000000000, 60, 1250000000),
    ('BV-002', 'Bệnh viện Nhi Đồng Thành Phố',             '0311999002', '15 Võ Trần Chí, BC, TP.HCM',      '028-3830-2222', 'ketoan@bvnd.vn',        'benh_vien',  1500000000, 90,  875000000),
    ('DL-001', 'CTCP TM Thiết bị Nha khoa Miền Bắc',       '0101888001', '68 Ngô Quyền, Hoàn Kiếm, Hà Nội',  '024-3900-1111', 'purchase@mienbac.vn',   'dai_ly',     1000000000, 45,  423000000),
    ('DL-002', 'CTCP Nha khoa Sài Gòn',                    '0311888001', '125 Nam Kỳ Khởi Nghĩa, Q3, HCM',  '028-3900-2222', 'purchase@sgdental.vn',  'dai_ly',     1000000000, 45,   98000000)
) AS c(customer_code, name, mst, address, phone, email, category, credit_limit, credit_terms_days, current_ar)
ON CONFLICT (tenant_id, customer_code) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 3. Seed suppliers — 3 NCC nước ngoài + 3 NCC nội địa
-- ---------------------------------------------------------------------------
INSERT INTO sim_suppliers (tenant_id, supplier_code, name, mst, country, currency, payment_terms, current_ap)
SELECT (SELECT id FROM tenants WHERE code='vietdang'), s.*
FROM (VALUES
    ('NCC-DE-001', 'KaVo Dental GmbH',                     NULL,         'DE', 'EUR', 'T/T 30% deposit + 70% before shipment', 750000000),
    ('NCC-DE-002', 'Sirona Dental Systems',                NULL,         'DE', 'EUR', 'LC 60 days',                            420000000),
    ('NCC-JP-001', 'Morita Corporation',                   NULL,         'JP', 'JPY', 'T/T 100% before shipment',                     0),
    ('NCC-VN-001', 'Công ty TNHH Vật tư Y tế Việt Nam',    '0311777001', 'VN', 'VND', 'Net 30',                                85000000),
    ('NCC-VN-002', 'CTCP Xuất nhập khẩu Hải Phòng',        '0201777002', 'VN', 'VND', 'Net 30',                                12500000),
    ('NCC-VN-003', 'Công ty Vận chuyển Cargo VN',          '0311777003', 'VN', 'VND', 'Net 15',                                 6800000)
) AS s(supplier_code, name, mst, country, currency, payment_terms, current_ap)
ON CONFLICT (tenant_id, supplier_code) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 4. Seed employees — 12 nhân viên các phòng ban
-- ---------------------------------------------------------------------------
INSERT INTO sim_employees (tenant_id, employee_code, full_name, department, position, email, phone, monthly_expense_budget, ytd_expense, outstanding_advance)
SELECT (SELECT id FROM tenants WHERE code='vietdang'), e.*
FROM (VALUES
    ('NV-0001', 'Nguyễn Thanh Việt',    'Ban giám đốc',      'Tổng giám đốc',        'ceo@vietdang.vn',        '0903-111-001', 50000000, 187000000,        0),
    ('NV-0002', 'Trần Thị Đăng',        'Ban giám đốc',      'Phó tổng giám đốc',    'cfo@vietdang.vn',        '0903-111-002', 30000000, 112000000,        0),
    ('NV-0003', 'Lê Minh Kế',           'Kế toán',           'Kế toán trưởng',       'ketoantruong@vietdang.vn', '0903-222-001', 10000000,  32000000,        0),
    ('NV-0004', 'Phạm Thu Toán',        'Kế toán',           'Kế toán tổng hợp',     'ketoan1@vietdang.vn',    '0903-222-002',  5000000,  18000000,        0),
    ('NV-0005', 'Đỗ Văn Nợ',            'Kế toán',           'Kế toán công nợ',      'ketoan2@vietdang.vn',    '0903-222-003',  5000000,  14000000,        0),
    ('NV-0006', 'Hoàng Thị Kho',        'Kế toán',           'Kế toán kho',          'ketoan3@vietdang.vn',    '0903-222-004',  5000000,   9000000,        0),
    ('NV-0007', 'Trần Văn Đức',         'Kinh doanh',        'Trưởng phòng KD miền Nam', 'kd.mn@vietdang.vn',      '0903-333-001', 25000000, 143000000,  5000000),
    ('NV-0008', 'Vũ Thị Hương',         'Kinh doanh',        'Trưởng phòng KD miền Bắc', 'kd.mb@vietdang.vn',      '0903-333-002', 25000000, 128000000,  3500000),
    ('NV-0009', 'Bùi Minh Khoa',        'Kinh doanh',        'Nhân viên KD',         'kd1@vietdang.vn',        '0903-333-003', 15000000,  67000000,  1200000),
    ('NV-0010', 'Ngô Văn Long',         'Kỹ thuật',          'Trưởng phòng KT',      'kt.truongphong@vietdang.vn', '0903-444-001', 10000000,  45000000,        0),
    ('NV-0011', 'Đinh Thị Mai',         'Marketing',         'Trưởng phòng MKT',     'mkt@vietdang.vn',        '0903-555-001', 20000000,  89000000,        0),
    ('NV-0012', 'Lâm Văn Bảo',          'Nhân sự',           'Trưởng phòng HR',      'hr@vietdang.vn',         '0903-666-001',  5000000,  17000000,        0)
) AS e(employee_code, full_name, department, position, email, phone, monthly_expense_budget, ytd_expense, outstanding_advance)
ON CONFLICT (tenant_id, employee_code) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 5. Seed AR ledger — mock giao dịch công nợ trong 60 ngày
-- ---------------------------------------------------------------------------
INSERT INTO sim_ar_ledger (tenant_id, customer_id, document_date, document_number, document_type, debit, credit, balance_after, due_date, reference_note)
SELECT (SELECT id FROM tenants WHERE code='vietdang'),
       c.id, d.document_date::date, d.document_number, d.document_type, d.debit, d.credit, d.balance_after, d.due_date::date, d.reference_note
FROM sim_customers c
JOIN LATERAL (VALUES
    -- Nha Xinh: bán 1 ghế 350tr ngày 12/07 (đã đến hạn 11/08, quá hạn), thu 50% ngày 12/07
    ('PK-001', '2026-07-12', '00001101', 'invoice',  367500000, 0,         367500000, '2026-08-11', 'Bán ghế KaVo E80'),
    ('PK-001', '2026-07-12', 'PT-08023', 'receipt',  0,         183750000, 183750000, NULL,          'Thu 50% CK'),
    ('PK-001', '2026-08-01', '00001167', 'invoice',    3750000, 0,         187500000, '2026-08-31', 'Vật tư tiêu hao'),

    -- Kim Cương: 2 HĐ mới trong tháng, chưa đến hạn
    ('PK-002', '2026-07-28', '00001155', 'invoice',  65000000, 0,          65000000, '2026-08-27', 'Máy X-quang Sirona'),

    -- Việt Đức: bán 32tr ngày 05/08
    ('PK-004', '2026-08-05', '00001178', 'invoice',  32000000, 0,          32000000, '2026-09-04', 'Autoclave 22L'),

    -- An Nhiên: 45.5tr quá hạn (bán 20/06)
    ('PK-006', '2026-06-20', '00001080', 'invoice',  45500000, 0,          45500000, '2026-07-20', 'Ghế nha phổ thông'),

    -- Ngọc Anh HN: HĐ lớn 156tr quá hạn nhẹ
    ('PK-007', '2026-07-05', '00001094', 'invoice', 156000000, 0,         156000000, '2026-08-04', 'Máy Scan 3D iTero'),

    -- BV RHM: HĐ 1.25 tỷ quá hạn 30 ngày (điển hình BV công thanh toán chậm)
    ('BV-001', '2026-06-15', '00001068', 'invoice', 1250000000, 0,       1250000000, '2026-08-14', 'Gói thầu 5 ghế + 2 máy X-quang'),

    -- BV Nhi Đồng: HĐ 875tr, Net90 chưa đến hạn
    ('BV-002', '2026-07-01', '00001085', 'invoice',  875000000, 0,        875000000, '2026-09-29', 'Gói thầu ghế + vật tư'),

    -- DL Miền Bắc: 423tr Net45 chưa đến hạn
    ('DL-001', '2026-07-20', '00001112', 'invoice',  423000000, 0,        423000000, '2026-09-03', 'Đơn hàng Q3'),

    -- Sài Gòn Dental: 98tr Net45 chưa đến hạn
    ('DL-002', '2026-08-01', '00001165', 'invoice',   98000000, 0,         98000000, '2026-09-15', 'Đơn hàng bổ sung')
) AS d(customer_code, document_date, document_number, document_type, debit, credit, balance_after, due_date, reference_note)
ON c.customer_code = d.customer_code
WHERE c.tenant_id = (SELECT id FROM tenants WHERE code='vietdang');

-- ---------------------------------------------------------------------------
-- 6. Seed expense claims — 5 đề nghị hoàn ứng gần đây
-- ---------------------------------------------------------------------------
INSERT INTO sim_expense_claims (tenant_id, document_number, document_date, employee_id, purpose, trip_from, trip_to, items, total_claim, advance_taken, status, approved_by, approved_at)
SELECT (SELECT id FROM tenants WHERE code='vietdang'),
       e.document_number, e.document_date::date, emp.id, e.purpose, e.trip_from::date, e.trip_to::date,
       e.items::jsonb, e.total_claim, e.advance_taken, e.status, e.approved_by, e.approved_at::timestamptz
FROM sim_employees emp
JOIN LATERAL (VALUES
    ('NV-0007', 'HC-2026-08-023', '2026-08-05', 'Công tác Hà Nội - gặp KH PK-007 Ngọc Anh',           '2026-08-01', '2026-08-04',
        '[{"category":"airfare","description":"Vé máy bay SGN-HAN khứ hồi VietJet","amount":3500000,"receipt_attached":true},{"category":"hotel","description":"Khách sạn Melia HN 3 đêm","amount":5400000,"receipt_attached":true},{"category":"meal","description":"Ăn uống 4 ngày","amount":1800000,"receipt_attached":false},{"category":"taxi","description":"Đi lại nội thành","amount":650000,"receipt_attached":true}]',
        11350000, 5000000, 'approved', 'NV-0002 Trần Thị Đăng', '2026-08-06 09:30'),

    ('NV-0008', 'HC-2026-08-019', '2026-08-04', 'Trưng bày sản phẩm Hội chợ Y tế Hà Nội',              '2026-07-28', '2026-08-02',
        '[{"category":"airfare","description":"Vé HAN-SGN","amount":2800000,"receipt_attached":true},{"category":"hotel","description":"KS Fortuna HN 5 đêm","amount":8000000,"receipt_attached":true},{"category":"gift","description":"Quà tặng khách VIP tại hội chợ","amount":4500000,"receipt_attached":true},{"category":"other","description":"Chi phí thuê gian hàng","amount":15000000,"receipt_attached":true}]',
        30300000, 20000000, 'approved', 'NV-0001 Nguyễn Thanh Việt', '2026-08-05 14:00'),

    ('NV-0009', 'HC-2026-08-031', '2026-08-08', 'Đi khách phòng khám tỉnh Đồng Nai',                    '2026-08-06', '2026-08-07',
        '[{"category":"taxi","description":"Grab HCM - Biên Hoà khứ hồi","amount":950000,"receipt_attached":true},{"category":"meal","description":"Ăn trưa cùng khách","amount":1200000,"receipt_attached":true},{"category":"gift","description":"Quà mở đơn cho BS Nam PK","amount":800000,"receipt_attached":false}]',
        2950000, 0, 'submitted', NULL, NULL),

    ('NV-0011', 'HC-2026-08-042', '2026-08-10', 'Chi phí quảng cáo Facebook + Google Q3 tháng 8',       '2026-08-01', '2026-08-31',
        '[{"category":"other","description":"Facebook Ads campaign #Q3-Vietdang-Dental","amount":18000000,"receipt_attached":true},{"category":"other","description":"Google Ads brand keywords","amount":12000000,"receipt_attached":true},{"category":"other","description":"Chi phí thiết kế banner outsource","amount":3500000,"receipt_attached":true}]',
        33500000, 0, 'submitted', NULL, NULL),

    ('NV-0007', 'HC-2026-07-089', '2026-07-25', 'Công tác Đà Nẵng - triển khai máy Scan cho PK-008',    '2026-07-20', '2026-07-24',
        '[{"category":"airfare","description":"SGN-DAD khứ hồi","amount":3200000,"receipt_attached":true},{"category":"hotel","description":"KS Hải Âu 4 đêm","amount":5200000,"receipt_attached":true},{"category":"taxi","description":"Đi lại","amount":890000,"receipt_attached":true},{"category":"meal","description":"Ăn uống","amount":1500000,"receipt_attached":false}]',
        10790000, 8000000, 'paid', 'NV-0002 Trần Thị Đăng', '2026-07-26 10:00')
) AS e(employee_code, document_number, document_date, purpose, trip_from, trip_to, items, total_claim, advance_taken, status, approved_by, approved_at)
ON emp.employee_code = e.employee_code
WHERE emp.tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ON CONFLICT (tenant_id, document_number) DO NOTHING;

COMMIT;

-- ---------------------------------------------------------------------------
-- Verify
-- ---------------------------------------------------------------------------
\echo ''
\echo '=== Customers (top 5 công nợ cao nhất) ==='
SELECT customer_code, name, category, current_ar,
       (SELECT COUNT(*) FROM sim_ar_ledger l
        WHERE l.customer_id = c.id
          AND CURRENT_DATE > l.due_date
          AND l.credit = 0) AS overdue_count
FROM sim_customers c
WHERE tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ORDER BY current_ar DESC LIMIT 5;

\echo ''
\echo '=== Suppliers ==='
SELECT supplier_code, name, country, currency, current_ap
FROM sim_suppliers WHERE tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
ORDER BY current_ap DESC;

\echo ''
\echo '=== Employees (có tạm ứng chưa hoàn) ==='
SELECT employee_code, full_name, department, outstanding_advance, ytd_expense
FROM sim_employees
WHERE tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
  AND outstanding_advance > 0
ORDER BY outstanding_advance DESC;

\echo ''
\echo '=== Expense Claims theo status ==='
SELECT status, COUNT(*) AS count, SUM(reimbursement_needed) AS total_reimburse
FROM sim_expense_claims
WHERE tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
GROUP BY status
ORDER BY count DESC;

\echo ''
\echo '=== AR Aging (quá hạn cần chú ý) ==='
WITH aging AS (
    SELECT c.customer_code, c.name, l.debit,
           CASE WHEN CURRENT_DATE > l.due_date AND l.credit = 0
                THEN (CURRENT_DATE - l.due_date)
                ELSE 0
           END AS days_overdue
    FROM sim_customers c
    JOIN sim_ar_ledger l ON l.customer_id = c.id
    WHERE c.tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
)
SELECT customer_code, name,
       SUM(CASE WHEN days_overdue BETWEEN 1 AND 30 THEN debit ELSE 0 END) AS "1-30d",
       SUM(CASE WHEN days_overdue BETWEEN 31 AND 60 THEN debit ELSE 0 END) AS "31-60d",
       SUM(CASE WHEN days_overdue > 60 THEN debit ELSE 0 END) AS ">60d"
FROM aging
WHERE days_overdue > 0
GROUP BY customer_code, name
ORDER BY SUM(debit) DESC;

\echo ''
\echo '=== Overdue customers (top 5 quá hạn) ==='
SELECT c.customer_code, c.name,
       COUNT(*) FILTER (WHERE CURRENT_DATE > l.due_date AND l.credit = 0) AS overdue_docs
FROM sim_customers c
JOIN sim_ar_ledger l ON l.customer_id = c.id
WHERE c.tenant_id = (SELECT id FROM tenants WHERE code='vietdang')
GROUP BY c.customer_code, c.name
HAVING COUNT(*) FILTER (WHERE CURRENT_DATE > l.due_date AND l.credit = 0) > 0
ORDER BY overdue_docs DESC LIMIT 5;
