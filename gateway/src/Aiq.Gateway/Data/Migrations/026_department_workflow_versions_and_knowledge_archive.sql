-- ACL V2-E: Department Workflow immutable versioning + Knowledge archive flag.
--
-- V2-A (migration 024) added scope/tenant_id/department_id to `workflows`
-- but explicitly deferred version/publish behavior to V2-E (see its own
-- report §10 blocker #5: "the workflow/skill version schema deferred to
-- V2-E"). Today `workflows.definition` is a single mutable JSONB column
-- read LIVE by DynamicAgentRepository.TryResolveWorkflowAsync at every
-- manifest-composition call — editing a workflow after an Agent version
-- referencing it is published silently changes that published version's
-- runtime behavior. This is the exact immutability gap the V2-E brief's
-- §3 called out and explicitly pre-authorized fixing via a new migration
-- ("If current schema cannot safely support immutable versioning, ...
-- migration 026 is required").
--
-- This migration is additive only and mirrors the existing, already-proven
-- agents/agent_versions draft-publish-immutable pattern (migration 008)
-- byte-for-byte in shape. It does NOT touch any platform (scope='platform')
-- workflow's existing direct-mutation behavior: no version rows are
-- backfilled for existing rows, so `workflows.published_version_id` stays
-- NULL for every current row and TryResolveWorkflowAsync's platform branch
-- (added in Gateway code, not here) keeps reading `workflows.definition`
-- directly, exactly as before. Only NEW department-scoped workflows (which
-- do not exist yet — Department Workflow CRUD is introduced by this same
-- V2-E phase) ever populate/require a published_version_id.
--
-- Skills and Knowledge do not need a schema change here: migration 024
-- already gave `skills` and `notes` every column V2-E's ownership model
-- needs (scope/tenant_id/department_id/created_by/declarative_content).
-- Skill content immutability-once-referenced-by-a-published-Agent-version
-- and Knowledge attachment isolation are both enforced by new Gateway
-- repository logic, not new schema. The one true additive gap for
-- Knowledge is an archive flag so ADMIN can retire a Department Knowledge
-- item without a hard DELETE when it has attachment history — `notes` has
-- no such flag today.

-- ---------------------------------------------------------------------------
-- A. Department Workflow versioning: workflow_versions mirrors agent_versions.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS workflow_versions (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    workflow_id   UUID NOT NULL REFERENCES workflows(id) ON DELETE CASCADE,
    version       INTEGER NOT NULL CHECK (version > 0),
    status        TEXT NOT NULL CHECK (status IN ('draft', 'published')),
    definition    JSONB NOT NULL DEFAULT '{"steps":[],"requiresApproval":false}'::jsonb
                      CHECK (jsonb_typeof(definition) = 'object'
                             AND jsonb_typeof(definition->'steps') = 'array'),
    created_by    UUID REFERENCES users(id) ON DELETE SET NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    published_by  UUID REFERENCES users(id) ON DELETE SET NULL,
    published_at  TIMESTAMPTZ,
    UNIQUE (workflow_id, version),
    UNIQUE (id, workflow_id),
    CHECK ((status = 'published' AND published_at IS NOT NULL) OR (status <> 'published'))
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_workflow_versions_one_draft
    ON workflow_versions(workflow_id) WHERE status = 'draft';
CREATE INDEX IF NOT EXISTS idx_workflow_versions_order
    ON workflow_versions(workflow_id, version DESC);

ALTER TABLE workflows
    ADD COLUMN IF NOT EXISTS current_draft_version_id UUID,
    ADD COLUMN IF NOT EXISTS published_version_id UUID;

DO $$ BEGIN
    ALTER TABLE workflows ADD CONSTRAINT workflows_current_draft_version_fkey
        FOREIGN KEY (current_draft_version_id, id)
        REFERENCES workflow_versions(id, workflow_id) DEFERRABLE INITIALLY DEFERRED;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
    ALTER TABLE workflows ADD CONSTRAINT workflows_published_version_fkey
        FOREIGN KEY (published_version_id, id)
        REFERENCES workflow_versions(id, workflow_id) DEFERRABLE INITIALLY DEFERRED;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE OR REPLACE FUNCTION protect_immutable_workflow_version()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status <> 'draft' THEN
            RAISE EXCEPTION 'published workflow versions are immutable' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;
    IF OLD.status <> 'draft' THEN
        RAISE EXCEPTION 'published workflow versions are immutable' USING ERRCODE = '23514';
    END IF;
    IF NEW.workflow_id <> OLD.workflow_id OR NEW.version <> OLD.version THEN
        RAISE EXCEPTION 'workflow version identity is immutable' USING ERRCODE = '23514';
    END IF;
    IF NEW.status NOT IN ('draft', 'published') THEN
        RAISE EXCEPTION 'draft workflow version may only remain draft or be published' USING ERRCODE = '23514';
    END IF;
    IF NEW.status = 'published' AND (NEW.published_at IS NULL OR NEW.published_by IS NULL) THEN
        RAISE EXCEPTION 'publishing a workflow version requires published_at and published_by' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_protect_immutable_workflow_version ON workflow_versions;
CREATE TRIGGER trg_protect_immutable_workflow_version
BEFORE UPDATE OR DELETE ON workflow_versions
FOR EACH ROW EXECUTE FUNCTION protect_immutable_workflow_version();

-- ---------------------------------------------------------------------------
-- B. Department Knowledge archive flag (soft-retire without hard delete).
-- ---------------------------------------------------------------------------

ALTER TABLE notes ADD COLUMN IF NOT EXISTS is_archived BOOLEAN NOT NULL DEFAULT FALSE;
CREATE INDEX IF NOT EXISTS idx_notes_department_active
    ON notes(tenant_id, department_id, updated_at DESC)
    WHERE scope = 'department' AND NOT is_archived;
