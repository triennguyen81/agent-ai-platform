-- R15 (business remediation) — VAT-deductible and CIT-deductible are two
-- separate concepts per the business document ("Hai cờ luôn đi kèm dòng chi
-- phí": VAT-deductible, CIT-deductible — never one combined value). The
-- existing `non_deductible_amount` column already behaves as the
-- CIT-non-deductible overlay on approved_amount (an item without an
-- invoice stays part of the approved/recognized expense but is flagged
-- non-deductible for CIT, matching the worked example in the business doc
-- — the taxi line is both "Đạt" approved AND "Không được trừ"). What was
-- missing is VAT: `vat_amount` (identified from extraction, never
-- fabricated — 0 if the source document didn't carry a VAT breakdown) and
-- `vat_deductible_amount` (the portion of vat_amount that actually
-- qualifies for input-VAT credit: only items with a real invoice number,
-- prorated down if the line was policy-capped).
ALTER TABLE expense_claim_results
  ADD COLUMN IF NOT EXISTS vat_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS vat_deductible_amount NUMERIC(18,2) NOT NULL DEFAULT 0;
