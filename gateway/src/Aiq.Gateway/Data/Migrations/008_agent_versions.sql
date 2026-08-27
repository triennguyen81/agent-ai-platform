CREATE TABLE IF NOT EXISTS agent_versions (
    id                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    agent_id           UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    version            INTEGER NOT NULL CHECK (version > 0),
    status             TEXT NOT NULL CHECK (status IN ('draft', 'published', 'superseded')),
    provider           TEXT NOT NULL,
    model              TEXT NOT NULL,
    system_prompt      TEXT NOT NULL,
    temperature        NUMERIC(3,2) NOT NULL DEFAULT 0.20 CHECK (temperature >= 0 AND temperature <= 2),
    max_turns          INTEGER NOT NULL DEFAULT 8 CHECK (max_turns BETWEEN 1 AND 20),
    timeout_seconds    INTEGER NOT NULL DEFAULT 120 CHECK (timeout_seconds BETWEEN 1 AND 3600),
    max_tokens         INTEGER NOT NULL DEFAULT 2048 CHECK (max_tokens BETWEEN 1 AND 1000000),
    memory_config      JSONB NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(memory_config) = 'object'),
    created_by         UUID REFERENCES users(id) ON DELETE SET NULL,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    published_by       UUID REFERENCES users(id) ON DELETE SET NULL,
    published_at       TIMESTAMPTZ,
    UNIQUE (agent_id, version),
    UNIQUE (id, agent_id),
    CHECK (provider ~ '^[a-z][a-z0-9_]{1,31}$'),
    CHECK (provider IN ('ollama', 'claude_sdk', 'anthropic')),
    CHECK (length(model) BETWEEN 1 AND 200),
    CHECK (model !~ '://' AND model !~ '[[:space:]]' AND model !~ '@'),
    CHECK ((status = 'published' AND published_at IS NOT NULL)
        OR (status <> 'published'))
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_agent_versions_one_draft
    ON agent_versions(agent_id) WHERE status = 'draft';
CREATE INDEX IF NOT EXISTS idx_agent_versions_order
    ON agent_versions(agent_id, version DESC);

-- Current deployed baseline is Ollama/qwen3:latest. A legacy model beginning
-- with "claude-" is deterministic because that override is consumed only by
-- the existing Claude Agent SDK runner; all other absent/unknown values use
-- the documented deployed baseline rather than model-name guessing.
INSERT INTO agent_versions (
    agent_id, version, status, provider, model, system_prompt,
    temperature, max_turns, timeout_seconds, max_tokens, memory_config,
    created_by, created_at, published_by, published_at
)
SELECT
    a.id,
    1,
    CASE WHEN a.lifecycle_state IN ('production', 'deprecated') THEN 'published' ELSE 'draft' END,
    CASE
        WHEN COALESCE(a.config->>'model', '') LIKE 'claude-%' THEN 'claude_sdk'
        ELSE 'ollama'
    END,
    CASE
        WHEN COALESCE(a.config->>'model', '') LIKE 'claude-%' THEN a.config->>'model'
        ELSE COALESCE(NULLIF(a.config->>'model', ''), 'qwen3:latest')
    END,
    COALESCE(NULLIF(a.config->>'system_prompt', ''),
        'Bạn là agentic assistant của AIQ. Có quyền dùng tools để search / đọc / lưu note vào knowledge base, và fetch URL công khai. Luôn kết thúc bằng 1 đoạn tổng kết ngắn cho người dùng bằng tiếng Việt.'),
    0.20,
    8,
    120,
    2048,
    '{}'::jsonb,
    COALESCE(a.created_by, a.owner_user_id),
    a.created_at,
    CASE WHEN a.lifecycle_state IN ('production', 'deprecated')
        THEN COALESCE(a.updated_by, a.owner_user_id) END,
    CASE WHEN a.lifecycle_state IN ('production', 'deprecated') THEN a.updated_at END
FROM agents a
ON CONFLICT (agent_id, version) DO NOTHING;

UPDATE agents a
SET current_draft_version_id = CASE WHEN v.status = 'draft' THEN v.id ELSE a.current_draft_version_id END,
    published_version_id = CASE WHEN v.status = 'published' THEN v.id ELSE a.published_version_id END
FROM agent_versions v
WHERE v.agent_id = a.id AND v.version = 1;

ALTER TABLE agents
    ADD CONSTRAINT agents_current_draft_version_fkey
    FOREIGN KEY (current_draft_version_id, id)
    REFERENCES agent_versions(id, agent_id) DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE agents
    ADD CONSTRAINT agents_published_version_fkey
    FOREIGN KEY (published_version_id, id)
    REFERENCES agent_versions(id, agent_id) DEFERRABLE INITIALLY DEFERRED;

CREATE OR REPLACE FUNCTION protect_immutable_agent_version()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status <> 'draft' THEN
            RAISE EXCEPTION 'published or superseded agent versions are immutable' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;
    IF OLD.status <> 'draft' THEN
        RAISE EXCEPTION 'published or superseded agent versions are immutable' USING ERRCODE = '23514';
    END IF;
    IF NEW.agent_id <> OLD.agent_id OR NEW.version <> OLD.version THEN
        RAISE EXCEPTION 'agent version identity is immutable' USING ERRCODE = '23514';
    END IF;
    IF NEW.status NOT IN ('draft', 'published') THEN
        RAISE EXCEPTION 'draft version may only remain draft or be published' USING ERRCODE = '23514';
    END IF;
    IF NEW.status = 'published' AND (NEW.published_at IS NULL OR NEW.published_by IS NULL) THEN
        RAISE EXCEPTION 'publishing requires published_at and published_by' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_protect_immutable_agent_version ON agent_versions;
CREATE TRIGGER trg_protect_immutable_agent_version
BEFORE UPDATE OR DELETE ON agent_versions
FOR EACH ROW EXECUTE FUNCTION protect_immutable_agent_version();

CREATE OR REPLACE FUNCTION create_initial_agent_version()
RETURNS TRIGGER AS $$
DECLARE
    new_version_id UUID;
BEGIN
    INSERT INTO agent_versions (
        agent_id, version, status, provider, model, system_prompt,
        temperature, max_turns, timeout_seconds, max_tokens, memory_config,
        created_by, created_at
    ) VALUES (
        NEW.id, 1, 'draft',
        CASE WHEN COALESCE(NEW.config->>'model', '') LIKE 'claude-%' THEN 'claude_sdk' ELSE 'ollama' END,
        CASE WHEN COALESCE(NEW.config->>'model', '') LIKE 'claude-%'
            THEN NEW.config->>'model'
            ELSE COALESCE(NULLIF(NEW.config->>'model', ''), 'qwen3:latest') END,
        COALESCE(NULLIF(NEW.config->>'system_prompt', ''),
            'Bạn là agentic assistant của AIQ. Có quyền dùng tools để search / đọc / lưu note vào knowledge base, và fetch URL công khai. Luôn kết thúc bằng 1 đoạn tổng kết ngắn cho người dùng bằng tiếng Việt.'),
        0.20, 8, 120, 2048, '{}'::jsonb,
        COALESCE(NEW.created_by, NEW.owner_user_id), NEW.created_at
    )
    RETURNING id INTO new_version_id;

    UPDATE agents SET current_draft_version_id = new_version_id WHERE id = NEW.id;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_create_initial_agent_version ON agents;
CREATE TRIGGER trg_create_initial_agent_version
AFTER INSERT ON agents
FOR EACH ROW EXECUTE FUNCTION create_initial_agent_version();
