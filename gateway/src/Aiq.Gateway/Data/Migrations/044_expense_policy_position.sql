-- P2 audit fix (ACCAGENT_PROCESS_AUDIT_REPORT.md — expense policy engine
-- only scoped by department+category, not by employee rank, contradicting
-- the business process document's own worked example: "Định mức 1,5tr/đêm
-- cấp chuyên viên" implies different limits per position). sim_employees
-- already has a `position` column (e.g. "Chuyên viên", "Trưởng phòng") —
-- this just lets a policy target it. NULL = applies to every position,
-- same wildcard convention already used by `category`.
ALTER TABLE department_expense_policies
  ADD COLUMN IF NOT EXISTS position TEXT;
