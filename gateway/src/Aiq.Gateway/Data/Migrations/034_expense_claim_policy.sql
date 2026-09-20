-- Luồng 03 — Hóa đơn thanh toán (expense claim outcome engine + policy
-- framework). Two tables:
--   department_expense_policies — configured limits (empty by default;
--     no row for a category/department = no limit, never hardcoded).
--   expense_claim_results — one row per expense_claim voucher, mirrors
--     receipt_match_results' shape/spirit for Luồng 02.

CREATE TABLE IF NOT EXISTS department_expense_policies (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  department_id UUID NOT NULL,

  category TEXT,                 -- NULL = applies to every category
  limit_amount NUMERIC(18,2) NOT NULL,
  limit_unit TEXT NOT NULL DEFAULT 'per_claim',
  allow_exception BOOLEAN NOT NULL DEFAULT FALSE,
  requires_receipt BOOLEAN NOT NULL DEFAULT TRUE,
  active BOOLEAN NOT NULL DEFAULT TRUE,

  created_by UUID REFERENCES users(id) ON DELETE SET NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

  FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);

DO $$ BEGIN
  ALTER TABLE department_expense_policies ADD CONSTRAINT chk_expense_policy_unit
    CHECK (limit_unit IN ('per_claim','per_item','per_day','per_night','per_trip'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE INDEX IF NOT EXISTS idx_expense_policy_scope ON department_expense_policies(tenant_id, department_id, category) WHERE active;

CREATE TABLE IF NOT EXISTS expense_claim_results (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  department_id UUID NOT NULL,
  voucher_id UUID NOT NULL REFERENCES vouchers(id) ON DELETE CASCADE,

  employee_id UUID REFERENCES sim_employees(id),

  requested_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  approved_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  blocked_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  capped_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  non_deductible_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  advance_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  -- signed: positive = company still owes employee, negative = employee
  -- owes company back (advance > approved expense).
  payable_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  currency TEXT NOT NULL DEFAULT 'VND',

  duplicate_flags JSONB NOT NULL DEFAULT '[]'::jsonb,
  policy_flags JSONB NOT NULL DEFAULT '[]'::jsonb,
  resolved_vendors JSONB NOT NULL DEFAULT '[]'::jsonb,
  missing_documents TEXT,

  outcome TEXT NOT NULL,
  review_status TEXT NOT NULL DEFAULT 'proposed',
  reviewed_by UUID REFERENCES users(id),
  reviewed_at TIMESTAMPTZ,

  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

  UNIQUE(voucher_id),
  FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);

DO $$ BEGIN
  ALTER TABLE expense_claim_results ADD CONSTRAINT chk_expense_claim_outcome
    CHECK (outcome IN (
      'APPROVED','PARTIALLY_APPROVED','RETURN_FOR_DOCUMENTS',
      'WAITING_EXCEPTION_APPROVAL','REJECTED'
    ));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
  ALTER TABLE expense_claim_results ADD CONSTRAINT chk_expense_claim_review_status
    CHECK (review_status IN ('proposed','confirmed','rejected'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE INDEX IF NOT EXISTS idx_expense_claim_scope ON expense_claim_results(tenant_id, department_id, voucher_id);
