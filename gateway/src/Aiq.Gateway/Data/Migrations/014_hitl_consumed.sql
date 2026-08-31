-- Phase 5D — wires the existing HITL gate/queue/decide machinery into real
-- tool execution. consumed_at prevents a single approval from being replayed
-- indefinitely: once a decided (approved) action is used to unblock one
-- matching tool call, it is marked consumed and cannot authorize another.
ALTER TABLE hitl_pending_actions ADD COLUMN IF NOT EXISTS consumed_at TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS idx_hitl_pending_lookup
    ON hitl_pending_actions(tenant_id, agent_id, tool_code, status)
    WHERE consumed_at IS NULL;
