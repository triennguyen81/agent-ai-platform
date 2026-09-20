-- R07/R08 (business remediation) — OVERDUE_UNPAID was declared in
-- receipt_match_results' outcome CHECK constraint (033_receipt_ar_match.sql)
-- but never assignable by ProposeMatchAsync: it classifies a RECEIPT
-- (money that arrived), so there is no receipt event to trigger a
-- "still unpaid" classification on. The correct mechanism (matching the
-- business document's own framing — "Quá hạn không thu được... Leo thang
-- theo bậc: nhắc lần 1 → công văn → tính lãi phạt → chuyển pháp chế" — a
-- time-based escalation independent of any incoming payment) is a
-- scheduled sweep over open sim_ar_ledger invoices, same shape as the
-- existing ContractDeadlineJob. Removing the dead enum value rather than
-- leaving a permanently-unreachable one.
ALTER TABLE receipt_match_results DROP CONSTRAINT IF EXISTS chk_receipt_match_outcome;
DO $$ BEGIN
  ALTER TABLE receipt_match_results ADD CONSTRAINT chk_receipt_match_outcome
    CHECK (outcome IN (
      'FULL_MATCH','PARTIAL_PAYMENT','OVERPAYMENT','ADVANCE_PAYMENT',
      'UNKNOWN_PAYER','CUSTOMER_NO_OPEN_RECEIVABLE','REVERSED_TRANSACTION',
      'FOREIGN_CURRENCY'
    ));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
