-- Phase 5C — database-driven router/delegation. A published agent VERSION
-- may list which OTHER system agents (by agent_id, not version_id — always
-- resolves to whatever that agent currently has published) it is allowed to
-- delegate to. Curated by SUPER_ADMIN in Agent Builder, exactly like tool
-- grants — never user-controlled. Delegation is deliberately single-hop: the
-- runtime does not expose delegate_to_agent on a specialist's own execution,
-- so a chain can never form regardless of what this table contains.
CREATE TABLE IF NOT EXISTS agent_version_delegates (
    agent_version_id    UUID NOT NULL REFERENCES agent_versions(id) ON DELETE CASCADE,
    specialist_agent_id UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    added_by            UUID REFERENCES users(id) ON DELETE SET NULL,
    added_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (agent_version_id, specialist_agent_id)
);

CREATE INDEX IF NOT EXISTS idx_agent_version_delegates_specialist ON agent_version_delegates(specialist_agent_id);
