-- AIQ's own legal-entity identity per tenant ("công ty chủ thể"). Strictly
-- 1:1 with tenants — tenant_id is the primary key, never a surrogate id, so
-- "one profile per tenant" is a structural invariant, not an app-level rule.
--
-- Deliberately a separate table from tenants.company_tax_id (see
-- ITenantRepository.GetCompanyTaxIdAsync/SetCompanyTaxIdAsync, wired into
-- InvoiceDirectionResolver's AR/AP direction check) — that narrower,
-- already-load-bearing field is untouched by this migration. This table is
-- the broader identity AIQ's own AI runtime uses as trusted company
-- context; the two are allowed to diverge and reconciling them is out of
-- scope here.
CREATE TABLE IF NOT EXISTS tenant_company_profiles (
    tenant_id             UUID PRIMARY KEY REFERENCES tenants(id) ON DELETE CASCADE,
    legal_name            TEXT,
    tax_code              TEXT,
    short_name            TEXT,
    aliases               TEXT[] NOT NULL DEFAULT '{}',
    registered_address    TEXT,
    legal_representative  TEXT,
    representative_title  TEXT,
    phone                 TEXT,
    email                 TEXT,
    website               TEXT,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_by            UUID REFERENCES users(id) ON DELETE SET NULL
);

CREATE OR REPLACE FUNCTION tenant_company_profiles_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_tenant_company_profiles_updated_at ON tenant_company_profiles;
CREATE TRIGGER trg_tenant_company_profiles_updated_at
    BEFORE UPDATE ON tenant_company_profiles
    FOR EACH ROW EXECUTE FUNCTION tenant_company_profiles_touch_updated_at();

COMMENT ON TABLE tenant_company_profiles IS
    'AIQ''s own legal-entity identity for a tenant ("công ty chủ thể") — TENANT_OWNER-managed, read by the AI runtime as trusted company context. tenant_id is the primary key: exactly zero or one row per tenant.';
