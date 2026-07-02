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
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Sidebar orders newest-first
CREATE INDEX IF NOT EXISTS idx_chat_threads_updated
    ON chat_threads(updated_at DESC);

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
