-- Customer/Supplier BANK ACCOUNT MASTER + bank statement transaction lines
-- with Customer identification.
--
-- Scope of this phase: Bank Statement -> Transaction -> Customer. NOTHING
-- here posts journals, reduces AR/AP, or creates payment_applications.
--
-- Reuse, not parallel models:
--   * MY_COMPANY's own accounts stay in sim_bank_accounts (already used by
--     cash settlement for bank balances) — a statement's own account links
--     there when it is registered.
--   * Statement lines extend the existing (unused) sim_bank_transactions
--     table instead of a second transaction table.
--   * Counterparty (customer/supplier) accounts are new — no equivalent
--     existed — in ONE table with an explicit owner type and real FKs.

CREATE TABLE IF NOT EXISTS counterparty_bank_accounts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  department_id UUID NOT NULL,
  owner_type TEXT NOT NULL,
  customer_id UUID REFERENCES sim_customers(id) ON DELETE RESTRICT,
  supplier_id UUID REFERENCES sim_suppliers(id) ON DELETE RESTRICT,

  bank_code TEXT,
  bank_name TEXT,
  account_number TEXT NOT NULL,              -- as entered; never an integer
  account_number_normalized TEXT NOT NULL,   -- separators stripped, leading zeros kept
  account_name TEXT,
  account_name_normalized TEXT,
  branch TEXT,
  currency TEXT NOT NULL DEFAULT 'VND',
  is_primary BOOLEAN NOT NULL DEFAULT FALSE,
  is_active BOOLEAN NOT NULL DEFAULT TRUE,

  -- How the record came to exist (audit): typed by ADMIN, or learned from a
  -- confirmed statement match (explicit ADMIN action, never silent).
  source TEXT NOT NULL DEFAULT 'manual',
  source_transaction_id UUID,

  created_by UUID REFERENCES users(id) ON DELETE SET NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_by UUID REFERENCES users(id) ON DELETE SET NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  deactivated_by UUID REFERENCES users(id) ON DELETE SET NULL,
  deactivated_at TIMESTAMPTZ,

  FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);

DO $$ BEGIN
  ALTER TABLE counterparty_bank_accounts ADD CONSTRAINT chk_cba_owner CHECK (
    (owner_type = 'customer' AND customer_id IS NOT NULL AND supplier_id IS NULL)
    OR (owner_type = 'supplier' AND supplier_id IS NOT NULL AND customer_id IS NULL));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE counterparty_bank_accounts ADD CONSTRAINT chk_cba_account CHECK (length(account_number_normalized) > 0);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE counterparty_bank_accounts ADD CONSTRAINT chk_cba_source CHECK (source IN ('manual','statement_review'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- One ACTIVE owner per (bank, account) inside a tenant+department — never
-- global, so unrelated tenants can hold the same number. Accounts with an
-- unknown bank share the '' key, so the same bare number cannot be active
-- twice either; cross-bank collisions are caught by the app (and the
-- matcher returns NEEDS_REVIEW rather than picking one).
CREATE UNIQUE INDEX IF NOT EXISTS uq_cba_active_account
  ON counterparty_bank_accounts (tenant_id, department_id, account_number_normalized, COALESCE(upper(bank_code), ''))
  WHERE is_active;
CREATE INDEX IF NOT EXISTS idx_cba_lookup
  ON counterparty_bank_accounts (tenant_id, department_id, account_number_normalized) WHERE is_active;
CREATE INDEX IF NOT EXISTS idx_cba_customer ON counterparty_bank_accounts (customer_id) WHERE customer_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_cba_supplier ON counterparty_bank_accounts (supplier_id) WHERE supplier_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Statement lines: extend sim_bank_transactions.
-- ---------------------------------------------------------------------------
ALTER TABLE sim_bank_transactions ALTER COLUMN account_id DROP NOT NULL;

ALTER TABLE sim_bank_transactions
  ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE,
  ADD COLUMN IF NOT EXISTS department_id UUID,
  ADD COLUMN IF NOT EXISTS voucher_id UUID REFERENCES vouchers(id) ON DELETE RESTRICT,
  ADD COLUMN IF NOT EXISTS line_no INT,
  ADD COLUMN IF NOT EXISTS company_account_number TEXT,
  ADD COLUMN IF NOT EXISTS direction TEXT,
  ADD COLUMN IF NOT EXISTS amount NUMERIC(18,2),
  ADD COLUMN IF NOT EXISTS currency TEXT NOT NULL DEFAULT 'VND',
  ADD COLUMN IF NOT EXISTS counterparty_account_normalized TEXT,
  ADD COLUMN IF NOT EXISTS counterparty_name_normalized TEXT,
  ADD COLUMN IF NOT EXISTS counterparty_bank_code TEXT,
  ADD COLUMN IF NOT EXISTS counterparty_bank_name TEXT,
  ADD COLUMN IF NOT EXISTS customer_id UUID REFERENCES sim_customers(id) ON DELETE RESTRICT,
  ADD COLUMN IF NOT EXISTS supplier_id UUID REFERENCES sim_suppliers(id) ON DELETE RESTRICT,
  ADD COLUMN IF NOT EXISTS counterparty_bank_account_id UUID REFERENCES counterparty_bank_accounts(id) ON DELETE RESTRICT,
  ADD COLUMN IF NOT EXISTS match_status TEXT,
  ADD COLUMN IF NOT EXISTS match_method TEXT,
  ADD COLUMN IF NOT EXISTS match_confidence NUMERIC(4,3),
  -- Automatic matcher output (signals, candidates, warnings). Never
  -- overwritten by an ADMIN decision — that is appended under "decisions".
  ADD COLUMN IF NOT EXISTS match_evidence JSONB NOT NULL DEFAULT '{}'::jsonb,
  ADD COLUMN IF NOT EXISTS matched_by UUID REFERENCES users(id) ON DELETE SET NULL,
  ADD COLUMN IF NOT EXISTS matched_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS confirmed_by UUID REFERENCES users(id) ON DELETE SET NULL,
  ADD COLUMN IF NOT EXISTS confirmed_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS fingerprint TEXT;

DO $$ BEGIN
  ALTER TABLE sim_bank_transactions ADD CONSTRAINT chk_sbt_direction
    CHECK (direction IS NULL OR direction IN ('money_in','money_out'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE sim_bank_transactions ADD CONSTRAINT chk_sbt_match_status
    CHECK (match_status IS NULL OR match_status IN ('matched','needs_review','unmatched'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE sim_bank_transactions ADD CONSTRAINT chk_sbt_scope
    CHECK (tenant_id IS NULL OR department_id IS NOT NULL);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE sim_bank_transactions ADD CONSTRAINT fk_sbt_department
    FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- Idempotent import: the same statement line can never be stored twice in a
-- tenant, whichever upload it came from.
CREATE UNIQUE INDEX IF NOT EXISTS uq_sbt_tenant_fingerprint
  ON sim_bank_transactions (tenant_id, fingerprint) WHERE fingerprint IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_sbt_voucher ON sim_bank_transactions (voucher_id, line_no) WHERE voucher_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_sbt_scope_status ON sim_bank_transactions (tenant_id, department_id, match_status);
