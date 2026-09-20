-- P2 audit fix (ACCAGENT_PROCESS_AUDIT_REPORT.md — late-payment penalty
-- never computed despite contracts.penalty_percent already being captured).
-- Persisted per the audit's own required fields: overdue amount, overdue
-- days, rate (copied at calculation time, so a later edit to the contract's
-- penalty_percent never silently rewrites history), penalty amount, and
-- the calculation date.
ALTER TABLE contract_payment_schedules
  ADD COLUMN IF NOT EXISTS penalty_overdue_amount NUMERIC(18, 2),
  ADD COLUMN IF NOT EXISTS penalty_overdue_days INT,
  ADD COLUMN IF NOT EXISTS penalty_rate NUMERIC(9, 4),
  ADD COLUMN IF NOT EXISTS penalty_amount NUMERIC(18, 2),
  ADD COLUMN IF NOT EXISTS penalty_calculated_at TIMESTAMPTZ;
