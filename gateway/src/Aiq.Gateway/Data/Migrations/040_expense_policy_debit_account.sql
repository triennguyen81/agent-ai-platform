-- P1 audit fix (ACCAGENT_PROCESS_AUDIT_REPORT.md — journal-entry proposal
-- ignores classified outcome) — expense-settlement journal routing needs a
-- category -> GL debit account mapping to know which of 641/642/627
-- recognizes a given expense category. No such mapping existed anywhere,
-- and inventing one would be fabricating a business rule. This adds a
-- nullable configuration column, admin-set per department+category, same
-- posture as limit_amount on this same table: unset by default, used only
-- when a real value has been configured.
ALTER TABLE department_expense_policies
  ADD COLUMN IF NOT EXISTS debit_account TEXT;
