-- Phase D4: business capability catalog + version-scoped declarations +
-- department capability-to-agent binding.
--
-- Canonical chain (mirrors D3's agent entitlement chain exactly):
--   business_capabilities (platform catalog)
--   -> agent_version_capabilities (immutable declaration on a published version)
--   -> department_capability_bindings (TENANT_OWNER-configured, deterministic routing)
--   -> resolved agent_id -> the existing, unmodified ExecutionManifest pipeline.
--
-- Purely additive: no existing table is altered, no agent is backfilled with
-- any capability (an agent supports zero capabilities until a super_admin
-- explicitly assigns them on a draft version — no silent attach-all).

CREATE TABLE IF NOT EXISTS business_capabilities (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code        TEXT UNIQUE NOT NULL,
    name        TEXT NOT NULL,
    description TEXT,
    category    TEXT,
    is_active   BOOLEAN NOT NULL DEFAULT TRUE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT business_capabilities_code_format
        CHECK (code ~ '^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+$' AND length(code) BETWEEN 3 AND 120),
    CONSTRAINT business_capabilities_name_not_blank CHECK (btrim(name) <> '')
);

CREATE INDEX IF NOT EXISTS idx_business_capabilities_active ON business_capabilities(is_active);
CREATE INDEX IF NOT EXISTS idx_business_capabilities_category ON business_capabilities(category) WHERE category IS NOT NULL;

CREATE OR REPLACE FUNCTION business_capabilities_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_business_capabilities_updated_at ON business_capabilities;
CREATE TRIGGER trg_business_capabilities_updated_at
    BEFORE UPDATE ON business_capabilities
    FOR EACH ROW EXECUTE FUNCTION business_capabilities_touch_updated_at();

-- Seed only capabilities that correspond to real, currently-shipped business
-- modules (gateway/src/Aiq.Gateway/Endpoints/VoucherEndpoints.cs,
-- Customers/, Suppliers/, Employees/, and the accounting/* Web pages) — not
-- speculative future modules.
INSERT INTO business_capabilities (code, name, description, category) VALUES
    ('accounting.journal',   'Định khoản',              'Lập và kiểm tra bút toán kế toán (Nợ/Có) theo TT200.', 'accounting'),
    ('accounting.invoice',   'Xử lý hoá đơn',           'Trích xuất trường và đề xuất bút toán từ hoá đơn.',    'accounting'),
    ('accounting.tax',       'Tính thuế',               'Tính VAT, thuế TNCN, thuế TNDN.',                      'accounting'),
    ('accounting.report',    'Báo cáo tài chính',       'Tạo báo cáo BCKQHĐKD, BCĐKT, LCTT.',                   'accounting'),
    ('voucher.processing',   'Xử lý chứng từ',          'OCR, trích xuất và ghi nhận chứng từ kế toán.',        'voucher'),
    ('expense.processing',   'Xử lý chi phí',           'Ghi nhận và xử lý đề nghị hoàn ứng/chi phí.',          'expense'),
    ('supplier.management',  'Quản lý nhà cung cấp',    'Tra cứu và quản lý thông tin nhà cung cấp.',           'master_data'),
    ('customer.management',  'Quản lý khách hàng',      'Tra cứu và quản lý thông tin khách hàng.',             'master_data')
ON CONFLICT (code) DO NOTHING;

-- ----------------------------------------------------------------------------
-- agent_version_capabilities: immutable once the version is no longer draft.
-- Same shape/behavior as agent_version_tool_grants / agent_version_skill_assignments
-- (010_versioned_agent_capabilities.sql) — reuses that migration's generic
-- protect_published_agent_capability() trigger function unchanged, since it
-- keys off NEW/OLD.agent_version_id and agent_versions.status alone.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS agent_version_capabilities (
    agent_version_id UUID NOT NULL REFERENCES agent_versions(id) ON DELETE CASCADE,
    capability_id    UUID NOT NULL REFERENCES business_capabilities(id) ON DELETE RESTRICT,
    assigned_by      UUID REFERENCES users(id) ON DELETE SET NULL,
    assigned_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (agent_version_id, capability_id)
);

CREATE INDEX IF NOT EXISTS idx_agent_version_capabilities_capability ON agent_version_capabilities(capability_id);

DROP TRIGGER IF EXISTS trg_protect_published_version_capabilities ON agent_version_capabilities;
CREATE TRIGGER trg_protect_published_version_capabilities
BEFORE INSERT OR UPDATE OR DELETE ON agent_version_capabilities
FOR EACH ROW EXECUTE FUNCTION protect_published_agent_capability();

-- A draft may only declare an active capability. This is checked again at
-- the application layer before assignment (clearer error message), but the
-- DB stays fail-closed on its own — a capability deactivated after being
-- wired into a still-unpublished draft cannot silently ride along to publish.
CREATE OR REPLACE FUNCTION enforce_capability_active_on_draft_assignment()
RETURNS TRIGGER AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM business_capabilities WHERE id = NEW.capability_id AND is_active) THEN
        RAISE EXCEPTION 'capability is not active' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_enforce_capability_active_on_draft_assignment ON agent_version_capabilities;
CREATE TRIGGER trg_enforce_capability_active_on_draft_assignment
BEFORE INSERT ON agent_version_capabilities
FOR EACH ROW EXECUTE FUNCTION enforce_capability_active_on_draft_assignment();

-- ----------------------------------------------------------------------------
-- department_capability_bindings: TENANT_OWNER-configured deterministic
-- routing. History-preserving, at most one active binding per
-- (department_id, capability_id) — same "status flip, never delete" shape as
-- agent_department_assignments (021).
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS department_capability_bindings (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    department_id   UUID NOT NULL,
    capability_id   UUID NOT NULL REFERENCES business_capabilities(id) ON DELETE RESTRICT,
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    status          TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'revoked')),
    assigned_by     UUID REFERENCES users(id) ON DELETE SET NULL,
    assigned_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revoked_at      TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_department_capability_bindings_revoked_at
        CHECK ((status = 'active' AND revoked_at IS NULL) OR (status = 'revoked' AND revoked_at IS NOT NULL)),
    -- Same-tenant department membership, structurally (mirrors users.department_id
    -- and agent_department_assignments.department_id's own composite FK).
    CONSTRAINT fk_department_capability_bindings_department_tenant
        FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_department_capability_bindings_active
    ON department_capability_bindings(department_id, capability_id)
    WHERE status = 'active';

CREATE INDEX IF NOT EXISTS idx_department_capability_bindings_tenant_department
    ON department_capability_bindings(tenant_id, department_id, status);

CREATE OR REPLACE FUNCTION department_capability_bindings_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_department_capability_bindings_updated_at ON department_capability_bindings;
CREATE TRIGGER trg_department_capability_bindings_updated_at
    BEFORE UPDATE ON department_capability_bindings
    FOR EACH ROW EXECUTE FUNCTION department_capability_bindings_touch_updated_at();

-- Defense in depth (the endpoint validates this too, see CapabilityEndpoints):
-- an active binding requires the agent to already carry an active DEPARTMENT
-- entitlement for this exact (tenant, department) AND its currently
-- published version must declare this exact capability. Mirrors
-- enforce_agent_department_assignment_tenant_entitlement (021).
CREATE OR REPLACE FUNCTION enforce_department_capability_binding_eligibility()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.status = 'active' THEN
        IF NOT EXISTS (
            SELECT 1 FROM agent_department_assignments ada
            WHERE ada.agent_id = NEW.agent_id AND ada.tenant_id = NEW.tenant_id
              AND ada.department_id = NEW.department_id AND ada.status = 'active'
        ) THEN
            RAISE EXCEPTION 'active capability binding requires an active department agent assignment' USING ERRCODE = '23514';
        END IF;
        IF NOT EXISTS (
            SELECT 1 FROM agents a
            JOIN agent_versions v ON v.id = a.published_version_id
            JOIN agent_version_capabilities avc ON avc.agent_version_id = v.id
            WHERE a.id = NEW.agent_id AND v.status = 'published' AND avc.capability_id = NEW.capability_id
        ) THEN
            RAISE EXCEPTION 'active capability binding requires the agent''s published version to declare this capability' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_enforce_department_capability_binding_eligibility ON department_capability_bindings;
CREATE TRIGGER trg_enforce_department_capability_binding_eligibility
BEFORE INSERT OR UPDATE ON department_capability_bindings
FOR EACH ROW EXECUTE FUNCTION enforce_department_capability_binding_eligibility();

COMMENT ON TABLE business_capabilities IS
    'Phase D4: platform catalog of business capabilities ("nghiệp vụ") — stable, machine-readable codes; version-scoped declaration lives in agent_version_capabilities.';
COMMENT ON TABLE agent_version_capabilities IS
    'Phase D4: immutable-once-published declaration of which business capabilities a specific agent version supports.';
COMMENT ON TABLE department_capability_bindings IS
    'Phase D4: TENANT_OWNER-configured deterministic capability->agent routing within one department. One active binding per (department, capability).';
