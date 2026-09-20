-- AccAgent Luong 01: department-scoped contract processing.
ALTER TABLE contracts ADD COLUMN IF NOT EXISTS department_id UUID;
ALTER TABLE contracts ADD COLUMN IF NOT EXISTS document_type TEXT NOT NULL DEFAULT 'contract';
ALTER TABLE contracts ADD COLUMN IF NOT EXISTS workflow_state TEXT NOT NULL DEFAULT 'RECEIVED';
ALTER TABLE contracts ADD COLUMN IF NOT EXISTS contract_number TEXT;
ALTER TABLE contracts ADD COLUMN IF NOT EXISTS subtotal NUMERIC(18,2);
ALTER TABLE contracts ADD COLUMN IF NOT EXISTS vat_rate NUMERIC(7,4);
ALTER TABLE contracts ADD COLUMN IF NOT EXISTS vat_amount NUMERIC(18,2);
ALTER TABLE contracts ADD COLUMN IF NOT EXISTS retention_percent NUMERIC(7,4);
ALTER TABLE contracts ADD COLUMN IF NOT EXISTS penalty_percent NUMERIC(7,4);

UPDATE contracts c SET department_id=u.department_id FROM users u
WHERE c.user_id=u.id AND c.tenant_id=u.tenant_id AND c.department_id IS NULL;

DO $$ BEGIN
  ALTER TABLE contracts ADD CONSTRAINT fk_contracts_department_tenant
    FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE contracts ADD CONSTRAINT chk_contract_workflow_state CHECK (workflow_state IN
    ('RECEIVED','CLASSIFIED','EXTRACTED','VALIDATED','SCHEDULE_GENERATED','WAITING_REVIEW','APPROVED','HANDED_OFF','NEEDS_DOCUMENT','NEEDS_REVIEW','REJECTED'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
CREATE INDEX IF NOT EXISTS idx_contracts_department ON contracts(tenant_id, department_id, created_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS uq_contract_number_department
  ON contracts(tenant_id, department_id, contract_number) WHERE contract_number IS NOT NULL;

CREATE TABLE IF NOT EXISTS contract_parties (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE, department_id UUID NOT NULL,
  party_role TEXT NOT NULL, name TEXT NOT NULL, tax_code TEXT, address TEXT, representative TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);
CREATE TABLE IF NOT EXISTS contract_payment_schedules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE, department_id UUID NOT NULL,
  ordinal INT NOT NULL, description TEXT NOT NULL, percentage NUMERIC(7,4) NOT NULL,
  subtotal_amount NUMERIC(18,2) NOT NULL, vat_amount NUMERIC(18,2) NOT NULL, total_amount NUMERIC(18,2) NOT NULL,
  due_date DATE, status TEXT NOT NULL DEFAULT 'pending', created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE(contract_id, ordinal), FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);
CREATE TABLE IF NOT EXISTS contract_milestones (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE, department_id UUID NOT NULL,
  ordinal INT NOT NULL, name TEXT NOT NULL, due_date DATE, reminder_days INT NOT NULL DEFAULT 7,
  status TEXT NOT NULL DEFAULT 'pending', created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE(contract_id, ordinal), FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);
CREATE TABLE IF NOT EXISTS contract_alerts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE, department_id UUID NOT NULL,
  code TEXT NOT NULL, severity TEXT NOT NULL, message TEXT NOT NULL, field_name TEXT, resolved_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);
CREATE TABLE IF NOT EXISTS contract_counterparty_proposals (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE, department_id UUID NOT NULL,
  party_id UUID REFERENCES contract_parties(id) ON DELETE SET NULL, proposed_action TEXT NOT NULL,
  rationale TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'proposed', created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);
CREATE TABLE IF NOT EXISTS contract_handoff_packages (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE, department_id UUID NOT NULL,
  payload JSONB NOT NULL, status TEXT NOT NULL DEFAULT 'prepared', approved_by UUID REFERENCES users(id),
  exported_at TIMESTAMPTZ, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);
CREATE TABLE IF NOT EXISTS contract_history (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE, department_id UUID NOT NULL,
  actor_id UUID REFERENCES users(id), event_type TEXT NOT NULL, from_state TEXT, to_state TEXT, details JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS idx_contract_payments_scope ON contract_payment_schedules(tenant_id, department_id, contract_id);
CREATE INDEX IF NOT EXISTS idx_contract_milestones_scope ON contract_milestones(tenant_id, department_id, contract_id);
CREATE INDEX IF NOT EXISTS idx_contract_alerts_scope ON contract_alerts(tenant_id, department_id, contract_id, resolved_at);
CREATE INDEX IF NOT EXISTS idx_contract_history_scope ON contract_history(tenant_id, department_id, contract_id, created_at);

INSERT INTO business_capabilities(code,name,description,category) VALUES
 ('contract.intake','Tiếp nhận hợp đồng','Tiếp nhận tài liệu hợp đồng.','contract'),
 ('contract.classify','Phân loại hợp đồng','Phân loại hợp đồng, phụ lục, nghiệm thu.','contract'),
 ('contract.extract','Trích xuất hợp đồng','Trích xuất dữ liệu ngữ nghĩa.','contract'),
 ('contract.validate','Kiểm tra hợp đồng','Kiểm tra dữ liệu bằng quy tắc xác định.','contract'),
 ('contract.payment_schedule','Lịch thanh toán','Tạo lịch thanh toán.','contract'),
 ('contract.milestone_tracking','Theo dõi mốc','Theo dõi mốc và nhắc việc.','contract'),
 ('contract.risk_review','Rà soát rủi ro','So sánh quy tắc hợp đồng chuẩn.','contract'),
 ('contract.counterparty_check','Kiểm tra đối tác','Chuẩn bị đề xuất đối tác.','contract'),
 ('contract.handoff','Bàn giao kế toán','Chuẩn bị dữ liệu bàn giao đã duyệt; không ghi sổ.','contract')
ON CONFLICT(code) DO NOTHING;
