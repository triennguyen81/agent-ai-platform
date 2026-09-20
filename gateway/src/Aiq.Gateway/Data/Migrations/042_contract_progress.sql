-- P2 audit fix (ACCAGENT_PROCESS_AUDIT_REPORT.md — contract progress %
-- missing, milestones never auto-activate). The business process document's
-- own worked example shows paying an installment recomputing the contract's
-- progress percentage and activating the next milestone; neither existed.
-- progress_percent is a pure, idempotent recompute (SUM of paid
-- installments' percentage share) — never an increment — so it is always
-- safe to recompute on any retry.
ALTER TABLE contracts
  ADD COLUMN IF NOT EXISTS progress_percent NUMERIC(5, 2) NOT NULL DEFAULT 0;
