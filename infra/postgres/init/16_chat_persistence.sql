-- ----------------------------------------------------------------------------
-- Chat persistence — 1 thread = 1 row with JSONB messages array
--
-- Matches Claude Messages API layout: each thread stores its entire
-- conversation as a single JSONB array. Simpler than per-message rows,
-- reads/writes are 1 query, and the sidebar listing is a plain SELECT.
--
-- Trade-off: appending rewrites the JSONB. Given max_messages=200 cap and
-- typical thread sizes < 20 messages, this is fine. If a thread ever gets
-- very hot, we can add a per-message table later without breaking the API.
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS chat_threads (
    thread_id       TEXT PRIMARY KEY,
    title           TEXT,
    messages        JSONB NOT NULL DEFAULT '[]'::jsonb,
    message_count   INT NOT NULL DEFAULT 0,
    -- Explicit, persisted conversation type — set once at thread creation,
    -- never inferred from title/content. Existing rows created before this
    -- column existed default to 'chat' (ADD COLUMN below), matching the
    -- Chat/Agentic mode toggle in web/src/components/chat-room.tsx.
    mode            TEXT NOT NULL DEFAULT 'chat',
    -- Which agent this thread belongs to (agents.id from the Gateway), or
    -- NULL for a plain/no-agent chat. Set once at thread creation, never
    -- changed on a later append — a thread cannot silently switch agents
    -- mid-conversation, and a NULL agent_id never matches a
    -- list_threads(agent_id=...) filter for a specific agent (legacy
    -- threads must not silently attach to whichever agent is selected now).
    agent_id        TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Backward compatibility for a database that already ran this file before
-- the `mode`/`agent_id` columns existed (docker-entrypoint-initdb.d scripts
-- only run once, on a brand-new data directory, so an already-initialized
-- instance needs these explicit ADD COLUMN statements too).
ALTER TABLE chat_threads ADD COLUMN IF NOT EXISTS mode TEXT NOT NULL DEFAULT 'chat';
ALTER TABLE chat_threads ADD COLUMN IF NOT EXISTS agent_id TEXT;

-- Sidebar orders newest-first
CREATE INDEX IF NOT EXISTS idx_chat_threads_updated
    ON chat_threads(updated_at DESC);

-- Agent-scoped thread listing (list_threads(agent_id=...))
CREATE INDEX IF NOT EXISTS idx_chat_threads_agent_id
    ON chat_threads(agent_id) WHERE agent_id IS NOT NULL;

-- LIKE prefix scan for per-user filtering (web sends thread_id = "{userId}:...")
CREATE INDEX IF NOT EXISTS idx_chat_threads_id_prefix
    ON chat_threads(thread_id text_pattern_ops);

-- Auto-bump updated_at when messages change
CREATE OR REPLACE FUNCTION chat_threads_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_chat_threads_updated_at ON chat_threads;
CREATE TRIGGER trg_chat_threads_updated_at
    BEFORE UPDATE ON chat_threads
    FOR EACH ROW
    EXECUTE FUNCTION chat_threads_touch_updated_at();
