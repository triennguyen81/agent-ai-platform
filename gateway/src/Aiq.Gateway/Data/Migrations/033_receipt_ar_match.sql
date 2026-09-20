-- Luồng 02 — Phiếu thu công nợ: minimal join structure for matching a
-- receipt voucher to a Customer, an open receivable/invoice, and (if
-- linkable) a Contract payment installment. One row per receipt voucher —
-- re-proposing (retry) upserts the same row, never duplicates it.

CREATE TABLE IF NOT EXISTS receipt_match_results (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  department_id UUID NOT NULL,
  voucher_id UUID NOT NULL REFERENCES vouchers(id) ON DELETE CASCADE,

  customer_id UUID REFERENCES sim_customers(id),
  customer_match_confidence NUMERIC(4,3) NOT NULL DEFAULT 0,
  customer_match_evidence TEXT,

  contract_id UUID REFERENCES contracts(id),
  installment_id UUID REFERENCES contract_payment_schedules(id),
  receivable_reference TEXT,

  outcome TEXT NOT NULL,
  receipt_amount NUMERIC(18,2) NOT NULL,
  matched_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  unallocated_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  currency TEXT NOT NULL DEFAULT 'VND',

  review_status TEXT NOT NULL DEFAULT 'proposed',
  reviewed_by UUID REFERENCES users(id),
  reviewed_at TIMESTAMPTZ,

  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

  UNIQUE(voucher_id),
  FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);

DO $$ BEGIN
  ALTER TABLE receipt_match_results ADD CONSTRAINT chk_receipt_match_outcome
    CHECK (outcome IN (
      'FULL_MATCH','PARTIAL_PAYMENT','OVERPAYMENT','ADVANCE_PAYMENT',
      'UNKNOWN_PAYER','CUSTOMER_NO_OPEN_RECEIVABLE','REVERSED_TRANSACTION',
      'OVERDUE_UNPAID','FOREIGN_CURRENCY'
    ));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
  ALTER TABLE receipt_match_results ADD CONSTRAINT chk_receipt_match_review_status
    CHECK (review_status IN ('proposed','confirmed','rejected'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE INDEX IF NOT EXISTS idx_receipt_match_scope ON receipt_match_results(tenant_id, department_id, voucher_id);
