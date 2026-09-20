-- Contract -> AR/AP lifecycle hardening (Sales + Purchase).
--
-- Two independent, additive fixes:
--
-- 1. Deterministic invoice/contract DIRECTION. Today `vouchers.voucher_type`
--    ('invoice_vat_out' vs 'invoice_vat_in') is set by the LLM extraction
--    pipeline from document text and trusted as-is by ApplyInvoiceAsync /
--    ApplyExpenseAsync — there is no server-side check against the tenant's
--    own registered tax ID. `tenants.company_tax_id` is the trusted,
--    server-side-only identity InvoiceDirectionResolver needs; nullable
--    because most existing tenants have never set it (direction resolution
--    then safely returns NEEDS_REVIEW rather than guessing).
--    `contracts.direction` is a NEW, orthogonal column — `document_type`
--    already carries free-text genre values including 'sale_contract'/
--    'purchase_contract', but those are caller-supplied and never verified
--    against contract_parties.tax_code, and conflate subject-matter genre
--    (service/goods/rent/loan) with direction (who sells to whom), so it is
--    not safe to reuse or redefine here.
--
-- 2. AR/AP OPEN ITEMS. sim_ar_ledger / sim_ap_ledger are flat ledgers: an
--    invoice-type row's own `credit`/`debit` field is never updated when a
--    receipt/payment is later applied against it — settlement is instead
--    recorded as a completely separate, unlinked ledger row. This means
--    "open invoices" (GetReceivablesAsync/GetPayablesAsync) never reflects
--    per-invoice partial/full settlement once a customer/supplier has more
--    than one invoice; only the aggregate current_ar/current_ap counter
--    (updated in the same transaction) has ever been reliable. Adding a
--    real status/paid_amount per invoice row, plus a real unique constraint
--    for idempotency (replacing the fragile `reference_note LIKE` text
--    match), and an `allocated_invoice_id` self-reference so a receipt/
--    payment row can be traced back to (and reversed against) the specific
--    invoice it settled.

ALTER TABLE tenants
  ADD COLUMN IF NOT EXISTS company_tax_id TEXT;

ALTER TABLE contracts
  ADD COLUMN IF NOT EXISTS direction TEXT;

DO $$ BEGIN
  ALTER TABLE contracts ADD CONSTRAINT chk_contracts_direction
    CHECK (direction IS NULL OR direction IN ('sales','purchase','needs_review'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- sim_ar_ledger: add voucher_id (sim_ap_ledger already has it — this closes
-- that pre-existing asymmetry), open-item status/paid_amount, and the same
-- optional contract linkage pattern vouchers.contract_id already uses
-- (038_voucher_contract_link.sql) — never auto-inferred, only propagated
-- from a voucher that already carries a real, application-set link.
ALTER TABLE sim_ar_ledger
  ADD COLUMN IF NOT EXISTS voucher_id UUID,
  ADD COLUMN IF NOT EXISTS contract_id UUID REFERENCES contracts(id),
  ADD COLUMN IF NOT EXISTS contract_payment_schedule_id UUID REFERENCES contract_payment_schedules(id),
  ADD COLUMN IF NOT EXISTS paid_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'open',
  ADD COLUMN IF NOT EXISTS allocated_invoice_id UUID REFERENCES sim_ar_ledger(id);

ALTER TABLE sim_ap_ledger
  ADD COLUMN IF NOT EXISTS contract_id UUID REFERENCES contracts(id),
  ADD COLUMN IF NOT EXISTS contract_payment_schedule_id UUID REFERENCES contract_payment_schedules(id),
  ADD COLUMN IF NOT EXISTS paid_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'open',
  ADD COLUMN IF NOT EXISTS allocated_invoice_id UUID REFERENCES sim_ap_ledger(id);

DO $$ BEGIN
  ALTER TABLE sim_ar_ledger ADD CONSTRAINT chk_sim_ar_ledger_status
    CHECK (status IN ('open','partial','paid'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
  ALTER TABLE sim_ap_ledger ADD CONSTRAINT chk_sim_ap_ledger_status
    CHECK (status IN ('open','partial','paid'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- Existing invoice-type rows: their true historical paid state cannot be
-- reconstructed (settlement was never linked back to a specific invoice
-- row) — default to 'open'/paid_amount=0 is the honest, safe choice; it
-- never fabricates a settled state, and the tenant's own current_ar/
-- current_ap aggregate (unchanged by this migration) remains the number
-- actually shown today. Non-invoice rows (receipt/payment/discount) keep
-- status='open' too — status/paid_amount are meaningful only for
-- document_type='invoice' rows; the app layer never reads them for others.

-- Idempotency: replaces the fragile `reference_note LIKE 'voucher:%'` text
-- match with a real constraint. Partial (voucher_id IS NOT NULL) because
-- older manually-entered rows (seed/demo data, bank-statement lines with no
-- voucher) never carried one and must stay insertable.
CREATE UNIQUE INDEX IF NOT EXISTS uq_sim_ar_ledger_tenant_voucher
  ON sim_ar_ledger (tenant_id, voucher_id) WHERE voucher_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_sim_ap_ledger_tenant_voucher
  ON sim_ap_ledger (tenant_id, voucher_id) WHERE voucher_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_sim_ar_ledger_open
  ON sim_ar_ledger (customer_id, status) WHERE document_type = 'invoice';
CREATE INDEX IF NOT EXISTS idx_sim_ap_ledger_open
  ON sim_ap_ledger (supplier_id, status) WHERE document_type = 'invoice';
CREATE INDEX IF NOT EXISTS idx_sim_ar_ledger_schedule
  ON sim_ar_ledger (contract_payment_schedule_id) WHERE contract_payment_schedule_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_sim_ap_ledger_schedule
  ON sim_ap_ledger (contract_payment_schedule_id) WHERE contract_payment_schedule_id IS NOT NULL;
