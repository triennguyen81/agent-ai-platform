CREATE OR REPLACE FUNCTION enforce_agent_assignment_tenant()
RETURNS TRIGGER AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM agents
        WHERE id = NEW.agent_id
          AND ((scope = 'tenant' AND tenant_id = NEW.tenant_id)
            OR scope = 'system')
    ) THEN
        RAISE EXCEPTION 'agent does not belong to assignment tenant' USING ERRCODE = '23514';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM users
        WHERE id = NEW.user_id AND tenant_id = NEW.tenant_id AND is_active
    ) THEN
        RAISE EXCEPTION 'user does not belong to assignment tenant or is disabled' USING ERRCODE = '23514';
    END IF;
    IF NEW.status = 'active' AND NOT EXISTS (
        SELECT 1 FROM agent_tenant_assignments
        WHERE agent_id = NEW.agent_id AND tenant_id = NEW.tenant_id AND status = 'active'
    ) THEN
        RAISE EXCEPTION 'active user assignment requires an active agent tenant assignment' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION validate_agent_version_pointers()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.current_draft_version_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM agent_versions
        WHERE id = NEW.current_draft_version_id AND agent_id = NEW.id AND status = 'draft'
    ) THEN
        RAISE EXCEPTION 'current draft version must reference a draft of the same agent' USING ERRCODE = '23514';
    END IF;
    IF NEW.published_version_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM agent_versions
        WHERE id = NEW.published_version_id AND agent_id = NEW.id AND status = 'published'
    ) THEN
        RAISE EXCEPTION 'published version must reference a published version of the same agent' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_validate_agent_version_pointers ON agents;
CREATE CONSTRAINT TRIGGER trg_validate_agent_version_pointers
AFTER INSERT OR UPDATE OF current_draft_version_id, published_version_id ON agents
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION validate_agent_version_pointers();

CREATE TABLE IF NOT EXISTS agent_version_audit (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    agent_id         UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    agent_version_id UUID NOT NULL REFERENCES agent_versions(id) ON DELETE CASCADE,
    version          INTEGER NOT NULL,
    event_type       TEXT NOT NULL CHECK (event_type IN ('backfilled', 'created', 'published')),
    actor_id         UUID REFERENCES users(id) ON DELETE SET NULL,
    occurred_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    details          JSONB NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(details) = 'object')
);

CREATE INDEX IF NOT EXISTS idx_agent_version_audit_agent
    ON agent_version_audit(agent_id, occurred_at DESC);

INSERT INTO agent_version_audit (
    agent_id, agent_version_id, version, event_type, actor_id, occurred_at,
    details
)
SELECT v.agent_id, v.id, v.version, 'backfilled', v.created_by, v.created_at,
       jsonb_build_object('migration', '011_agent_consistency_audit.sql')
FROM agent_versions v
WHERE v.version = 1
  AND NOT EXISTS (
      SELECT 1 FROM agent_version_audit ava
      WHERE ava.agent_version_id = v.id AND ava.event_type = 'backfilled'
  );

CREATE OR REPLACE FUNCTION audit_agent_version_change()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO agent_version_audit (
            agent_id, agent_version_id, version, event_type, actor_id, occurred_at
        ) VALUES (
            NEW.agent_id, NEW.id, NEW.version,
            CASE WHEN NEW.status = 'published' THEN 'published' ELSE 'created' END,
            COALESCE(NEW.published_by, NEW.created_by),
            COALESCE(NEW.published_at, NEW.created_at)
        );
    ELSIF OLD.status = 'draft' AND NEW.status = 'published' THEN
        INSERT INTO agent_version_audit (
            agent_id, agent_version_id, version, event_type, actor_id, occurred_at
        ) VALUES (
            NEW.agent_id, NEW.id, NEW.version, 'published', NEW.published_by, NEW.published_at
        );
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_audit_agent_version_change ON agent_versions;
CREATE TRIGGER trg_audit_agent_version_change
AFTER INSERT OR UPDATE OF status ON agent_versions
FOR EACH ROW EXECUTE FUNCTION audit_agent_version_change();
