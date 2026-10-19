-- Expense claim final approval -> ACTUAL payment through the canonical cash
-- infrastructure (cash_transactions + posting journal), not a parallel engine.
--
-- Accounting route already established by the repository
-- (JournalRoutingRules.ForExpenseSettlement): the reimbursable remainder
-- after the employee's advance is a liability on 334 (phải trả người lao
-- động). Paying it is therefore Dr 334 / Cr 1111 (cash) | 112x (company bank
-- account), counterparty = the employee.
--
-- Minimal additions:
--   cash_transactions: employee counterparty + expense_claim source link,
--                      at most one live posting per claim.
--   expense_claim_results: payment status separate from approval status,
--                      the posting that paid it, who and when.

ALTER TABLE cash_transactions
  ADD COLUMN IF NOT EXISTS employee_id UUID REFERENCES sim_employees(id),
  ADD COLUMN IF NOT EXISTS source_expense_claim_id UUID REFERENCES expense_claim_results(id) ON DELETE RESTRICT;

ALTER TABLE cash_transactions DROP CONSTRAINT IF EXISTS chk_cash_tx_direction;
ALTER TABLE cash_transactions ADD CONSTRAINT chk_cash_tx_direction CHECK (
     (direction = 'money_in'  AND counterparty_type = 'customer' AND customer_id IS NOT NULL
        AND supplier_id IS NULL AND employee_id IS NULL AND counter_account = '131')
  OR (direction = 'money_out' AND counterparty_type = 'supplier' AND supplier_id IS NOT NULL
        AND customer_id IS NULL AND employee_id IS NULL AND counter_account = '331')
  OR (direction = 'money_out' AND counterparty_type = 'employee' AND employee_id IS NOT NULL
        AND customer_id IS NULL AND supplier_id IS NULL AND counter_account = '334')
);

ALTER TABLE cash_transactions DROP CONSTRAINT IF EXISTS chk_cash_tx_source;
ALTER TABLE cash_transactions ADD CONSTRAINT chk_cash_tx_source CHECK (
     (source_type = 'voucher' AND source_bank_transaction_id IS NULL AND source_expense_claim_id IS NULL)
  OR (source_type = 'bank_statement_line' AND source_bank_transaction_id IS NOT NULL AND source_expense_claim_id IS NULL)
  OR (source_type = 'expense_claim' AND source_expense_claim_id IS NOT NULL AND source_bank_transaction_id IS NULL
        AND counterparty_type = 'employee')
);

-- Idempotency at the DB level: one live (posted) payment per claim.
CREATE UNIQUE INDEX IF NOT EXISTS uq_cash_tx_expense_claim_posted
  ON cash_transactions (source_expense_claim_id)
  WHERE status = 'posted' AND source_expense_claim_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_cash_tx_employee ON cash_transactions (employee_id) WHERE employee_id IS NOT NULL;

ALTER TABLE expense_claim_results
  ADD COLUMN IF NOT EXISTS payment_status TEXT NOT NULL DEFAULT 'unpaid',
  ADD COLUMN IF NOT EXISTS paid_cash_transaction_id UUID REFERENCES cash_transactions(id),
  ADD COLUMN IF NOT EXISTS paid_by UUID REFERENCES users(id),
  ADD COLUMN IF NOT EXISTS paid_at TIMESTAMPTZ;

-- unpaid  : nothing paid yet
-- paid    : the linked posting paid the employee
-- not_due : approved with no cash due to the employee (advance covers it;
--           a negative payable — employee refund — is NOT handled here)
ALTER TABLE expense_claim_results DROP CONSTRAINT IF EXISTS chk_expense_claim_payment_status;
ALTER TABLE expense_claim_results ADD CONSTRAINT chk_expense_claim_payment_status CHECK (
     (payment_status = 'paid' AND paid_cash_transaction_id IS NOT NULL AND paid_by IS NOT NULL AND paid_at IS NOT NULL
        AND review_status = 'confirmed')
  OR (payment_status IN ('unpaid', 'not_due') AND paid_cash_transaction_id IS NULL)
);
