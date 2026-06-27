-- ----------------------------------------------------------------------------
-- G1 — Tenants + Membership
--
-- Multi-tenant foundation. Every existing user becomes a member of the
-- 'default' tenant (created in 12_backfill_default_tenant.sql).
--
-- A user MAY belong to N tenants with different roles per tenant.
-- The "active" tenant is encoded in the JWT claim `tenant_id` issued at
-- login time (or after the user switches tenant via /me/switch-tenant).
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS tenants (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code        TEXT UNIQUE NOT NULL,           -- 'default', 'partner-abc'
    name        TEXT NOT NULL,
    status      TEXT NOT NULL DEFAULT 'active', -- active / suspended / archived
    metadata    JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_tenants_status ON tenants(status);

-- Tenant membership — a user can belong to N tenants with a per-tenant role
CREATE TABLE IF NOT EXISTS tenant_members (
    tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role        TEXT NOT NULL,                  -- tenant_admin / member / viewer
    joined_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_tenant_members_user ON tenant_members(user_id);

-- Auto-bump updated_at
CREATE OR REPLACE FUNCTION tenants_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_tenants_updated_at ON tenants;
CREATE TRIGGER trg_tenants_updated_at
    BEFORE UPDATE ON tenants
    FOR EACH ROW
    EXECUTE FUNCTION tenants_touch_updated_at();
