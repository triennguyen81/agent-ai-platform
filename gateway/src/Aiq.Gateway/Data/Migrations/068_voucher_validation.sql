-- Chứng từ flow v2 PR2 (docs/flows/chung-tu-proposed.md §3.3, §4.1, QĐ 7, 8, 11).
-- Additive only. Down: Data/Migrations/down/068_voucher_validation.down.sql (run by hand).
--
-- Validation results live in their own table, not on vouchers: writing a
-- result must not bump vouchers.updated_at (trigger vouchers_touch_updated_at)
-- nor touch a posted voucher's row.

-- VAT rates by validity window, looked up by the INVOICE date (QĐ 11). rate is
-- a percent; NULL for non-numeric categories (KCT, KKKNT). Rates are data, never
-- code: extending or ending a reduced rate is an INSERT/UPDATE here.
CREATE TABLE IF NOT EXISTS vat_rates (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code           TEXT NOT NULL,
    rate           NUMERIC(5,2),
    effective_from DATE NOT NULL,
    effective_to   DATE,
    legal_ref      TEXT,
    note           TEXT,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT vat_rates_window_check CHECK (effective_to IS NULL OR effective_to >= effective_from),
    CONSTRAINT vat_rates_rate_check CHECK (rate IS NULL OR rate >= 0),
    CONSTRAINT vat_rates_code_from_key UNIQUE (code, effective_from)
);

-- Seed: standard rates + the approved 8% window. Everything except the 8% row
-- awaits the accountant's review (QĐ 11); earlier reduced-rate windows are not
-- seeded on purpose — add them as rows once confirmed.
INSERT INTO vat_rates (code, rate, effective_from, effective_to, legal_ref, note) VALUES
    ('0',     0,    '2009-01-01', NULL,         'Luật Thuế GTGT', 'chờ kế toán duyệt'),
    ('5',     5,    '2009-01-01', NULL,         'Luật Thuế GTGT', 'chờ kế toán duyệt'),
    ('10',    10,   '2009-01-01', NULL,         'Luật Thuế GTGT', 'chờ kế toán duyệt'),
    ('8',     8,    '2025-07-01', '2026-12-31', 'NĐ 174/2025/NĐ-CP', 'giảm thuế GTGT — QĐ 11'),
    ('KCT',   NULL, '2009-01-01', NULL,         'Luật Thuế GTGT', 'không chịu thuế — chờ kế toán duyệt'),
    ('KKKNT', NULL, '2009-01-01', NULL,         'Luật Thuế GTGT', 'không kê khai, tính nộp — chờ kế toán duyệt')
ON CONFLICT (code, effective_from) DO NOTHING;

-- Per-tenant validator settings (QĐ 7, 8). No row = defaults (0.80, built-in lists).
-- required_fields: {"<voucher_type>": ["field", ...]} replaces that type's list.
CREATE TABLE IF NOT EXISTS tenant_voucher_settings (
    tenant_id            UUID PRIMARY KEY REFERENCES tenants(id) ON DELETE CASCADE,
    confidence_threshold NUMERIC(3,2) NOT NULL DEFAULT 0.80,
    required_fields      JSONB,
    updated_by           UUID REFERENCES users(id) ON DELETE SET NULL,
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT tenant_voucher_settings_threshold_check CHECK (confidence_threshold >= 0 AND confidence_threshold <= 1),
    CONSTRAINT tenant_voucher_settings_required_check CHECK (required_fields IS NULL OR jsonb_typeof(required_fields) = 'object')
);

-- Latest validator result per voucher (advisory in PR2 — nothing blocks on it).
CREATE TABLE IF NOT EXISTS voucher_validations (
    voucher_id        UUID PRIMARY KEY REFERENCES vouchers(id) ON DELETE CASCADE,
    tenant_id         UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    validator_version INTEGER NOT NULL,
    has_errors        BOOLEAN NOT NULL,
    issues            JSONB NOT NULL,
    validated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT voucher_validations_issues_check CHECK (jsonb_typeof(issues) = 'array')
);
CREATE INDEX IF NOT EXISTS idx_voucher_validations_tenant_errors
    ON voucher_validations (tenant_id) WHERE has_errors;
