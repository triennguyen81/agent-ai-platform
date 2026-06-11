-- Knowledge notes per user — the data behind the "Knowledge" tab of the
-- web UI. Tags are stored as a Postgres text[] for cheap filtering;
-- fulltext search can be added later via tsvector + GIN.

CREATE TABLE IF NOT EXISTS notes (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    title           TEXT NOT NULL,
    content         TEXT NOT NULL DEFAULT '',
    tags            TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_notes_user_id_updated
    ON notes(user_id, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_notes_tags
    ON notes USING gin (tags);

CREATE INDEX IF NOT EXISTS idx_notes_content_trgm
    ON notes USING gin (content gin_trgm_ops);
