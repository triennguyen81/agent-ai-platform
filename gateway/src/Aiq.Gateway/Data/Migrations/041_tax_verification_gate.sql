-- P1 audit fix (ACCAGENT_PROCESS_AUDIT_REPORT.md — tax-portal verification
-- exists but is never enforced as a gate). tct_verified was a one-way
-- true-only flag with no way to represent "checked and found invalid /
-- cancelled" or "checked but the provider was unavailable" — both real,
-- distinct outcomes the business process document's internal-control gate
-- #1 requires the system to react to differently (block vs. flag-for-human,
-- never silently treat as verified).
ALTER TABLE vouchers
  ADD COLUMN IF NOT EXISTS tax_verification_status TEXT,
  ADD COLUMN IF NOT EXISTS tax_verification_reason TEXT,
  ADD COLUMN IF NOT EXISTS tax_verification_checked_at TIMESTAMPTZ;

ALTER TABLE vouchers DROP CONSTRAINT IF EXISTS chk_tax_verification_status;
DO $$ BEGIN
  ALTER TABLE vouchers ADD CONSTRAINT chk_tax_verification_status
    CHECK (tax_verification_status IS NULL OR tax_verification_status IN ('verified', 'invalid', 'unavailable'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- Whether this specific claim actually needs a tax-portal check at all
-- (only claim items carrying a real invoice_no do) — computed once at
-- propose time so the approval gate never has to re-parse extracted_fields.
ALTER TABLE expense_claim_results
  ADD COLUMN IF NOT EXISTS requires_tax_verification BOOLEAN NOT NULL DEFAULT FALSE;
