-- Load the AccAgent business sample-data pack (tests/fixtures/business_samples/)
-- into the CURRENT DEV tenant so it is visible in the browser.
--
-- Fictional data only. No production IDs. Does NOT create any tenant,
-- department, or user row — hard-wired to the tenant/department/user that
-- already exist in the persistent dev stack (aiq-postgres/aiq-gateway):
--   tenant     = test-123          74a79d3f-1662-4f2e-b67b-46ec1a6b403c
--   department = Default Department 0facef04-3e54-438b-8724-966dbbaaaa9a
--   user       = test@test.com (admin) 64aa15d2-f852-4a84-95fe-3390443300f2
--
-- Idempotent: safe to re-run. Uses ON CONFLICT where a real unique
-- constraint exists, WHERE NOT EXISTS guards where it doesn't (sim_ar_ledger,
-- department_expense_policies have no unique constraint in the live schema).
--
-- One deliberate deviation from tests/fixtures/business_samples/seed.sql:
-- CUS-001's mst is 0301234599 here instead of the fixture's 0301234567 —
-- this tenant already has a pre-existing customer (customer_code
-- "0301234567") occupying that exact mst, which would violate the real
-- UNIQUE(tenant_id, mst) constraint on sim_customers. Every other value
-- (id, customerCode, name, and all other customers/suppliers/employees)
-- is unchanged from the fixture pack. All fixture IDs were checked against
-- this tenant's existing rows first — no collisions found.

BEGIN;

-- ============================================================
-- 1. CUSTOMERS (sim_customers) — 9 rows
-- ============================================================
INSERT INTO sim_customers (id, tenant_id, department_id, customer_code, name, mst, address, phone, email, category, credit_limit, credit_terms_days, customer_type, representative, status, notes)
VALUES
  ('30000000-0000-4000-8000-000000000001', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'CUS-001', 'Công ty CP Kỹ thuật Bình Minh', '0301234599', '12 Nguyễn Huệ, Quận 1, TP.HCM', '028-3822-1234', 'ketoan@binhminh-tech.example.vn', 'cong_ty', 3000000000, 30, 'company', 'Nguyễn Văn Bình', 'active', 'Case: normal active + multiple bank accounts (TK1 Vietcombank 0071000123456, TK2 BIDV 21410000987654 — notes field only, no bank-account column exists). mst changed from fixture default 0301234567 to avoid colliding with this tenant''s pre-existing customer at that mst.'),
  ('30000000-0000-4000-8000-000000000002', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'CUS-002', 'Công ty TNHH Thương mại Hải Đăng', '0302345678', '45 Trần Phú, Hải Phòng', '0225-3845-678', NULL, 'cong_ty', 500000000, 15, 'company', 'Lê Thị Hải', 'active', 'Case: new/unresolved.'),
  ('30000000-0000-4000-8000-000000000003', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'CUS-003', 'Công ty CP Xây dựng Sông Hồng', '0100000001', '78 Lý Thường Kiệt, Hà Nội', '024-3933-4567', 'taichinh@songhong-jsc.example.vn', 'xay_dung', 5000000000, 45, 'company', 'Trần Văn Sơn', 'active', 'Case: multiple contracts.'),
  ('30000000-0000-4000-8000-000000000004', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'CUS-004', 'Công ty TNHH Dệt may Thăng Long', '0304567890', '220 Minh Khai, Hà Nội', '024-3862-9988', 'cnttl@thanglong-textile.example.vn', 'det_may', 1200000000, 30, 'company', 'Đỗ Thị Long', 'active', 'Case: overdue receivable.'),
  ('30000000-0000-4000-8000-000000000005', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'CUS-005', 'Công ty CP Dược phẩm An Khang', '0305678901', '9 Giải Phóng, Hà Nội', '024-3868-1122', 'ketoan@ankhang-pharma.example.vn', 'duoc_pham', 800000000, 30, 'company', 'Vũ Thị An', 'active', 'Case: partial payment.'),
  ('30000000-0000-4000-8000-000000000006', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'CUS-006', 'Công ty TNHH Vận tải Phương Nam', '0306789012', '150 Cộng Hòa, Tân Bình, TP.HCM', '028-3811-2233', 'taixe@phuongnam-transport.example.vn', 'van_tai', 600000000, 15, 'company', 'Ngô Văn Nam', 'active', 'Case: overpayment.'),
  ('30000000-0000-4000-8000-000000000007', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'CUS-007', 'Công ty CP Đầu tư Việt Tín', '0307890123', '33 Bà Triệu, Hà Nội', '024-3944-5566', 'vietin@viettin-invest.example.vn', 'dau_tu', 2000000000, 30, 'company', 'Phạm Việt Tín', 'active', 'Case: advance before invoice.'),
  ('30000000-0000-4000-8000-000000000008', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'CUS-008', 'Công ty TNHH MTV Ánh Dương', NULL, '5 Hoàng Diệu, Đà Nẵng', '0236-3711-222', NULL, 'cong_ty', 0, 0, 'company', NULL, 'inactive', 'Case: ambiguous/inactive, no MST on file.'),
  ('30000000-0000-4000-8000-000000000009', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'CUS-009', 'Công ty TNHH Thiết bị Y tế Hoà Bình', '0309012345', '60 Láng Hạ, Hà Nội', '024-3775-8899', 'ketoan@hoabinh-medical.example.vn', 'cong_ty', 400000000, 30, 'company', 'Hoà Văn Bình', 'active', 'Case: Luồng 02 known-customer-no-open-receivable fixture.')
ON CONFLICT (tenant_id, customer_code) DO NOTHING;

-- ============================================================
-- 2. SUPPLIERS (sim_suppliers) — 10 rows
-- ============================================================
INSERT INTO sim_suppliers (id, tenant_id, department_id, supplier_code, name, mst, country, currency, payment_terms, payment_terms_days, supplier_type, representative, phone, email, address, status, notes)
VALUES
  ('40000000-0000-4000-8000-000000000001', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'SUP-001', 'Công ty TNHH Cung ứng Đại Phát', '0200000001', 'VN', 'VND', 'Net 30', 30, 'company', 'Hoàng Đại Phát', '028-3777-0011', 'sales@daiphat.example.vn', '88 Điện Biên Phủ, TP.HCM', 'active', 'Case: normal active.'),
  ('40000000-0000-4000-8000-000000000002', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'SUP-002', 'Công ty CP Vật tư Kim Long', '0200000002', 'VN', 'VND', NULL, 0, 'company', 'Kim Văn Long', '024-3955-1122', NULL, '17 Kim Mã, Hà Nội', 'active', 'Case: new/unresolved.'),
  ('40000000-0000-4000-8000-000000000003', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'SUP-003', 'Công ty TNHH Thiết bị Toàn Cầu', '0200000003', 'VN', 'VND', 'Net 15', 15, 'company', 'Trịnh Toàn Cầu', '024-3966-3344', 'ap@toancau-equip.example.vn', '200 Cầu Giấy, Hà Nội', 'active', 'Case: valid VAT invoice.'),
  ('40000000-0000-4000-8000-000000000004', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'SUP-004', 'Công ty CP In ấn Sao Việt', '0200000004', 'VN', 'VND', 'Net 30', 30, 'company', 'Sao Văn Việt', '028-3988-5566', 'kd@saoviet-print.example.vn', '56 Cách Mạng Tháng Tám, TP.HCM', 'active', 'Case: cancelled invoice.'),
  ('40000000-0000-4000-8000-000000000005', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'SUP-005', 'Công ty TNHH Văn phòng phẩm Minh Anh', '0200000005', 'VN', 'VND', 'Net 30', 30, 'company', 'Minh Thị Anh', '024-3977-7788', 'order@minhanh-office.example.vn', '9 Thái Hà, Hà Nội', 'active', 'Case: duplicate invoice.'),
  ('40000000-0000-4000-8000-000000000006', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'SUP-006', 'Công ty CP Dịch vụ Kỹ thuật Phú Cường', '0200000006', 'VN', 'VND', 'Net 30', 30, 'company', 'Phú Văn Cường', '024-3999-1122', 'cskh@phucuong-tech.example.vn', '120 Xã Đàn, Hà Nội', 'active', 'Case: VAT non-deductible.'),
  ('40000000-0000-4000-8000-000000000007', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'SUP-007', 'Nhà hàng Sen Tây Hồ', NULL, 'VN', 'VND', NULL, 0, 'individual', 'Nguyễn Thị Sen', '024-3719-4455', NULL, '25 Xuân Diệu, Tây Hồ, Hà Nội', 'active', 'Case: restaurant, no VAT invoice — CIT non-deductible.'),
  ('40000000-0000-4000-8000-000000000008', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'SUP-008', 'Khách sạn Melia Hà Nội', '0200000008', 'VN', 'VND', 'Net 15', 15, 'company', NULL, '024-3934-3343', 'corporate@melia-hanoi.example.vn', '44B Lý Thường Kiệt, Hà Nội', 'active', 'Case: hotel over policy.'),
  ('40000000-0000-4000-8000-000000000009', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'SUP-009', 'Vietnam Airlines', '0100107638', 'VN', 'VND', 'Net 15', 15, 'company', NULL, '1900-1100', 'corporate@vietnamairlines.example.vn', '200 Nguyễn Sơn, Long Biên, Hà Nội', 'active', 'Case: travel/airfare.'),
  ('40000000-0000-4000-8000-000000000010', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'SUP-010', 'Công ty TNHH Kế toán - Kiểm toán Việt Long', '0200000010', 'VN', 'VND', 'Net 30', 30, 'company', 'Việt Văn Long', '024-3900-1234', 'info@vietlong-audit.example.vn', '15 Trần Duy Hưng, Hà Nội', 'active', 'Case: multiple bank accounts (notes field only).')
ON CONFLICT (tenant_id, supplier_code) DO NOTHING;

-- ============================================================
-- 3. EMPLOYEES (sim_employees) — needed for Luồng 03
-- ============================================================
INSERT INTO sim_employees (id, tenant_id, employee_code, full_name, department, position, monthly_expense_budget, outstanding_advance)
VALUES
  ('50000000-0000-4000-8000-000000000001', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', 'NV-001', 'Nguyễn Văn An', 'Kinh doanh', 'Chuyên viên', 5000000, 5000000),
  ('50000000-0000-4000-8000-000000000002', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', 'NV-002', 'Trần Thị Bình', 'Kinh doanh', 'Trưởng phòng', 8000000, 0),
  ('50000000-0000-4000-8000-000000000003', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', 'NV-003', 'Lê Văn Cường', 'Kỹ thuật', 'Chuyên viên', 4000000, 0),
  ('50000000-0000-4000-8000-000000000004', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', 'NV-004', 'Phạm Thị Dung', 'Hành chính', 'Chuyên viên', 3000000, 0)
ON CONFLICT DO NOTHING;

-- ============================================================
-- 4. EXPENSE POLICIES (department_expense_policies) — no unique
--    constraint in the live schema, guarded with WHERE NOT EXISTS.
-- ============================================================
INSERT INTO department_expense_policies (tenant_id, department_id, category, limit_amount, limit_unit, allow_exception, requires_receipt, debit_account, position)
SELECT '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'hotel', 1500000, 'per_item', true, true, '642', NULL
WHERE NOT EXISTS (SELECT 1 FROM department_expense_policies WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c' AND department_id = '0facef04-3e54-438b-8724-966dbbaaaa9a' AND category = 'hotel' AND position IS NULL);

INSERT INTO department_expense_policies (tenant_id, department_id, category, limit_amount, limit_unit, allow_exception, requires_receipt, debit_account, position)
SELECT '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'hotel', 2500000, 'per_item', true, true, '642', 'Trưởng phòng'
WHERE NOT EXISTS (SELECT 1 FROM department_expense_policies WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c' AND department_id = '0facef04-3e54-438b-8724-966dbbaaaa9a' AND category = 'hotel' AND position = 'Trưởng phòng');

INSERT INTO department_expense_policies (tenant_id, department_id, category, limit_amount, limit_unit, allow_exception, requires_receipt, debit_account, position)
SELECT '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'airfare', 100000000, 'per_claim', false, true, '641', NULL
WHERE NOT EXISTS (SELECT 1 FROM department_expense_policies WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c' AND department_id = '0facef04-3e54-438b-8724-966dbbaaaa9a' AND category = 'airfare' AND position IS NULL);

INSERT INTO department_expense_policies (tenant_id, department_id, category, limit_amount, limit_unit, allow_exception, requires_receipt, debit_account, position)
SELECT '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'meal', 100000000, 'per_claim', false, false, '642', NULL
WHERE NOT EXISTS (SELECT 1 FROM department_expense_policies WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c' AND department_id = '0facef04-3e54-438b-8724-966dbbaaaa9a' AND category = 'meal' AND position IS NULL);

INSERT INTO department_expense_policies (tenant_id, department_id, category, limit_amount, limit_unit, allow_exception, requires_receipt, debit_account, position)
SELECT '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'conference', 2000000, 'per_item', true, true, '642', NULL
WHERE NOT EXISTS (SELECT 1 FROM department_expense_policies WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c' AND department_id = '0facef04-3e54-438b-8724-966dbbaaaa9a' AND category = 'conference' AND position IS NULL);

-- ============================================================
-- 5. LUỒNG 02 supporting data — sim_ar_ledger open invoices
--    No unique constraint on sim_ar_ledger, guarded with WHERE NOT EXISTS
--    on (tenant_id, document_number).
-- ============================================================
INSERT INTO sim_ar_ledger (tenant_id, customer_id, document_date, document_number, document_type, debit, credit, due_date)
SELECT '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '30000000-0000-4000-8000-000000000001', CURRENT_DATE - 5, 'INV-BM-001', 'invoice', 594000000, 0, CURRENT_DATE + 25
WHERE NOT EXISTS (SELECT 1 FROM sim_ar_ledger WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c' AND document_number = 'INV-BM-001');

INSERT INTO sim_ar_ledger (tenant_id, customer_id, document_date, document_number, document_type, debit, credit, due_date)
SELECT '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '30000000-0000-4000-8000-000000000005', CURRENT_DATE - 10, 'INV-AK-100', 'invoice', 200000000, 0, CURRENT_DATE + 20
WHERE NOT EXISTS (SELECT 1 FROM sim_ar_ledger WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c' AND document_number = 'INV-AK-100');

INSERT INTO sim_ar_ledger (tenant_id, customer_id, document_date, document_number, document_type, debit, credit, due_date)
SELECT '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '30000000-0000-4000-8000-000000000006', CURRENT_DATE - 8, 'INV-PN-050', 'invoice', 2000000, 0, CURRENT_DATE + 20
WHERE NOT EXISTS (SELECT 1 FROM sim_ar_ledger WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c' AND document_number = 'INV-PN-050');

INSERT INTO sim_ar_ledger (tenant_id, customer_id, document_date, document_number, document_type, debit, credit, due_date)
SELECT '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '30000000-0000-4000-8000-000000000004', CURRENT_DATE - 95, 'INV-TL-001', 'invoice', 350000000, 0, CURRENT_DATE - 65
WHERE NOT EXISTS (SELECT 1 FROM sim_ar_ledger WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c' AND document_number = 'INV-TL-001');

-- ============================================================
-- 6. LUỒNG 01/02 supporting data — HD-001's contract + payment
--    schedule + milestone (fast-path so AR-001 can settle against a
--    KNOWN installment id without first driving the full /process
--    API call for this exact contract).
-- ============================================================
INSERT INTO contracts (id, user_id, tenant_id, department_id, title, party_a, party_b, signed_date, effective_date, end_date, total_value, currency, status, workflow_state, document_type, contract_number, subtotal, vat_rate, vat_amount, retention_percent, penalty_percent, tags)
VALUES (
  '60000000-0000-4000-8000-000000000001', '64aa15d2-f852-4a84-95fe-3390443300f2', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a',
  'Hợp đồng dịch vụ CNTT 147/2026/HĐDV', 'AccAgent Demo Co.', 'Công ty CP Kỹ thuật Bình Minh',
  '2026-08-15', '2026-08-15', '2026-12-31', 1980000000, 'VND', 'active', 'SCHEDULE_GENERATED',
  'service_contract', '147/2026/HĐDV', 1800000000, 10, 180000000, 5, 0.05, ARRAY['luong01','sample','normal']
)
ON CONFLICT (id) DO NOTHING;

-- contract_parties has no unique constraint in the live schema (only a PK
-- on its own random id) — a bare ON CONFLICT DO NOTHING is a silent no-op
-- and would duplicate rows on every re-run. Guarded with WHERE NOT EXISTS
-- on (contract_id, party_role) instead, for real idempotency.
INSERT INTO contract_parties (contract_id, tenant_id, department_id, party_role, name, tax_code, address, representative)
SELECT '60000000-0000-4000-8000-000000000001', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'seller', 'AccAgent Demo Co.', '0100000999', 'Trụ sở demo', 'Đại diện Demo'
WHERE NOT EXISTS (SELECT 1 FROM contract_parties WHERE contract_id = '60000000-0000-4000-8000-000000000001' AND party_role = 'seller');

INSERT INTO contract_parties (contract_id, tenant_id, department_id, party_role, name, tax_code, address, representative)
SELECT '60000000-0000-4000-8000-000000000001', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'buyer', 'Công ty CP Kỹ thuật Bình Minh', '0301234599', '12 Nguyễn Huệ, Quận 1, TP.HCM', 'Nguyễn Văn Bình'
WHERE NOT EXISTS (SELECT 1 FROM contract_parties WHERE contract_id = '60000000-0000-4000-8000-000000000001' AND party_role = 'buyer');

INSERT INTO contract_payment_schedules (id, contract_id, tenant_id, department_id, ordinal, description, percentage, subtotal_amount, vat_amount, total_amount, due_date, status)
VALUES
  ('61000000-0000-4000-8000-000000000001', '60000000-0000-4000-8000-000000000001', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 1, 'Ký hợp đồng', 30, 540000000, 54000000, 594000000, CURRENT_DATE + 5, 'pending'),
  ('61000000-0000-4000-8000-000000000002', '60000000-0000-4000-8000-000000000001', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 2, 'Nghiệm thu giai đoạn 1', 40, 720000000, 72000000, 792000000, NULL, 'pending')
ON CONFLICT (id) DO NOTHING;

UPDATE contract_payment_schedules SET trigger_event = 'Nghiệm thu giai đoạn 1'
WHERE id = '61000000-0000-4000-8000-000000000002' AND trigger_event IS NULL;

INSERT INTO contract_milestones (id, contract_id, tenant_id, department_id, ordinal, name, due_date, reminder_days, status)
VALUES
  ('62000000-0000-4000-8000-000000000001', '60000000-0000-4000-8000-000000000001', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 1, 'Bàn giao tài liệu thiết kế', CURRENT_DATE + 20, 7, 'pending'),
  ('62000000-0000-4000-8000-000000000002', '60000000-0000-4000-8000-000000000001', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 2, 'Nghiệm thu giai đoạn 1', NULL, 7, 'pending')
ON CONFLICT (id) DO NOTHING;

UPDATE contract_milestones SET trigger_event = 'Sau khi thanh toán đợt 1', duration_months = 1
WHERE id = '62000000-0000-4000-8000-000000000002' AND trigger_event IS NULL;

-- ============================================================
-- 7. LUỒNG 02 supporting data — AR-004's contract (advance case)
-- ============================================================
INSERT INTO contracts (id, user_id, tenant_id, department_id, title, party_a, party_b, signed_date, effective_date, total_value, currency, status, workflow_state, document_type, contract_number)
VALUES (
  '60000000-0000-4000-8000-000000000006', '64aa15d2-f852-4a84-95fe-3390443300f2', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a',
  'Hợp đồng tư vấn đầu tư 501/2026/HĐTV (hỗ trợ AR-004)', 'AccAgent Demo Co.', 'Công ty CP Đầu tư Việt Tín',
  '2026-08-05', '2026-08-05', 150000000, 'VND', 'active', 'SCHEDULE_GENERATED', 'service_contract', '501/2026/HĐTV'
)
ON CONFLICT (id) DO NOTHING;

INSERT INTO contract_parties (contract_id, tenant_id, department_id, party_role, name, tax_code)
SELECT '60000000-0000-4000-8000-000000000006', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 'buyer', 'Công ty CP Đầu tư Việt Tín', '0307890123'
WHERE NOT EXISTS (SELECT 1 FROM contract_parties WHERE contract_id = '60000000-0000-4000-8000-000000000006' AND party_role = 'buyer');

INSERT INTO contract_payment_schedules (id, contract_id, tenant_id, department_id, ordinal, description, percentage, subtotal_amount, vat_amount, total_amount, due_date, status)
VALUES ('61000000-0000-4000-8000-000000000006', '60000000-0000-4000-8000-000000000006', '74a79d3f-1662-4f2e-b67b-46ec1a6b403c', '0facef04-3e54-438b-8724-966dbbaaaa9a', 1, 'Đặt cọc tư vấn', 100, 150000000, 0, 150000000, CURRENT_DATE + 30, 'pending')
ON CONFLICT (id) DO NOTHING;

COMMIT;

-- ============================================================
-- Verify
-- ============================================================
SELECT 'customers' AS entity, count(*) FROM sim_customers WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c'
UNION ALL SELECT 'suppliers', count(*) FROM sim_suppliers WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c'
UNION ALL SELECT 'employees', count(*) FROM sim_employees WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c'
UNION ALL SELECT 'expense_policies', count(*) FROM department_expense_policies WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c'
UNION ALL SELECT 'ar_ledger_rows', count(*) FROM sim_ar_ledger WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c'
UNION ALL SELECT 'sample_contracts', count(*) FROM contracts WHERE tenant_id = '74a79d3f-1662-4f2e-b67b-46ec1a6b403c' AND id::text LIKE '60000000-%';
