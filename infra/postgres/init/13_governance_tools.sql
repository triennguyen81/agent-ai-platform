-- ----------------------------------------------------------------------------
-- G2 — Tool Registry + Action Tier enforcement + Delegation chain
--
-- Tools are now first-class entities (vs hardcoded in Python). Each tool has
-- a risk_tier and min_action_tier that determines which agents may call it:
--
--   agent.action_tier  >=  tool.min_action_tier
--      read_only       <   suggest   <   execute_with_approval   <   autonomous
--
-- Global tool registry is super_admin-managed. Per-agent grants are
-- tenant_admin-managed via /agents/{id}/tools.
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS tools (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code             TEXT UNIQUE NOT NULL,        -- 'mcp__aiq__save_contract'
    name             TEXT NOT NULL,
    description      TEXT,
    category         TEXT,                        -- read / write / external
    risk_tier        TEXT NOT NULL DEFAULT 'low', -- low / medium / high / critical
    min_action_tier  TEXT NOT NULL DEFAULT 'suggest',
        -- minimum agent.action_tier required to call this tool
    is_active        BOOLEAN NOT NULL DEFAULT TRUE,
    metadata         JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_tools_active ON tools(is_active);

-- Per-agent whitelist of tools the agent may invoke
CREATE TABLE IF NOT EXISTS agent_tool_grants (
    agent_id     UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    tool_id      UUID NOT NULL REFERENCES tools(id) ON DELETE CASCADE,
    granted_by   UUID REFERENCES users(id) ON DELETE SET NULL,
    granted_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (agent_id, tool_id)
);

CREATE INDEX IF NOT EXISTS idx_agent_tool_grants_tool ON agent_tool_grants(tool_id);

-- Delegation chain log (orchestrator → sub-agent invocations)
CREATE TABLE IF NOT EXISTS agent_delegations (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    parent_agent_id UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    child_agent_id  UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    session_id      UUID,
    payload_hash    TEXT,
    invoked_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_agent_delegations_parent ON agent_delegations(parent_agent_id, invoked_at DESC);
CREATE INDEX IF NOT EXISTS idx_agent_delegations_tenant ON agent_delegations(tenant_id, invoked_at DESC);

-- Auto-bump updated_at on tools
CREATE OR REPLACE FUNCTION tools_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_tools_updated_at ON tools;
CREATE TRIGGER trg_tools_updated_at
    BEFORE UPDATE ON tools
    FOR EACH ROW
    EXECUTE FUNCTION tools_touch_updated_at();

-- ----------------------------------------------------------------------------
-- Seed the existing MCP tools so backward compatibility is preserved.
-- Risk tiers reflect what the tool actually does:
--   - extract_contract_fields: read-only LLM extraction → low
--   - save_contract: writes contract row → medium
--   - get_contract: reads contract → low
--   - save_checklist_items: bulk-write/replace checklist → high
-- ----------------------------------------------------------------------------

INSERT INTO tools (code, name, description, category, risk_tier, min_action_tier)
VALUES
    ('mcp__aiq__extract_contract_fields',
     'Extract contract fields',
     'Parse raw contract text into structured JSON (parties, dates, clauses).',
     'read', 'low', 'suggest'),
    ('mcp__aiq__save_contract',
     'Save contract',
     'Persist extracted contract + clauses into the contracts table.',
     'write', 'medium', 'execute_with_approval'),
    ('mcp__aiq__get_contract',
     'Get contract',
     'Read a contract with its clauses by id.',
     'read', 'low', 'read_only'),
    ('mcp__aiq__save_checklist_items',
     'Save checklist items',
     'Bulk insert/replace checklist items for a contract.',
     'write', 'high', 'execute_with_approval')
ON CONFLICT (code) DO NOTHING;

-- Auto-grant all 4 tools to the backfilled contract-monitor agent
INSERT INTO agent_tool_grants (agent_id, tool_id)
SELECT a.id, t.id
FROM agents a
JOIN tenants tn ON tn.id = a.tenant_id AND tn.code = 'default'
CROSS JOIN tools t
WHERE a.code = 'contract-monitor'
  AND t.code IN (
    'mcp__aiq__extract_contract_fields',
    'mcp__aiq__save_contract',
    'mcp__aiq__get_contract',
    'mcp__aiq__save_checklist_items'
  )
ON CONFLICT (agent_id, tool_id) DO NOTHING;

-- ----------------------------------------------------------------------------
-- Voucher module Phase 1 (Vouchers · Kế toán chứng từ)
-- ----------------------------------------------------------------------------
INSERT INTO tools (code, name, description, category, risk_tier, min_action_tier)
VALUES
    ('mcp__aiq__extract_voucher_fields',
     'Extract voucher fields',
     'Parse raw OCR text of a Vietnamese voucher (hoá đơn VAT, phiếu thu/chi, uỷ nhiệm chi, sao kê) into structured JSON.',
     'read', 'low', 'suggest'),
    ('mcp__aiq__save_voucher',
     'Save voucher',
     'Persist extracted voucher + line items into the vouchers table.',
     'write', 'medium', 'execute_with_approval'),
    ('mcp__aiq__verify_einvoice_tct',
     'Verify e-invoice with TCT',
     'Look up VAT invoice against Tổng cục Thuế (hoadondientu.gdt.gov.vn). Best-effort — falls back to local MST + format checks.',
     'read', 'low', 'read_only'),
    ('mcp__aiq__suggest_journal_entry',
     'Suggest journal entry',
     'Propose Nợ/Có journal entries per Vietnamese TT200 chart of accounts for a saved voucher.',
     'read', 'low', 'suggest'),
    ('mcp__aiq__save_journal_suggestions',
     'Save journal suggestions',
     'Persist the AI-proposed Nợ/Có entries into voucher_journal_suggestions so the user can accept/reject on the detail page.',
     'write', 'medium', 'execute_with_approval')
ON CONFLICT (code) DO NOTHING;
