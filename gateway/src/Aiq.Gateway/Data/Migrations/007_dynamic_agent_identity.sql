ALTER TABLE agents
    ADD COLUMN IF NOT EXISTS slug TEXT,
    ADD COLUMN IF NOT EXISTS icon_metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN IF NOT EXISTS scope TEXT,
    ADD COLUMN IF NOT EXISTS owning_tenant_id UUID REFERENCES tenants(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS created_by UUID REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS updated_by UUID REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS current_draft_version_id UUID,
    ADD COLUMN IF NOT EXISTS published_version_id UUID;

UPDATE agents
SET slug = COALESCE(slug, code),
    scope = COALESCE(scope, 'tenant'),
    owning_tenant_id = COALESCE(owning_tenant_id, tenant_id),
    created_by = COALESCE(created_by, owner_user_id),
    updated_by = COALESCE(updated_by, owner_user_id)
WHERE slug IS NULL OR scope IS NULL OR owning_tenant_id IS NULL
   OR created_by IS NULL OR updated_by IS NULL;

ALTER TABLE agents
    ALTER COLUMN slug SET NOT NULL,
    ALTER COLUMN scope SET NOT NULL,
    ALTER COLUMN scope SET DEFAULT 'tenant',
    ALTER COLUMN tenant_id DROP NOT NULL;

ALTER TABLE agents DROP CONSTRAINT IF EXISTS agents_slug_format;
ALTER TABLE agents ADD CONSTRAINT agents_slug_format
    CHECK (slug ~ '^[a-z0-9]+(?:-[a-z0-9]+)*$' AND length(slug) BETWEEN 2 AND 80);
ALTER TABLE agents DROP CONSTRAINT IF EXISTS agents_scope_valid;
ALTER TABLE agents ADD CONSTRAINT agents_scope_valid
    CHECK (scope IN ('tenant', 'system'));
ALTER TABLE agents DROP CONSTRAINT IF EXISTS agents_scope_ownership_valid;
ALTER TABLE agents ADD CONSTRAINT agents_scope_ownership_valid CHECK (
    (scope = 'tenant' AND tenant_id IS NOT NULL AND owning_tenant_id = tenant_id)
    OR (scope = 'system' AND tenant_id IS NULL AND owning_tenant_id IS NULL)
);
ALTER TABLE agents DROP CONSTRAINT IF EXISTS agents_icon_metadata_object;
ALTER TABLE agents ADD CONSTRAINT agents_icon_metadata_object
    CHECK (jsonb_typeof(icon_metadata) = 'object');

CREATE UNIQUE INDEX IF NOT EXISTS uq_agents_system_slug
    ON agents(slug) WHERE scope = 'system';
CREATE UNIQUE INDEX IF NOT EXISTS uq_agents_tenant_slug
    ON agents(owning_tenant_id, slug) WHERE scope = 'tenant';
CREATE INDEX IF NOT EXISTS idx_agents_scope ON agents(scope);

CREATE OR REPLACE FUNCTION populate_transitional_agent_identity()
RETURNS TRIGGER AS $$
BEGIN
    NEW.slug := COALESCE(NEW.slug, NEW.code);
    NEW.scope := COALESCE(NEW.scope, 'tenant');
    IF NEW.scope = 'tenant' THEN
        NEW.owning_tenant_id := COALESCE(NEW.owning_tenant_id, NEW.tenant_id);
    END IF;
    NEW.created_by := COALESCE(NEW.created_by, NEW.owner_user_id);
    NEW.updated_by := COALESCE(NEW.updated_by, NEW.owner_user_id);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_populate_transitional_agent_identity ON agents;
CREATE TRIGGER trg_populate_transitional_agent_identity
BEFORE INSERT ON agents
FOR EACH ROW EXECUTE FUNCTION populate_transitional_agent_identity();

COMMENT ON COLUMN agents.tenant_id IS
    'Legacy tenant routing column retained for current repositories; null only for future scope=system agents.';
