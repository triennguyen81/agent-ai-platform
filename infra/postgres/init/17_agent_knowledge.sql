-- ----------------------------------------------------------------------------
-- Agent Knowledge Base — M:N between agents and notes
--
-- Admin attaches notes to agents so those notes form the RAG context for
-- users chatting with that agent. Notes still belong to their owner (admin
-- who created them); attach is a separate M:N grant.
--
-- Users (role=user) only READ via the chat pipeline — they don't see
-- /knowledge, don't own notes, and can't attach/detach. This table gives
-- admins per-agent curation without duplicating the notes themselves.
--
-- Backfill: none. Existing notes stay unattached — admin manually attaches
-- them to agents via the new UI on agent detail page.
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS agent_knowledge (
    agent_id     UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    note_id      UUID NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
    attached_by  UUID REFERENCES users(id) ON DELETE SET NULL,
    attached_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (agent_id, note_id)
);

CREATE INDEX IF NOT EXISTS idx_agent_knowledge_note ON agent_knowledge(note_id);
CREATE INDEX IF NOT EXISTS idx_agent_knowledge_agent ON agent_knowledge(agent_id);
