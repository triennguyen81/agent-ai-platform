-- Bank statement money_in -> the SAME canonical cash settlement
-- (cash_transactions / payment_applications / sub-ledger / journal).
--
-- Only the SOURCE differs. A statement voucher holds many lines, so
-- "one live posting per voucher" cannot hold for it:
--   * source_type = 'voucher'             : Phiếu thu/chi/UNC (unchanged) —
--                                            at most one live posting per voucher.
--   * source_type = 'bank_statement_line' : one statement line —
--                                            at most one live posting per line.
-- voucher_id stays filled (the statement voucher) for scope + journal rows.

ALTER TABLE cash_transactions
  ADD COLUMN IF NOT EXISTS source_type TEXT NOT NULL DEFAULT 'voucher',
  ADD COLUMN IF NOT EXISTS source_bank_transaction_id UUID REFERENCES sim_bank_transactions(id) ON DELETE RESTRICT,
  -- The bank's own transaction reference (deterministic identity for
  -- double-posting detection across sources).
  ADD COLUMN IF NOT EXISTS bank_reference TEXT;

DO $$ BEGIN
  ALTER TABLE cash_transactions ADD CONSTRAINT chk_cash_tx_source CHECK (
    (source_type = 'voucher' AND source_bank_transaction_id IS NULL)
    OR (source_type = 'bank_statement_line' AND source_bank_transaction_id IS NOT NULL));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DROP INDEX IF EXISTS uq_cash_tx_voucher_posted;
CREATE UNIQUE INDEX IF NOT EXISTS uq_cash_tx_voucher_posted
  ON cash_transactions (voucher_id) WHERE status = 'posted' AND source_type = 'voucher';
CREATE UNIQUE INDEX IF NOT EXISTS uq_cash_tx_bank_line_posted
  ON cash_transactions (source_bank_transaction_id) WHERE status = 'posted' AND source_bank_transaction_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_cash_tx_bank_reference
  ON cash_transactions (tenant_id, bank_reference) WHERE bank_reference IS NOT NULL;

-- A statement line already accounted for by ANOTHER source (e.g. the
-- Phiếu thu for the same transfer): ADMIN links it instead of posting it
-- twice. One existing posting can explain at most one statement line.
ALTER TABLE sim_bank_transactions
  ADD COLUMN IF NOT EXISTS reconciled_cash_transaction_id UUID REFERENCES cash_transactions(id) ON DELETE RESTRICT,
  ADD COLUMN IF NOT EXISTS reconciled_by UUID REFERENCES users(id) ON DELETE SET NULL,
  ADD COLUMN IF NOT EXISTS reconciled_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS settlement_evidence JSONB NOT NULL DEFAULT '{}'::jsonb;
CREATE UNIQUE INDEX IF NOT EXISTS uq_sbt_reconciled_cash_tx
  ON sim_bank_transactions (reconciled_cash_transaction_id) WHERE reconciled_cash_transaction_id IS NOT NULL;
