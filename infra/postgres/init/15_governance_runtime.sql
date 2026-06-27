-- ----------------------------------------------------------------------------
-- G4 — Runtime Governance: HITL Gates + Pending Actions
--
-- A "gate" is a per-agent rule that pauses tool execution when a payload
-- matches the trigger condition (e.g. send_payment with amount > 50M).
-- The agent runtime inserts a pending_action; a human reviews + decides
-- before the action is allowed to proceed.
--
-- Trigger rule schema (JSONB, evaluated runtime-side):
--   { "tool": "<mcp_code>", "field_path": "amount", "op": "gte", "value": 50000000 }
--
-- For G4 MVP we only store the gate config + pending actions. Runtime
-- integration (actually pausing the agent) is deferred to a follow-up.
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS hitl_gates (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    name            TEXT NOT NULL,
    description     TEXT,
    trigger_rule    JSONB NOT NULL DEFAULT '{}'::jsonb,
    approver_role   TEXT NOT NULL DEFAULT 'owner',  -- owner / tenant_admin / specific user_id
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_by      UUID REFERENCES users(id) ON DELETE SET NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (agent_id, name)
);

CREATE INDEX IF NOT EXISTS idx_hitl_gates_agent ON hitl_gates(agent_id, is_active);
CREATE INDEX IF NOT EXISTS idx_hitl_gates_tenant ON hitl_gates(tenant_id);

CREATE TABLE IF NOT EXISTS hitl_pending_actions (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    gate_id         UUID NOT NULL REFERENCES hitl_gates(id) ON DELETE CASCADE,
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    triggered_by    UUID REFERENCES users(id) ON DELETE SET NULL,
    tool_code       TEXT NOT NULL,
    payload         JSONB NOT NULL,           -- full tool call args
    status          TEXT NOT NULL DEFAULT 'pending',  -- pending / approved / rejected / expired
    decided_by      UUID REFERENCES users(id) ON DELETE SET NULL,
    decided_at      TIMESTAMPTZ,
    decision_comment TEXT,
    expires_at      TIMESTAMPTZ,              -- nullable; if set, expiry job marks as 'expired'
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_hitl_pending_status
    ON hitl_pending_actions(status, created_at DESC)
    WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_hitl_pending_agent
    ON hitl_pending_actions(agent_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_hitl_pending_tenant
    ON hitl_pending_actions(tenant_id, created_at DESC);

-- Per-agent rate limits (optional override; if no row → use global default)
CREATE TABLE IF NOT EXISTS agent_rate_limits (
    agent_id           UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    tenant_id          UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    requests_per_min   INT,
    tokens_per_day     BIGINT,
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (agent_id, tenant_id)
);

-- Auto-bump updated_at on hitl_gates
CREATE OR REPLACE FUNCTION hitl_gates_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_hitl_gates_updated_at ON hitl_gates;
CREATE TRIGGER trg_hitl_gates_updated_at
    BEFORE UPDATE ON hitl_gates
    FOR EACH ROW
    EXECUTE FUNCTION hitl_gates_touch_updated_at();
