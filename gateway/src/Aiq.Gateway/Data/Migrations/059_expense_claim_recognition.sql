-- Expense claim: AUTHORITATIVE expense recognition at accounting approval,
-- separate from (and a prerequisite of) the cash payment added in 058.
--
--   Dr 641/642/627 (department_expense_policies.debit_account, per category,
--                   net of deductible VAT)
--   Dr 1331        (deductible input VAT — invoice-backed lines only)
--       Cr 141     (employee advance cleared)
--       Cr 334     (payable to the employee — later settled by Dr 334 / Cr 111|112)
--
-- Lines come from the existing deterministic router
-- (JournalRoutingRules.ForExpenseSettlement); accounts never come from the
-- LLM, the browser, extracted JSON or a journal suggestion.

ALTER TABLE expense_claim_results
  ADD COLUMN IF NOT EXISTS recognition_status TEXT NOT NULL DEFAULT 'unrecognized',
  ADD COLUMN IF NOT EXISTS recognized_by UUID REFERENCES users(id),
  ADD COLUMN IF NOT EXISTS recognized_at TIMESTAMPTZ,
  -- Deterministic proposal output per category (gross approved, deductible
  -- VAT, and their un-capped values for an exception grant). Not derivable
  -- later: the per-category split exists only inside the classification.
  ADD COLUMN IF NOT EXISTS recognition_basis JSONB;

ALTER TABLE expense_claim_results DROP CONSTRAINT IF EXISTS chk_expense_claim_recognition_status;
ALTER TABLE expense_claim_results ADD CONSTRAINT chk_expense_claim_recognition_status CHECK (
     (recognition_status = 'recognized' AND recognized_by IS NOT NULL AND recognized_at IS NOT NULL)
  OR (recognition_status IN ('unrecognized', 'reversed'))
);

-- A claim is paid only after its liability was recognized. NOT VALID: a
-- claim paid before this migration (058 flow, no recognition existed) keeps
-- its history; every new write is checked.
DO $$ BEGIN
  ALTER TABLE expense_claim_results ADD CONSTRAINT chk_expense_claim_paid_after_recognition
    CHECK (payment_status <> 'paid' OR recognition_status = 'recognized') NOT VALID;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- Recognition lines: canonical posting rows linked to the claim (no cash).
ALTER TABLE voucher_journal_suggestions
  ADD COLUMN IF NOT EXISTS expense_claim_id UUID REFERENCES expense_claim_results(id) ON DELETE RESTRICT;
ALTER TABLE voucher_journal_suggestions DROP CONSTRAINT IF EXISTS chk_journal_expense_claim_posting;
ALTER TABLE voucher_journal_suggestions ADD CONSTRAINT chk_journal_expense_claim_posting CHECK (
  expense_claim_id IS NULL OR (source IN ('posting', 'reversal') AND cash_transaction_id IS NULL)
);
CREATE INDEX IF NOT EXISTS idx_journal_expense_claim ON voucher_journal_suggestions (expense_claim_id) WHERE expense_claim_id IS NOT NULL;

-- recognized => its posting lines exist (checked at commit).
CREATE OR REPLACE FUNCTION expense_claim_recognition_has_posting() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.recognition_status = 'recognized' AND NOT EXISTS (
       SELECT 1 FROM voucher_journal_suggestions j
       WHERE j.expense_claim_id = NEW.id AND j.source = 'posting') THEN
    RAISE EXCEPTION 'expense_claim % is recognized without a recognition posting', NEW.id USING ERRCODE = '23514';
  END IF;
  RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS trg_expense_claim_recognition_has_posting ON expense_claim_results;
CREATE CONSTRAINT TRIGGER trg_expense_claim_recognition_has_posting
  AFTER INSERT OR UPDATE OF recognition_status ON expense_claim_results
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION expense_claim_recognition_has_posting();

-- Once recognized, the amounts behind the journal are frozen: no silent
-- re-extraction / edit / re-proposal can leave a stale journal behind.
CREATE OR REPLACE FUNCTION expense_claim_recognized_amounts_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.recognition_status = 'recognized' AND NEW.recognition_status = 'recognized' AND (
       NEW.approved_amount IS DISTINCT FROM OLD.approved_amount OR NEW.advance_amount IS DISTINCT FROM OLD.advance_amount
    OR NEW.payable_amount IS DISTINCT FROM OLD.payable_amount OR NEW.vat_deductible_amount IS DISTINCT FROM OLD.vat_deductible_amount
    OR NEW.recognition_basis IS DISTINCT FROM OLD.recognition_basis OR NEW.employee_id IS DISTINCT FROM OLD.employee_id
    OR NEW.currency IS DISTINCT FROM OLD.currency) THEN
    RAISE EXCEPTION 'expense_claim_recognized: amounts of a recognized expense claim cannot change' USING ERRCODE = '23001';
  END IF;
  RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_expense_claim_recognized_amounts_frozen ON expense_claim_results;
CREATE TRIGGER trg_expense_claim_recognized_amounts_frozen
  BEFORE UPDATE ON expense_claim_results
  FOR EACH ROW EXECUTE FUNCTION expense_claim_recognized_amounts_frozen();

CREATE OR REPLACE FUNCTION voucher_recognized_expense_claim_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.extracted_fields IS DISTINCT FROM OLD.extracted_fields OR NEW.total_amount IS DISTINCT FROM OLD.total_amount
      OR NEW.voucher_type IS DISTINCT FROM OLD.voucher_type OR NEW.currency IS DISTINCT FROM OLD.currency
      OR NEW.document_date IS DISTINCT FROM OLD.document_date)
     AND EXISTS (SELECT 1 FROM expense_claim_results r WHERE r.voucher_id = NEW.id AND r.recognition_status = 'recognized') THEN
    RAISE EXCEPTION 'expense_claim_recognized: the expense claim is recognized — its document cannot be edited or re-extracted' USING ERRCODE = '23001';
  END IF;
  RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_voucher_recognized_expense_claim_frozen ON vouchers;
CREATE TRIGGER trg_voucher_recognized_expense_claim_frozen
  BEFORE UPDATE ON vouchers
  FOR EACH ROW EXECUTE FUNCTION voucher_recognized_expense_claim_frozen();

-- Authoritative journal lines (posting / reversal) are immutable history:
-- never updated, never deleted (a correction is a new reversal line). Before
-- this, replacing AI suggestions deleted EVERY line of the voucher and an
-- entry_no collision could overwrite a posted line.
CREATE OR REPLACE FUNCTION journal_posting_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.source IN ('posting', 'reversal') THEN
    RAISE EXCEPTION 'journal_posting_immutable: authoritative journal line % cannot be % ', OLD.id, lower(TG_OP) USING ERRCODE = '23001';
  END IF;
  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;
DROP TRIGGER IF EXISTS trg_journal_posting_immutable ON voucher_journal_suggestions;
CREATE TRIGGER trg_journal_posting_immutable
  BEFORE UPDATE OR DELETE ON voucher_journal_suggestions
  FOR EACH ROW EXECUTE FUNCTION journal_posting_immutable();
