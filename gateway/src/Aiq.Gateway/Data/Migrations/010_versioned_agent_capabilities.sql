CREATE TABLE IF NOT EXISTS agent_version_tool_grants (
    agent_version_id UUID NOT NULL REFERENCES agent_versions(id) ON DELETE CASCADE,
    tool_id          UUID NOT NULL REFERENCES tools(id) ON DELETE RESTRICT,
    granted_by       UUID REFERENCES users(id) ON DELETE SET NULL,
    granted_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (agent_version_id, tool_id)
);

CREATE TABLE IF NOT EXISTS agent_version_skill_assignments (
    agent_version_id UUID NOT NULL REFERENCES agent_versions(id) ON DELETE CASCADE,
    skill_name       TEXT NOT NULL,
    assigned_by      UUID REFERENCES users(id) ON DELETE SET NULL,
    assigned_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (agent_version_id, skill_name),
    CHECK (skill_name ~ '^[a-z0-9]+(?:-[a-z0-9]+)*$' AND length(skill_name) BETWEEN 2 AND 120)
);

INSERT INTO agent_version_tool_grants (agent_version_id, tool_id, granted_by, granted_at)
SELECT v.id, g.tool_id, g.granted_by, g.granted_at
FROM agent_tool_grants g
JOIN agent_versions v ON v.agent_id = g.agent_id AND v.version = 1
ON CONFLICT (agent_version_id, tool_id) DO NOTHING;

INSERT INTO agent_version_skill_assignments (agent_version_id, skill_name, assigned_by, assigned_at)
SELECT v.id, skill.value, COALESCE(a.updated_by, a.owner_user_id), a.updated_at
FROM agents a
JOIN agent_versions v ON v.agent_id = a.id AND v.version = 1
CROSS JOIN LATERAL jsonb_array_elements_text(
    CASE WHEN jsonb_typeof(a.config->'skills') = 'array' THEN a.config->'skills' ELSE '[]'::jsonb END
) AS skill(value)
ON CONFLICT (agent_version_id, skill_name) DO NOTHING;

CREATE OR REPLACE FUNCTION protect_published_agent_capability()
RETURNS TRIGGER AS $$
DECLARE
    target_version UUID;
    version_status TEXT;
BEGIN
    target_version := CASE WHEN TG_OP = 'DELETE' THEN OLD.agent_version_id ELSE NEW.agent_version_id END;
    SELECT status INTO version_status FROM agent_versions WHERE id = target_version;
    IF version_status <> 'draft' THEN
        RAISE EXCEPTION 'published or superseded agent version capabilities are immutable' USING ERRCODE = '23514';
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_protect_published_version_tools ON agent_version_tool_grants;
CREATE TRIGGER trg_protect_published_version_tools
BEFORE INSERT OR UPDATE OR DELETE ON agent_version_tool_grants
FOR EACH ROW EXECUTE FUNCTION protect_published_agent_capability();

DROP TRIGGER IF EXISTS trg_protect_published_version_skills ON agent_version_skill_assignments;
CREATE TRIGGER trg_protect_published_version_skills
BEFORE INSERT OR UPDATE OR DELETE ON agent_version_skill_assignments
FOR EACH ROW EXECUTE FUNCTION protect_published_agent_capability();
