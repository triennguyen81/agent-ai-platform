-- Audit what the receipt/payment DOCUMENT said it pays next to what was
-- actually applied (payment_applications.match_method = 'admin_selected'
-- when ADMIN chose a different invoice), and close the Luồng 02 pending
-- state for vouchers that are already posted through cash settlement.

ALTER TABLE cash_transactions ADD COLUMN IF NOT EXISTS document_reference TEXT;

UPDATE cash_transactions c
SET document_reference = NULLIF(concat_ws(', ',
      NULLIF(v.extracted_fields->>'invoice_number', ''),
      NULLIF(v.extracted_fields->>'related_invoice_number', ''),
      NULLIF(v.extracted_fields->>'linked_invoice_number', ''),
      NULLIF(v.extracted_fields->>'reference_document', ''),
      NULLIF(v.extracted_fields->>'invoice_reference', '')), '')
FROM vouchers v
WHERE v.id = c.voucher_id AND c.document_reference IS NULL;

UPDATE receipt_match_results r
SET review_status = 'confirmed', reviewed_by = c.posted_by, reviewed_at = c.posted_at, updated_at = NOW()
FROM cash_transactions c
WHERE c.voucher_id = r.voucher_id AND c.status = 'posted' AND r.review_status = 'proposed';
