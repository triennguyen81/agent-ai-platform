-- R14 (business remediation) — Luồng 03 approval collapsed all three named
-- decision-makers (Trưởng bộ phận / Kế toán / Duyệt chi) into one ADMIN
-- tier. This adds the two intermediate review stages; the existing
-- reviewed_by/reviewed_at columns become the FINAL (payment-approval)
-- actor+timestamp, unchanged in meaning. Enforced sequence:
--   proposed -> dept_reviewed -> accounting_reviewed -> confirmed
-- with a different actor required at every stage (checked in application
-- code, since Postgres CHECK constraints can't reference prior-row history
-- cheaply) plus the existing R16 rule that the claim's own creator may
-- never act at any stage.
ALTER TABLE expense_claim_results
  ADD COLUMN IF NOT EXISTS dept_reviewed_by UUID REFERENCES users(id),
  ADD COLUMN IF NOT EXISTS dept_reviewed_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS accounting_reviewed_by UUID REFERENCES users(id),
  ADD COLUMN IF NOT EXISTS accounting_reviewed_at TIMESTAMPTZ;

ALTER TABLE expense_claim_results DROP CONSTRAINT IF EXISTS chk_expense_claim_review_status;
DO $$ BEGIN
  ALTER TABLE expense_claim_results ADD CONSTRAINT chk_expense_claim_review_status
    CHECK (review_status IN ('proposed','dept_reviewed','accounting_reviewed','confirmed','rejected'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
