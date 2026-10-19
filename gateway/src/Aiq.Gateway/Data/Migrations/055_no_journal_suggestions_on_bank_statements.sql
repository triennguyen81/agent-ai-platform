-- A bank statement is N transactions, not one accounting entry; in this
-- phase statement lines are identified (customer/supplier) only and must
-- never lead to journals. The generic extraction prompt still proposed
-- per-line entries; any still-pending suggestion on a statement voucher is
-- closed as rejected (kept as history, never deleted). Accepted entries, if
-- any, are left untouched for an accountant to review.
UPDATE voucher_journal_suggestions j
   SET rejected_at = NOW(), reject_reason = 'bank_statement_identification_only'
  FROM vouchers v
 WHERE v.id = j.voucher_id AND v.voucher_type = 'bank_statement'
   AND j.accepted_at IS NULL AND j.rejected_at IS NULL;
