CREATE TABLE IF NOT EXISTS agent_tenant_assignments (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    agent_id    UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    status      TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'revoked')),
    assigned_by UUID REFERENCES users(id) ON DELETE SET NULL,
    assigned_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revoked_at  TIMESTAMPTZ,
    CHECK ((status = 'active' AND revoked_at IS NULL)
        OR (status = 'revoked' AND revoked_at IS NOT NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_agent_tenant_assignments_active
    ON agent_tenant_assignments(agent_id, tenant_id) WHERE status = 'active';
CREATE INDEX IF NOT EXISTS idx_agent_tenant_assignments_tenant
    ON agent_tenant_assignments(tenant_id, agent_id) WHERE status = 'active';

INSERT INTO agent_tenant_assignments (
    agent_id, tenant_id, status, assigned_by, assigned_at
)
SELECT a.id, a.tenant_id, 'active', COALESCE(a.created_by, a.owner_user_id), a.created_at
FROM agents a
WHERE a.scope = 'tenant' AND a.tenant_id IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM agent_tenant_assignments ata
      WHERE ata.agent_id = a.id AND ata.tenant_id = a.tenant_id AND ata.status = 'active'
  );

CREATE OR REPLACE FUNCTION enforce_agent_tenant_assignment_scope()
RETURNS TRIGGER AS $$
DECLARE
    agent_scope TEXT;
    owner_tenant UUID;
BEGIN
    SELECT scope, owning_tenant_id INTO agent_scope, owner_tenant
    FROM agents WHERE id = NEW.agent_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'agent does not exist' USING ERRCODE = '23503';
    END IF;
    IF agent_scope = 'tenant' AND owner_tenant <> NEW.tenant_id THEN
        RAISE EXCEPTION 'tenant-owned agent may only be assigned to its owning tenant' USING ERRCODE = '23514';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM tenants WHERE id = NEW.tenant_id AND status = 'active') THEN
        RAISE EXCEPTION 'agent tenant assignment requires an active tenant' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_enforce_agent_tenant_assignment_scope ON agent_tenant_assignments;
CREATE TRIGGER trg_enforce_agent_tenant_assignment_scope
BEFORE INSERT OR UPDATE ON agent_tenant_assignments
FOR EACH ROW EXECUTE FUNCTION enforce_agent_tenant_assignment_scope();

CREATE OR REPLACE FUNCTION create_initial_agent_tenant_assignment()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.scope = 'tenant' AND NEW.tenant_id IS NOT NULL THEN
        INSERT INTO agent_tenant_assignments (
            agent_id, tenant_id, status, assigned_by, assigned_at
        ) VALUES (
            NEW.id, NEW.tenant_id, 'active', COALESCE(NEW.created_by, NEW.owner_user_id), NEW.created_at
        )
        ON CONFLICT (agent_id, tenant_id) WHERE status = 'active' DO NOTHING;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_create_initial_agent_tenant_assignment ON agents;
CREATE TRIGGER trg_create_initial_agent_tenant_assignment
AFTER INSERT ON agents
FOR EACH ROW EXECUTE FUNCTION create_initial_agent_tenant_assignment();
