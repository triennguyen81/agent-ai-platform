-- Receipt/payment vouchers uploaded under the wrong type (the upload picker
-- used to default to "Hoá đơn bán" = invoice_vat_out) kept that label even
-- when extraction clearly read a PHIẾU CHI / PHIẾU THU / ỦY NHIỆM CHI —
-- ReplaceExtractedAsync never wrote voucher_type back. Such a voucher never
-- reached the cash-settlement flow (no "Duyệt & ghi nhận").
--
-- Same two-signal rule as CashSettlementRules.ResolveExtractedVoucherType:
-- the extractor's voucher_type is a cash type AND the OCR text carries that
-- document title. Only still-unposted rows ('extracted', no posting) are
-- touched; the original label is preserved in extracted_fields.

UPDATE vouchers v
SET voucher_type = v.extracted_fields->>'voucher_type',
    extracted_fields = v.extracted_fields || jsonb_build_object('voucher_type_uploaded_as', v.voucher_type)
WHERE v.status = 'extracted'
  AND v.extracted_fields->>'voucher_type' IN ('receipt', 'payment', 'bank_transfer')
  AND v.voucher_type <> v.extracted_fields->>'voucher_type'
  AND v.voucher_type NOT IN ('receipt', 'payment', 'bank_transfer')
  AND NOT EXISTS (SELECT 1 FROM cash_transactions c WHERE c.voucher_id = v.id)
  AND (
    (v.extracted_fields->>'voucher_type' = 'bank_transfer'
       AND (upper(v.raw_ocr_text) LIKE '%ỦY NHIỆM CHI%' OR upper(v.raw_ocr_text) LIKE '%UỶ NHIỆM CHI%' OR upper(v.raw_ocr_text) LIKE '%UY NHIEM CHI%'))
    OR (v.extracted_fields->>'voucher_type' = 'payment'
       AND (upper(v.raw_ocr_text) LIKE '%PHIẾU CHI%' OR upper(v.raw_ocr_text) LIKE '%PHIEU CHI%')
       AND NOT (upper(v.raw_ocr_text) LIKE '%PHIẾU THU%' OR upper(v.raw_ocr_text) LIKE '%PHIEU THU%'))
    OR (v.extracted_fields->>'voucher_type' = 'receipt'
       AND (upper(v.raw_ocr_text) LIKE '%PHIẾU THU%' OR upper(v.raw_ocr_text) LIKE '%PHIEU THU%')
       AND NOT (upper(v.raw_ocr_text) LIKE '%PHIẾU CHI%' OR upper(v.raw_ocr_text) LIKE '%PHIEU CHI%'))
  );
