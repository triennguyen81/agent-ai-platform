-- Chứng từ THU / CHI -> ADMIN duyệt -> tài khoản tiền -> đối trừ AR/AP.
--
-- Before this migration a receipt/payment was "applied" by inserting
-- receipt/payment rows straight into sim_ar_ledger/sim_ap_ledger, tagged
-- only by a `reference_note LIKE 'voucher:{id}%'` text convention:
--   * no first-class record of the money movement itself (amount, method,
--     cash/bank account, who approved it) — only its per-invoice slices;
--   * no way to hold money that could not be safely matched (it was either
--     forced onto an invoice or silently dropped);
--   * idempotency was an app-level COUNT(*) check (racy under double-click);
--   * unapply DELETEd the ledger rows (history lost).
--
-- Two additive tables fix that without touching the open-item model from
-- 045 (sim_*_ledger invoice rows + paid_amount/status stay the source of
-- truth for outstanding balances):
--
--   cash_transactions     one row per POSTED receipt/payment voucher: the
--                         cash/bank effect (Dr 111x/112x Cr 131, or
--                         Dr 331 Cr 111x/112x). Unapplied amount =
--                         amount - SUM(active applications) — money that
--                         is held as customer advance / supplier prepayment
--                         until an ADMIN allocates it.
--   payment_applications  cash_transaction -> ONE open item (AR or AP
--                         invoice row) -> applied_amount. N:M by design
--                         (one payment -> many invoices, many payments ->
--                         one invoice). Reversal flips status to 'reversed'
--                         and is mirrored by a reversing ledger row — never
--                         a DELETE.

CREATE TABLE IF NOT EXISTS cash_transactions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  department_id UUID NOT NULL,
  voucher_id UUID NOT NULL REFERENCES vouchers(id) ON DELETE RESTRICT,

  direction TEXT NOT NULL,
  counterparty_type TEXT NOT NULL,
  customer_id UUID REFERENCES sim_customers(id),
  supplier_id UUID REFERENCES sim_suppliers(id),

  amount NUMERIC(18,2) NOT NULL,
  currency TEXT NOT NULL DEFAULT 'VND',
  transaction_date DATE NOT NULL,
  payment_method TEXT NOT NULL,
  cash_account TEXT NOT NULL,
  counter_account TEXT NOT NULL,
  bank_account_id UUID REFERENCES sim_bank_accounts(id),
  document_number TEXT NOT NULL,
  description TEXT,

  status TEXT NOT NULL DEFAULT 'posted',
  posted_by UUID REFERENCES users(id) ON DELETE SET NULL,
  posted_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  reversed_by UUID REFERENCES users(id) ON DELETE SET NULL,
  reversed_at TIMESTAMPTZ,
  reversal_reason TEXT,

  FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);

DO $$ BEGIN
  ALTER TABLE cash_transactions ADD CONSTRAINT chk_cash_tx_direction CHECK (
    (direction = 'money_in'  AND counterparty_type = 'customer' AND customer_id IS NOT NULL AND supplier_id IS NULL AND counter_account = '131')
    OR
    (direction = 'money_out' AND counterparty_type = 'supplier' AND supplier_id IS NOT NULL AND customer_id IS NULL AND counter_account = '331'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE cash_transactions ADD CONSTRAINT chk_cash_tx_amount CHECK (amount > 0);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE cash_transactions ADD CONSTRAINT chk_cash_tx_method CHECK (
    (payment_method = 'cash' AND cash_account LIKE '111%') OR (payment_method = 'bank' AND cash_account LIKE '112%'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE cash_transactions ADD CONSTRAINT chk_cash_tx_status CHECK (status IN ('posted','reversed'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- Idempotency: at most ONE live posting per voucher. A double-click / retry
-- races into this index and the loser reads back the winner's row. Partial
-- on status so a reversed (voided) posting stays as history while the
-- voucher can be posted again after correction.
CREATE UNIQUE INDEX IF NOT EXISTS uq_cash_tx_voucher_posted
  ON cash_transactions (voucher_id) WHERE status = 'posted';
CREATE INDEX IF NOT EXISTS idx_cash_tx_scope
  ON cash_transactions (tenant_id, department_id, voucher_id);
CREATE INDEX IF NOT EXISTS idx_cash_tx_customer ON cash_transactions (customer_id) WHERE customer_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_cash_tx_supplier ON cash_transactions (supplier_id) WHERE supplier_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS payment_applications (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  department_id UUID NOT NULL,
  cash_transaction_id UUID NOT NULL REFERENCES cash_transactions(id) ON DELETE RESTRICT,
  ar_invoice_id UUID REFERENCES sim_ar_ledger(id) ON DELETE RESTRICT,
  ap_invoice_id UUID REFERENCES sim_ap_ledger(id) ON DELETE RESTRICT,
  applied_amount NUMERIC(18,2) NOT NULL,
  match_method TEXT NOT NULL,

  status TEXT NOT NULL DEFAULT 'active',
  applied_by UUID REFERENCES users(id) ON DELETE SET NULL,
  applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  reversed_by UUID REFERENCES users(id) ON DELETE SET NULL,
  reversed_at TIMESTAMPTZ,
  reversal_reason TEXT,

  FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);

DO $$ BEGIN
  ALTER TABLE payment_applications ADD CONSTRAINT chk_payment_app_target
    CHECK ((ar_invoice_id IS NOT NULL) <> (ap_invoice_id IS NOT NULL));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE payment_applications ADD CONSTRAINT chk_payment_app_amount CHECK (applied_amount > 0);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE payment_applications ADD CONSTRAINT chk_payment_app_status CHECK (status IN ('active','reversed'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
  ALTER TABLE payment_applications ADD CONSTRAINT chk_payment_app_method
    CHECK (match_method IN ('invoice_reference','exact_amount','outstanding_compatible','admin_selected','legacy'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- One ACTIVE application per (payment, invoice) pair: a retried allocation
-- can never double-apply the same money to the same invoice. Topping up an
-- existing pair is done by reversing + re-applying (auditable), not by a
-- second concurrent row.
CREATE UNIQUE INDEX IF NOT EXISTS uq_payment_app_active_ar
  ON payment_applications (cash_transaction_id, ar_invoice_id)
  WHERE status = 'active' AND ar_invoice_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_payment_app_active_ap
  ON payment_applications (cash_transaction_id, ap_invoice_id)
  WHERE status = 'active' AND ap_invoice_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_payment_app_ar_invoice ON payment_applications (ar_invoice_id) WHERE ar_invoice_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_payment_app_ap_invoice ON payment_applications (ap_invoice_id) WHERE ap_invoice_id IS NOT NULL;

-- Sub-ledger trace: every receipt/payment (and reversal) row written by the
-- new flow points at the application it represents.
ALTER TABLE sim_ar_ledger ADD COLUMN IF NOT EXISTS payment_application_id UUID REFERENCES payment_applications(id);
ALTER TABLE sim_ap_ledger ADD COLUMN IF NOT EXISTS payment_application_id UUID REFERENCES payment_applications(id);
-- Exactly one apply row and at most one reversal row per application for
-- rows written by the new flow (reference_note 'cash_tx:...'); backfilled
-- legacy rows may legitimately share one aggregated application.
CREATE UNIQUE INDEX IF NOT EXISTS uq_sim_ar_ledger_payment_app
  ON sim_ar_ledger (payment_application_id, document_type)
  WHERE payment_application_id IS NOT NULL AND reference_note LIKE 'cash_tx:%';
CREATE UNIQUE INDEX IF NOT EXISTS uq_sim_ap_ledger_payment_app
  ON sim_ap_ledger (payment_application_id, document_type)
  WHERE payment_application_id IS NOT NULL AND reference_note LIKE 'cash_tx:%';

-- Journal provenance: a row written by the posting service is a POSTED
-- entry, not an AI suggestion; a reversal is its own (swapped) row.
ALTER TABLE voucher_journal_suggestions
  ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'suggestion',
  ADD COLUMN IF NOT EXISTS cash_transaction_id UUID REFERENCES cash_transactions(id);
DO $$ BEGIN
  ALTER TABLE voucher_journal_suggestions ADD CONSTRAINT chk_journal_source
    CHECK (source IN ('suggestion','posting','reversal'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- ---------------------------------------------------------------------------
-- Backfill: receipts/payments already applied through the legacy path
-- (reference_note 'voucher:{32hex}|apply:{doc}') get a cash_transactions row
-- + one payment_applications row per invoice slice, so they can be unapplied
-- through the same audited path. Only rows whose voucher still exists with a
-- department are backfilled; discount rows are left as legacy history.
-- ---------------------------------------------------------------------------
WITH legacy AS (
  SELECT l.*, substring(l.reference_note from 'voucher:([0-9a-f]{32})\|apply:')::uuid AS legacy_voucher_id
  FROM sim_ar_ledger l
  WHERE l.document_type = 'receipt' AND l.payment_application_id IS NULL
    AND l.reference_note ~ '^voucher:[0-9a-f]{32}\|apply:'
), per_voucher AS (
  SELECT lg.tenant_id, lg.customer_id, lg.legacy_voucher_id,
         SUM(lg.credit) AS applied_total, MIN(lg.document_date) AS tx_date, MIN(lg.document_number) AS doc_no
  FROM legacy lg GROUP BY lg.tenant_id, lg.customer_id, lg.legacy_voucher_id
)
INSERT INTO cash_transactions (tenant_id, department_id, voucher_id, direction, counterparty_type, customer_id,
  amount, currency, transaction_date, payment_method, cash_account, counter_account, document_number,
  description, status, posted_by, posted_at)
SELECT pv.tenant_id, v.department_id, v.id, 'money_in', 'customer', pv.customer_id,
  GREATEST(COALESCE(v.total_amount, 0), pv.applied_total), COALESCE(v.currency, 'VND'), pv.tx_date,
  CASE WHEN lower(COALESCE(v.payment_method,'')) IN ('tm','tiền mặt','tien mat','cash') THEN 'cash' ELSE 'bank' END,
  CASE WHEN lower(COALESCE(v.payment_method,'')) IN ('tm','tiền mặt','tien mat','cash') THEN '1111' ELSE '1121' END,
  '131', pv.doc_no, 'Backfill từ luồng áp thu cũ (migration 051)', 'posted', v.user_id, NOW()
FROM per_voucher pv
JOIN vouchers v ON v.id = pv.legacy_voucher_id AND v.tenant_id = pv.tenant_id AND v.department_id IS NOT NULL
WHERE NOT EXISTS (SELECT 1 FROM cash_transactions c WHERE c.voucher_id = v.id AND c.status = 'posted');

WITH legacy AS (
  SELECT l.id AS ledger_id, l.credit AS amt, l.allocated_invoice_id,
         substring(l.reference_note from 'voucher:([0-9a-f]{32})\|apply:')::uuid AS legacy_voucher_id
  FROM sim_ar_ledger l
  WHERE l.document_type = 'receipt' AND l.payment_application_id IS NULL AND l.allocated_invoice_id IS NOT NULL
    AND l.reference_note ~ '^voucher:[0-9a-f]{32}\|apply:'
), grouped AS (
  SELECT legacy_voucher_id, allocated_invoice_id, SUM(amt) AS amt FROM legacy GROUP BY 1, 2
), ins AS (
  INSERT INTO payment_applications (tenant_id, department_id, cash_transaction_id, ar_invoice_id,
    applied_amount, match_method, status, applied_by, applied_at)
  SELECT c.tenant_id, c.department_id, c.id, g.allocated_invoice_id, g.amt, 'legacy', 'active', c.posted_by, c.posted_at
  FROM grouped g JOIN cash_transactions c ON c.voucher_id = g.legacy_voucher_id AND c.status = 'posted'
  WHERE g.amt > 0
  RETURNING id, cash_transaction_id, ar_invoice_id
)
UPDATE sim_ar_ledger l SET payment_application_id = ins.id
FROM ins
JOIN cash_transactions c ON c.id = ins.cash_transaction_id
JOIN legacy lg ON lg.legacy_voucher_id = c.voucher_id AND lg.allocated_invoice_id = ins.ar_invoice_id
WHERE l.id = lg.ledger_id;

WITH legacy AS (
  SELECT l.*, substring(l.reference_note from 'voucher:([0-9a-f]{32})\|apply:')::uuid AS legacy_voucher_id
  FROM sim_ap_ledger l
  WHERE l.document_type = 'payment' AND l.payment_application_id IS NULL
    AND l.reference_note ~ '^voucher:[0-9a-f]{32}\|apply:'
), per_voucher AS (
  SELECT lg.tenant_id, lg.supplier_id, lg.legacy_voucher_id,
         SUM(lg.debit) AS applied_total, MIN(lg.document_date) AS tx_date, MIN(lg.document_number) AS doc_no
  FROM legacy lg GROUP BY lg.tenant_id, lg.supplier_id, lg.legacy_voucher_id
)
INSERT INTO cash_transactions (tenant_id, department_id, voucher_id, direction, counterparty_type, supplier_id,
  amount, currency, transaction_date, payment_method, cash_account, counter_account, document_number,
  description, status, posted_by, posted_at)
SELECT pv.tenant_id, v.department_id, v.id, 'money_out', 'supplier', pv.supplier_id,
  GREATEST(COALESCE(v.total_amount, 0), pv.applied_total), COALESCE(v.currency, 'VND'), pv.tx_date,
  CASE WHEN lower(COALESCE(v.payment_method,'')) IN ('tm','tiền mặt','tien mat','cash') THEN 'cash' ELSE 'bank' END,
  CASE WHEN lower(COALESCE(v.payment_method,'')) IN ('tm','tiền mặt','tien mat','cash') THEN '1111' ELSE '1121' END,
  '331', pv.doc_no, 'Backfill từ luồng áp chi cũ (migration 051)', 'posted', v.user_id, NOW()
FROM per_voucher pv
JOIN vouchers v ON v.id = pv.legacy_voucher_id AND v.tenant_id = pv.tenant_id AND v.department_id IS NOT NULL
WHERE NOT EXISTS (SELECT 1 FROM cash_transactions c WHERE c.voucher_id = v.id AND c.status = 'posted');

WITH legacy AS (
  SELECT l.id AS ledger_id, l.debit AS amt, l.allocated_invoice_id,
         substring(l.reference_note from 'voucher:([0-9a-f]{32})\|apply:')::uuid AS legacy_voucher_id
  FROM sim_ap_ledger l
  WHERE l.document_type = 'payment' AND l.payment_application_id IS NULL AND l.allocated_invoice_id IS NOT NULL
    AND l.reference_note ~ '^voucher:[0-9a-f]{32}\|apply:'
), grouped AS (
  SELECT legacy_voucher_id, allocated_invoice_id, SUM(amt) AS amt FROM legacy GROUP BY 1, 2
), ins AS (
  INSERT INTO payment_applications (tenant_id, department_id, cash_transaction_id, ap_invoice_id,
    applied_amount, match_method, status, applied_by, applied_at)
  SELECT c.tenant_id, c.department_id, c.id, g.allocated_invoice_id, g.amt, 'legacy', 'active', c.posted_by, c.posted_at
  FROM grouped g JOIN cash_transactions c ON c.voucher_id = g.legacy_voucher_id AND c.status = 'posted'
  WHERE g.amt > 0
  RETURNING id, cash_transaction_id, ap_invoice_id
)
UPDATE sim_ap_ledger l SET payment_application_id = ins.id
FROM ins
JOIN cash_transactions c ON c.id = ins.cash_transaction_id
JOIN legacy lg ON lg.legacy_voucher_id = c.voucher_id AND lg.allocated_invoice_id = ins.ap_invoice_id
WHERE l.id = lg.ledger_id;
