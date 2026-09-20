-- R22 (business remediation) — sim_expense_claims (migration 020) has been
-- confirmed legacy/unused: no department_id, not read by any query, and its
-- one remaining write (an opportunistic status='paid' UPDATE inside
-- EmployeeRepository.ApplyExpenseClaimAsync) has been removed. The real,
-- canonical Luồng 03 claim record is `expense_claim_results` (migration
-- 034), keyed to the actual `vouchers` row. Safe to drop — no other table
-- references it (no FKs point at sim_expense_claims).
DROP TABLE IF EXISTS sim_expense_claims;
