-- Multi-file Expense Claim intake: ONE claim -> MANY bill items, ONE FILE = ONE ITEM.
--
-- The claim stays what it already is: one vouchers row (voucher_type =
-- 'expense_claim') anchoring expense_claim_results, the review stages,
-- authoritative recognition (ExpenseRecognition) and payment
-- (CashSettlementExpenseClaimSource) — all unchanged. A multi-file claim is
-- a claim voucher WITHOUT a source file, plus:
--   expense_claim_intakes     structured claim metadata (employee, trip)
--   expense_claim_items       one row per uploaded bill file: the original
--                             file, per-item OCR/extraction (+ provenance),
--                             the extracted values, the human-confirmed
--                             values, review state and exclusion
--   expense_claim_item_events item-level audit trail
-- Item changes feed the EXISTING ExpenseClaimRepository.ProposeAsync, so the
-- claim total / VAT / advance / payable are always server-derived.
-- Legacy single-document claims have no intake row and behave exactly as
-- before; nothing is back-filled.

CREATE TABLE IF NOT EXISTS expense_claim_intakes (
    claim_voucher_id UUID PRIMARY KEY REFERENCES vouchers(id) ON DELETE CASCADE,
    tenant_id        UUID NOT NULL,
    department_id    UUID NOT NULL,
    employee_id      UUID NOT NULL REFERENCES sim_employees(id),
    purpose          TEXT,
    trip_start       DATE,
    trip_end         DATE,
    request_id       TEXT,
    created_by       UUID NOT NULL REFERENCES users(id),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CHECK (trip_start IS NULL OR trip_end IS NULL OR trip_end >= trip_start)
);

-- A repeated submit of the same browser request never creates a second claim.
CREATE UNIQUE INDEX IF NOT EXISTS uq_expense_claim_intake_request
    ON expense_claim_intakes (tenant_id, created_by, request_id) WHERE request_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS expense_claim_items (
    id                         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id                  UUID NOT NULL,
    department_id              UUID NOT NULL,
    claim_voucher_id           UUID NOT NULL REFERENCES expense_claim_intakes(claim_voucher_id) ON DELETE CASCADE,
    sequence_no                INT  NOT NULL CHECK (sequence_no >= 1),
    request_id                 TEXT,
    client_index               INT,
    -- the original bill file (never deleted, also when excluded)
    object_key                 TEXT NOT NULL,
    original_filename          TEXT NOT NULL,
    content_type               TEXT NOT NULL,
    size_bytes                 BIGINT NOT NULL CHECK (size_bytes > 0),
    sha256                     TEXT NOT NULL,
    status                     TEXT NOT NULL DEFAULT 'processing'
                               CHECK (status IN ('processing', 'ready', 'needs_review', 'extraction_failed', 'excluded')),
    review_reasons             TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    attempt                    INT NOT NULL DEFAULT 1 CHECK (attempt >= 1),
    -- per-item OCR + extraction (values proposed by the model)
    raw_ocr_text               TEXT,
    ocr_confidence             NUMERIC(5,4),
    extracted_data             JSONB,
    extraction_confidence      NUMERIC(5,4),
    extracted_category         TEXT,
    extracted_merchant_name    TEXT,
    extracted_merchant_tax_code TEXT,
    extracted_document_number  TEXT,
    extracted_document_date    DATE,
    extracted_gross_amount     NUMERIC(18,2),
    extracted_vat_amount       NUMERIC(18,2),
    extracted_currency         TEXT,
    extracted_description      TEXT,
    -- human-confirmed values (ADMIN); when set they are the item's values
    confirmed_category         TEXT,
    confirmed_merchant_name    TEXT,
    confirmed_merchant_tax_code TEXT,
    confirmed_document_number  TEXT,
    confirmed_document_date    DATE,
    confirmed_gross_amount     NUMERIC(18,2) CHECK (confirmed_gross_amount IS NULL OR confirmed_gross_amount > 0),
    confirmed_vat_amount       NUMERIC(18,2) CHECK (confirmed_vat_amount IS NULL OR confirmed_vat_amount >= 0),
    confirmed_currency         TEXT,
    confirmed_description      TEXT,
    confirmed_by               UUID REFERENCES users(id),
    confirmed_at               TIMESTAMPTZ,
    -- exclusion (kept for audit; never counted)
    excluded_reason            TEXT,
    excluded_by                UUID REFERENCES users(id),
    excluded_at                TIMESTAMPTZ,
    -- processing provenance (same facts the voucher pipeline records)
    agent_id                   UUID,
    agent_version_id           UUID,
    agent_version              INT,
    config_source              TEXT CHECK (config_source IS NULL OR config_source IN ('manifest', 'legacy')),
    provider                   TEXT,
    model                      TEXT,
    error_code                 TEXT,
    error_stage                TEXT,
    processed_at               TIMESTAMPTZ,
    created_by                 UUID NOT NULL REFERENCES users(id),
    created_at                 TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at                 TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (claim_voucher_id, sequence_no),
    CHECK ((status = 'excluded') = (excluded_at IS NOT NULL)),
    CHECK (status <> 'excluded' OR (excluded_by IS NOT NULL AND length(trim(coalesce(excluded_reason, ''))) > 0)),
    CHECK ((confirmed_by IS NULL) = (confirmed_at IS NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_expense_claim_item_request
    ON expense_claim_items (claim_voucher_id, request_id, client_index) WHERE request_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_expense_claim_items_claim ON expense_claim_items (claim_voucher_id, sequence_no);
CREATE INDEX IF NOT EXISTS idx_expense_claim_items_sha ON expense_claim_items (tenant_id, sha256);
CREATE INDEX IF NOT EXISTS idx_expense_claim_items_invoice
    ON expense_claim_items (tenant_id, extracted_merchant_tax_code, extracted_document_number);

CREATE TABLE IF NOT EXISTS expense_claim_item_events (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id        UUID NOT NULL,
    claim_voucher_id UUID NOT NULL,
    item_id          UUID NOT NULL REFERENCES expense_claim_items(id) ON DELETE CASCADE,
    actor_id         UUID,
    event            TEXT NOT NULL,
    details          JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_expense_claim_item_events_item ON expense_claim_item_events (item_id, created_at);

-- Per-item processing runs share the canonical audit (voucher_id = the claim voucher).
ALTER TABLE document_processing_runs
    ADD COLUMN IF NOT EXISTS expense_claim_item_id UUID REFERENCES expense_claim_items(id) ON DELETE CASCADE;
