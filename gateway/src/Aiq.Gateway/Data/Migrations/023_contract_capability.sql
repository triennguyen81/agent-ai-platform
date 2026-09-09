-- Phase D6: business capability catalog is additive, not a fixed migration
-- 022 snapshot. Contract extraction and checklist generation are a real,
-- currently-shipped business workflow (web/src/app/api/contracts/upload,
-- web/src/app/api/contracts/[id]/checklist/generate) that no capability in
-- 022's seed covers — the closest existing codes (accounting.*,
-- voucher.processing, expense.processing, supplier/customer.management)
-- are all genuinely different business responsibilities. Extraction-on-
-- upload and checklist-generation are kept as ONE capability rather than
-- two: both operate on the same resource (a contract) for the same
-- responsibility (AI-assisted contract management), matching this
-- codebase's existing granularity precedent (voucher.processing covers
-- OCR + extraction + journal-suggestion as one code).
INSERT INTO business_capabilities (code, name, description, category) VALUES
    ('contract.processing', 'Xử lý hợp đồng', 'Trích xuất điều khoản và tạo checklist tuân thủ từ hợp đồng.', 'contract')
ON CONFLICT (code) DO NOTHING;
