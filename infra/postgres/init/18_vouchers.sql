-- ----------------------------------------------------------------------------
-- Vouchers module — Phase 1 (Foundation: hoá đơn VAT)
--
-- Idempotent: safe to re-run. Three tables:
--   vouchers                     — one row per uploaded chứng từ
--   voucher_line_items           — line items on hoá đơn (mỗi dòng SP/DV)
--   voucher_journal_suggestions  — bút toán suggest theo TT200 (Nợ/Có)
--
-- voucher_type covers 4 kinds (Phase 1 chỉ populate 'invoice_vat'):
--   invoice_vat / receipt / payment / bank_transfer / bank_statement
--
-- Pipeline: user upload → OCR → extract → status='extracted' →
--           suggest_journal → user accept → xuất Excel để import ERP.
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS vouchers (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    tenant_id           UUID REFERENCES tenants(id) ON DELETE SET NULL,

    voucher_type        TEXT NOT NULL,       -- invoice_vat / receipt / payment / bank_transfer / bank_statement
    status              TEXT NOT NULL DEFAULT 'processing',
                        -- processing / extracted / journalized / verified / rejected

    -- Document identifiers
    document_number     TEXT,                -- Số hoá đơn / phiếu / lệnh chi
    document_series     TEXT,                -- Ký hiệu hoá đơn (VD: 1C25TAA)
    document_date       DATE,

    -- Parties
    seller_name         TEXT,
    seller_mst          TEXT,                -- Mã số thuế bên bán (10 hoặc 13 số)
    seller_address      TEXT,
    buyer_name          TEXT,
    buyer_mst           TEXT,
    buyer_address       TEXT,

    -- Amounts (aggregated — line items có breakdown chi tiết)
    subtotal            NUMERIC(18, 2),      -- Cộng tiền hàng trước VAT
    vat_rate            NUMERIC(5, 2),       -- 0 / 5 / 8 / 10 (nếu mixed thì để NULL, xem line_items)
    vat_amount          NUMERIC(18, 2),
    discount_amount     NUMERIC(18, 2),
    total_amount        NUMERIC(18, 2),      -- Tổng cộng thanh toán
    currency            TEXT NOT NULL DEFAULT 'VND',
    amount_in_words     TEXT,                -- Số tiền bằng chữ

    -- Payment
    payment_method      TEXT,                -- CK / TM / MIXED

    -- TCT verification (optional)
    tct_verified        BOOLEAN NOT NULL DEFAULT FALSE,
    tct_verified_at     TIMESTAMPTZ,
    tct_response        JSONB,               -- Raw TCT response for audit

    -- Source
    source_file_path    TEXT,                -- Path in /var/aiq/uploads
    source_file_name    TEXT,                -- Original filename
    source_mime_type    TEXT,                -- image/jpeg, image/png, application/pdf

    -- OCR + extract output
    raw_ocr_text        TEXT,
    ocr_confidence      NUMERIC(3, 2),       -- 0.00 - 1.00
    extracted_fields    JSONB NOT NULL DEFAULT '{}'::jsonb,
                        -- Full LLM extract output preserved for nuance / edge fields

    -- Error tracking
    error_message       TEXT,                -- Populated nếu status='rejected'

    tags                TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_vouchers_user_status ON vouchers(user_id, status);
CREATE INDEX IF NOT EXISTS idx_vouchers_tenant ON vouchers(tenant_id) WHERE tenant_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_vouchers_seller_mst ON vouchers(seller_mst) WHERE seller_mst IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_vouchers_type_date ON vouchers(voucher_type, document_date DESC NULLS LAST);
CREATE INDEX IF NOT EXISTS idx_vouchers_doc_number ON vouchers(document_number) WHERE document_number IS NOT NULL;

-- Deduplication: same document_number + seller_mst + date → same voucher
-- (Partial unique — only enforced when all 3 fields populated to allow
-- in-flight rows with NULL fields during processing.)
CREATE UNIQUE INDEX IF NOT EXISTS uniq_voucher_document
    ON vouchers(tenant_id, seller_mst, document_number, document_date)
    WHERE tenant_id IS NOT NULL
      AND seller_mst IS NOT NULL
      AND document_number IS NOT NULL
      AND document_date IS NOT NULL;

-- ----------------------------------------------------------------------------
-- Line items (mỗi dòng sản phẩm/dịch vụ trên hoá đơn)
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS voucher_line_items (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    voucher_id      UUID NOT NULL REFERENCES vouchers(id) ON DELETE CASCADE,
    ordinal         INT NOT NULL,             -- STT dòng trên hoá đơn (1, 2, 3...)
    item_code       TEXT,                     -- Mã sản phẩm nếu có
    description     TEXT NOT NULL,            -- Tên hàng hoá / dịch vụ
    unit            TEXT,                     -- Đơn vị tính (cái, kg, tấn, giờ...)
    quantity        NUMERIC(18, 4),
    unit_price      NUMERIC(18, 2),
    subtotal        NUMERIC(18, 2),           -- Thành tiền trước VAT
    vat_rate        NUMERIC(5, 2),
    vat_amount      NUMERIC(18, 2),
    total_amount    NUMERIC(18, 2),           -- Thành tiền sau VAT
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_line_items_voucher ON voucher_line_items(voucher_id, ordinal);

-- ----------------------------------------------------------------------------
-- Journal suggestions — bút toán N/C theo TT200
-- Một voucher có thể có nhiều bút toán (VD hoá đơn VAT đầu vào: 2 bút toán
-- Nợ 156 + Nợ 1331 + Có 331). Mỗi bút toán 1 row.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS voucher_journal_suggestions (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    voucher_id      UUID NOT NULL REFERENCES vouchers(id) ON DELETE CASCADE,
    entry_no        INT NOT NULL,             -- Số thứ tự bút toán trong voucher (1, 2, 3)
    debit_account   TEXT NOT NULL,            -- TK Nợ (VD: 156, 1331, 641, 6427)
    credit_account  TEXT NOT NULL,            -- TK Có (VD: 331, 111, 112)
    amount          NUMERIC(18, 2) NOT NULL,
    description     TEXT,                     -- Diễn giải bút toán
    confidence      NUMERIC(3, 2),            -- LLM confidence 0.00-1.00
    accepted_by     UUID REFERENCES users(id) ON DELETE SET NULL,
    accepted_at     TIMESTAMPTZ,              -- User confirm timestamp
    rejected_at     TIMESTAMPTZ,              -- User reject timestamp
    reject_reason   TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_journal_voucher ON voucher_journal_suggestions(voucher_id, entry_no);
CREATE INDEX IF NOT EXISTS idx_journal_pending ON voucher_journal_suggestions(voucher_id)
    WHERE accepted_at IS NULL AND rejected_at IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uniq_journal_entry_no
    ON voucher_journal_suggestions(voucher_id, entry_no);

-- ----------------------------------------------------------------------------
-- Auto-bump updated_at on vouchers table
-- ----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION vouchers_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_vouchers_updated_at ON vouchers;
CREATE TRIGGER trg_vouchers_updated_at
    BEFORE UPDATE ON vouchers
    FOR EACH ROW
    EXECUTE FUNCTION vouchers_touch_updated_at();
