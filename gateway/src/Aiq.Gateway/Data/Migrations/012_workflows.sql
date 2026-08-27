-- Phase 5B — database-driven workflow composition. A workflow is a reusable,
-- platform-level catalog entity (like tools/skills) that a system agent
-- VERSION may optionally reference. definition.steps is validated both here
-- (shape) and at manifest-resolution time (unknown step types fail closed —
-- see runtime/executors.py). No workflow means "run as today": a single
-- prompt/tool-use turn, unchanged.
CREATE TABLE IF NOT EXISTS workflows (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name         TEXT NOT NULL,
    description  TEXT,
    definition   JSONB NOT NULL DEFAULT '{"steps":[],"requiresApproval":false}'::jsonb
                 CHECK (jsonb_typeof(definition) = 'object'
                        AND jsonb_typeof(definition->'steps') = 'array'),
    is_active    BOOLEAN NOT NULL DEFAULT true,
    created_by   UUID REFERENCES users(id) ON DELETE SET NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE agent_versions
    ADD COLUMN IF NOT EXISTS workflow_id UUID REFERENCES workflows(id) ON DELETE SET NULL;

CREATE INDEX IF NOT EXISTS idx_agent_versions_workflow ON agent_versions(workflow_id) WHERE workflow_id IS NOT NULL;
