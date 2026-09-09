-- Phase D3: department-level agent entitlement.
--
-- Canonical chain: Platform Agent -> Tenant Entitlement (agent_tenant_assignments)
-- -> Department Assignment (this table) -> [Admin execution | User Assignment (agent_assignments) -> User execution].
--
-- Structurally mirrors agent_tenant_assignments / agent_assignments: history
-- preserving, at most one active row per (department_id, agent_id), a
-- BEFORE INSERT/UPDATE trigger requiring an active parent tenant
-- entitlement before an active department assignment can exist (same
-- pattern as enforce_agent_assignment_tenant in 002/011).

CREATE TABLE IF NOT EXISTS agent_department_assignments (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    department_id   UUID NOT NULL,
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    status          TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'revoked')),
    assigned_by     UUID REFERENCES users(id) ON DELETE SET NULL,
    assigned_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revoked_at      TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_agent_department_assignments_revoked_at
        CHECK ((status = 'active' AND revoked_at IS NULL) OR (status = 'revoked' AND revoked_at IS NOT NULL)),
    -- Same-tenant department membership, structurally: a department can only
    -- ever be paired with the tenant it actually belongs to (mirrors
    -- users.department_id's own composite FK from migration 019).
    CONSTRAINT fk_agent_department_assignments_department_tenant
        FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_agent_department_assignments_active
    ON agent_department_assignments(department_id, agent_id)
    WHERE status = 'active';

CREATE INDEX IF NOT EXISTS idx_agent_department_assignments_tenant_department
    ON agent_department_assignments(tenant_id, department_id, status);

CREATE INDEX IF NOT EXISTS idx_agent_department_assignments_agent
    ON agent_department_assignments(agent_id, status);

CREATE OR REPLACE FUNCTION agent_department_assignments_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_agent_department_assignments_updated_at ON agent_department_assignments;
CREATE TRIGGER trg_agent_department_assignments_updated_at
    BEFORE UPDATE ON agent_department_assignments
    FOR EACH ROW EXECUTE FUNCTION agent_department_assignments_touch_updated_at();

-- Mirrors enforce_agent_assignment_tenant (002/011): an active department
-- assignment requires the agent to already carry an active TENANT
-- entitlement for the SAME tenant. A revoked row is historical and never
-- checked — only newly-active rows are gated, so revoking the parent tenant
-- entitlement later does not retroactively fail this constraint (the
-- revoke path below explicitly cascades instead).
CREATE OR REPLACE FUNCTION enforce_agent_department_assignment_tenant_entitlement()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.status = 'active' AND NOT EXISTS (
        SELECT 1 FROM agent_tenant_assignments ata
        WHERE ata.agent_id = NEW.agent_id AND ata.tenant_id = NEW.tenant_id AND ata.status = 'active'
    ) THEN
        RAISE EXCEPTION 'active department assignment requires an active agent tenant entitlement' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_enforce_agent_department_assignment_tenant_entitlement ON agent_department_assignments;
CREATE TRIGGER trg_enforce_agent_department_assignment_tenant_entitlement
BEFORE INSERT OR UPDATE ON agent_department_assignments
FOR EACH ROW EXECUTE FUNCTION enforce_agent_department_assignment_tenant_entitlement();

-- ----------------------------------------------------------------------------
-- Backfill: preserve every existing USER's current access.
--
-- For every currently ACTIVE agent_assignments row (tenant, agent, user)
-- where the tenant entitlement is also active and the user has a valid
-- active department in that same tenant, ensure a matching active
-- agent_department_assignments row exists. Admin assignment rows are
-- included too (same query, role is not filtered) -- an admin's existing
-- direct assignment upgrades into department entitlement rather than being
-- silently dropped, satisfying "preserve existing direct ADMIN
-- assignments... but runtime should no longer require them." Cross-tenant
-- or otherwise invalid combinations (inactive tenant entitlement, no/invalid
-- department) are skipped, never fabricated.
-- ----------------------------------------------------------------------------
INSERT INTO agent_department_assignments (tenant_id, department_id, agent_id, status, assigned_by)
SELECT DISTINCT aa.tenant_id, u.department_id, aa.agent_id, 'active', aa.assigned_by
FROM agent_assignments aa
JOIN users u ON u.id = aa.user_id AND u.tenant_id = aa.tenant_id
JOIN departments d ON d.id = u.department_id AND d.tenant_id = aa.tenant_id AND d.status = 'active'
WHERE aa.status = 'active'
  AND EXISTS (
      SELECT 1 FROM agent_tenant_assignments ata
      WHERE ata.agent_id = aa.agent_id AND ata.tenant_id = aa.tenant_id AND ata.status = 'active'
  )
ON CONFLICT (department_id, agent_id) WHERE status = 'active' DO NOTHING;

COMMENT ON TABLE agent_department_assignments IS
    'Phase D3: department-scoped agent entitlement. Active department assignment is required for both ADMIN execution (no direct user assignment needed) and USER execution (in addition to a direct agent_assignments row).';
