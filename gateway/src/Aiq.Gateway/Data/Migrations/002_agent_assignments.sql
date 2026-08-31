CREATE TABLE IF NOT EXISTS agent_assignments (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    user_id         UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    assigned_by     UUID REFERENCES users(id) ON DELETE SET NULL,
    status          TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'revoked')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revoked_at      TIMESTAMPTZ,
    UNIQUE (tenant_id, agent_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_agent_assignments_user_active
    ON agent_assignments(tenant_id, user_id, agent_id) WHERE status = 'active';

-- Preserve current USER execution access represented by agent_acl.
INSERT INTO agent_assignments (tenant_id, agent_id, user_id, assigned_by, status)
SELECT a.tenant_id, a.id, acl.principal_id, acl.granted_by, 'active'
FROM agent_acl acl
JOIN agents a ON a.id = acl.agent_id
JOIN users u ON u.id = acl.principal_id AND u.tenant_id = a.tenant_id
JOIN user_roles ur ON ur.user_id = u.id
JOIN roles r ON r.id = ur.role_id AND r.name = 'user'
WHERE acl.principal_type = 'user' AND acl.access_level = 'user'
ON CONFLICT (tenant_id, agent_id, user_id) DO NOTHING;

CREATE OR REPLACE FUNCTION enforce_agent_assignment_tenant()
RETURNS TRIGGER AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM agents WHERE id = NEW.agent_id AND tenant_id = NEW.tenant_id) THEN
        RAISE EXCEPTION 'agent does not belong to assignment tenant' USING ERRCODE = '23514';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM users WHERE id = NEW.user_id AND tenant_id = NEW.tenant_id AND is_active) THEN
        RAISE EXCEPTION 'user does not belong to assignment tenant or is disabled' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_enforce_agent_assignment_tenant ON agent_assignments;
CREATE TRIGGER trg_enforce_agent_assignment_tenant
BEFORE INSERT OR UPDATE ON agent_assignments
FOR EACH ROW EXECUTE FUNCTION enforce_agent_assignment_tenant();
