-- Complete Luong 01 without introducing a parallel document/checklist stack.
ALTER TABLE contracts ADD COLUMN IF NOT EXISTS contract_code TEXT;
ALTER TABLE contracts ADD COLUMN IF NOT EXISTS warranty_months INT;
ALTER TABLE contracts ADD COLUMN IF NOT EXISTS hitl_action_id UUID REFERENCES hitl_pending_actions(id) ON DELETE SET NULL;
ALTER TABLE contract_clauses ADD COLUMN IF NOT EXISTS clause_type TEXT;
ALTER TABLE contract_clauses ADD COLUMN IF NOT EXISTS is_open BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE contract_clauses ADD COLUMN IF NOT EXISTS trigger_event TEXT;
CREATE UNIQUE INDEX IF NOT EXISTS uq_contract_code_department
  ON contracts(tenant_id, department_id, contract_code) WHERE contract_code IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_contract_scope_identity ON contracts(id,tenant_id,department_id);

ALTER TABLE contract_checklist_items ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'manual';
ALTER TABLE contract_checklist_items ADD COLUMN IF NOT EXISTS department_id UUID;
UPDATE contract_checklist_items i SET department_id=c.department_id
FROM contracts c WHERE c.id=i.contract_id AND i.department_id IS NULL;
DO $$ BEGIN
  ALTER TABLE contract_checklist_items ADD CONSTRAINT fk_contract_checklist_department_tenant
    FOREIGN KEY(department_id,tenant_id) REFERENCES departments(id,tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
CREATE INDEX IF NOT EXISTS idx_contract_checklist_scope
  ON contract_checklist_items(tenant_id,department_id,contract_id,status);

CREATE TABLE IF NOT EXISTS contract_documents (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  department_id UUID NOT NULL,
  uploaded_by UUID NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  document_type TEXT NOT NULL CHECK(document_type IN ('contract','sale_contract','purchase_contract','rent_contract','loan_contract','appendix','acceptance','delivery','other')),
  referenced_code TEXT,
  original_filename TEXT NOT NULL,
  content_type TEXT NOT NULL,
  storage_key TEXT NOT NULL,
  sha256 TEXT NOT NULL,
  size_bytes BIGINT NOT NULL CHECK(size_bytes>0),
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  FOREIGN KEY(department_id,tenant_id) REFERENCES departments(id,tenant_id) ON DELETE RESTRICT,
  UNIQUE(contract_id,sha256)
);
CREATE INDEX IF NOT EXISTS idx_contract_documents_scope
  ON contract_documents(tenant_id,department_id,contract_id,created_at);

DO $$ DECLARE t TEXT; BEGIN
  FOREACH t IN ARRAY ARRAY['contract_parties','contract_payment_schedules','contract_milestones','contract_alerts','contract_counterparty_proposals','contract_handoff_packages','contract_history','contract_documents','contract_checklist_items'] LOOP
    BEGIN
      EXECUTE format('ALTER TABLE %I ADD CONSTRAINT %I FOREIGN KEY(contract_id,tenant_id,department_id) REFERENCES contracts(id,tenant_id,department_id) ON DELETE CASCADE',t,'fk_'||t||'_contract_scope');
    EXCEPTION WHEN duplicate_object THEN NULL;
    END;
  END LOOP;
END $$;
