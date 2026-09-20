-- R06 (business remediation) — Contract -> Invoice/Receipt traceability was
-- only a heuristic (tax-code + earliest-unpaid-installment join, see
-- Receipts/IReceiptMatchRepository.cs FindOpenInstallmentAsync), never a
-- persistent relationship. Adds an explicit, optional link from a voucher
-- (invoice or receipt) to the specific contract/installment it fulfills.
-- Nullable and application-set (never auto-inferred here — inferring it
-- automatically would just be the same heuristic already in place; this
-- column exists so a real link can be RECORDED once known, not guessed).
-- Same plain-FK convention already used by receipt_match_results.contract_id
-- (033_receipt_ar_match.sql) — tenant scoping enforced at the application
-- layer (the linking endpoint verifies both rows belong to the caller's own
-- tenant+department), consistent with that precedent.
ALTER TABLE vouchers
  ADD COLUMN IF NOT EXISTS contract_id UUID REFERENCES contracts(id),
  ADD COLUMN IF NOT EXISTS contract_installment_id UUID REFERENCES contract_payment_schedules(id);

CREATE INDEX IF NOT EXISTS idx_vouchers_contract ON vouchers(contract_id) WHERE contract_id IS NOT NULL;
