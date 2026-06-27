-- ----------------------------------------------------------------------------
-- G1 — Agents + Lifecycle + ACL
--
-- An Agent is a managed identity (NOT a process). Each agent has an owner,
-- a type/class, a lifecycle state, and a per-principal ACL (Owner/Editor/User).
--
-- Lifecycle: draft → testing → pending_review → approved → production
--                                                 ↘ rejected (back to testing)
--                                            production → deprecated → retired
--
-- The transition draft → production is BLOCKED unless an approved review
-- request exists (enforced in gateway at G3).
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS agents (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id         UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    code              TEXT NOT NULL,                  -- 'contract-monitor', 'kt-bookkeeper'
    name              TEXT NOT NULL,
    type              TEXT NOT NULL DEFAULT 'domain', -- orchestrator / domain / sub / tool_only
    description       TEXT,
    lifecycle_state   TEXT NOT NULL DEFAULT 'draft',
        -- draft / testing / pending_review / production / deprecated / retired
    owner_user_id     UUID REFERENCES users(id) ON DELETE SET NULL,
    sponsor_team      TEXT,
    baseline_role     TEXT,                           -- reference to role template
    action_tier       TEXT NOT NULL DEFAULT 'suggest',
        -- read_only / suggest / execute_with_approval / autonomous
    config            JSONB NOT NULL DEFAULT '{}'::jsonb,
        -- { "skills": [...], "model": "claude-opus-4-7", "max_turns": 6 }
    disabled_at       TIMESTAMPTZ,                    -- kill switch
    disabled_by       UUID REFERENCES users(id) ON DELETE SET NULL,
    disabled_reason   TEXT,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, code)
);

CREATE INDEX IF NOT EXISTS idx_agents_tenant_state ON agents(tenant_id, lifecycle_state);
CREATE INDEX IF NOT EXISTS idx_agents_owner ON agents(owner_user_id) WHERE owner_user_id IS NOT NULL;

-- Lifecycle history — every state transition logged (immutable)
CREATE TABLE IF NOT EXISTS agent_lifecycle_events (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    from_state      TEXT,
    to_state        TEXT NOT NULL,
    transitioned_by UUID REFERENCES users(id) ON DELETE SET NULL,
    reason          TEXT,
    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_agent_lifecycle_agent ON agent_lifecycle_events(agent_id, occurred_at DESC);

-- ACL — M:N agent ↔ principal with access_level
-- principal_type='user' for now; 'group' reserved for future role-based grants
CREATE TABLE IF NOT EXISTS agent_acl (
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    principal_id    UUID NOT NULL,
    principal_type  TEXT NOT NULL DEFAULT 'user',    -- user / group
    access_level    TEXT NOT NULL,                    -- owner / editor / user
    granted_by      UUID REFERENCES users(id) ON DELETE SET NULL,
    granted_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (agent_id, principal_id, principal_type)
);

CREATE INDEX IF NOT EXISTS idx_agent_acl_principal ON agent_acl(principal_id, principal_type);

-- Auto-bump updated_at + auto-log lifecycle transitions
CREATE OR REPLACE FUNCTION agents_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();

    -- Log state transitions automatically when lifecycle_state changes
    IF OLD.lifecycle_state IS DISTINCT FROM NEW.lifecycle_state THEN
        INSERT INTO agent_lifecycle_events (agent_id, from_state, to_state, reason)
        VALUES (NEW.id, OLD.lifecycle_state, NEW.lifecycle_state, 'auto-logged from agents trigger');
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_agents_updated_at ON agents;
CREATE TRIGGER trg_agents_updated_at
    BEFORE UPDATE ON agents
    FOR EACH ROW
    EXECUTE FUNCTION agents_touch_updated_at();
