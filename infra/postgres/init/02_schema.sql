-- AIQ Agentic AI Platform — Core schema
-- Initial baseline for: users, roles, books, OCR corrections, audit log.

-- ----------------------------------------------------------------------------
-- Auth & RBAC
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS users (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email           TEXT UNIQUE NOT NULL,
    password_hash   TEXT NOT NULL,
    display_name    TEXT,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS roles (
    id              SERIAL PRIMARY KEY,
    name            TEXT UNIQUE NOT NULL,
    description     TEXT
);

CREATE TABLE IF NOT EXISTS user_roles (
    user_id         UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role_id         INT  NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, role_id)
);

INSERT INTO roles (name, description) VALUES
    ('super_admin', 'Top-level administrator: manages users and roles, full system access'),
    ('admin',       'Knowledge contributor: can add and edit notes / KB content'),
    ('user',        'Knowledge consumer: can chat using the KB'),
    -- Legacy roles preserved for backward-compatibility — not surfaced in UI:
    ('reviewer',    'Legacy: review and approve OCR corrections'),
    ('uploader',    'Legacy: upload source documents'),
    ('viewer',      'Legacy: read-only access')
ON CONFLICT (name) DO NOTHING;

-- ----------------------------------------------------------------------------
-- OCR / Library domain
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS books (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    title           TEXT NOT NULL,
    author          TEXT,
    language        TEXT NOT NULL DEFAULT 'vi',
    source_path     TEXT,
    uploaded_by     UUID REFERENCES users(id) ON DELETE SET NULL,
    page_count      INT,
    status          TEXT NOT NULL DEFAULT 'pending',
    metadata        JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_books_status ON books(status);
CREATE INDEX IF NOT EXISTS idx_books_title_trgm ON books USING gin (title gin_trgm_ops);

CREATE TABLE IF NOT EXISTS book_pages (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    book_id         UUID NOT NULL REFERENCES books(id) ON DELETE CASCADE,
    page_number     INT NOT NULL,
    raw_text        TEXT,
    corrected_text  TEXT,
    confidence      REAL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (book_id, page_number)
);

CREATE TABLE IF NOT EXISTS corrections (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    page_id         UUID NOT NULL REFERENCES book_pages(id) ON DELETE CASCADE,
    model           TEXT NOT NULL,
    before_text     TEXT NOT NULL,
    after_text      TEXT NOT NULL,
    diff_summary    JSONB,
    reviewed_by     UUID REFERENCES users(id) ON DELETE SET NULL,
    review_status   TEXT NOT NULL DEFAULT 'pending',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    reviewed_at     TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_corrections_review_status ON corrections(review_status);

-- ----------------------------------------------------------------------------
-- Vector reference (embeddings live primarily in Qdrant; this is a backup
-- index keyed by external id so we can rebuild Qdrant from Postgres).
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS embeddings (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_type     TEXT NOT NULL,
    source_id       UUID NOT NULL,
    model           TEXT NOT NULL,
    dimension       INT  NOT NULL,
    embedding       VECTOR(1024),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_embeddings_source ON embeddings(source_type, source_id);

-- ----------------------------------------------------------------------------
-- Audit log
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS audit_log (
    id              BIGSERIAL PRIMARY KEY,
    actor_id        UUID REFERENCES users(id) ON DELETE SET NULL,
    action          TEXT NOT NULL,
    resource_type   TEXT NOT NULL,
    resource_id     TEXT,
    payload         JSONB,
    ip_address      INET,
    user_agent      TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_audit_log_actor ON audit_log(actor_id);
CREATE INDEX IF NOT EXISTS idx_audit_log_created_at ON audit_log(created_at DESC);
