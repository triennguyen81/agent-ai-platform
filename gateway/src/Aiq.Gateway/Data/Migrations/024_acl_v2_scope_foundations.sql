-- ACL V2-A: additive scope and ownership foundations only.
--
-- Compatibility rule: the existing agents.scope values ('system'/'tenant')
-- remain unchanged because migrations 007-023 and current repositories use
-- them. agents.v2_scope is the staged ACL V2 classification. Authorization
-- continues to use the D0-D8 model until V2-B/C explicitly switches it.

-- ---------------------------------------------------------------------------
-- A. Agents: classify only deterministic ownership; report, never guess.
-- ---------------------------------------------------------------------------

ALTER TABLE agents
    ADD COLUMN IF NOT EXISTS v2_scope TEXT,
    ADD COLUMN IF NOT EXISTS department_id UUID,
    ADD COLUMN IF NOT EXISTS source_agent_id UUID,
    ADD COLUMN IF NOT EXISTS v2_migration_status TEXT NOT NULL DEFAULT 'pending',
    ADD COLUMN IF NOT EXISTS v2_migration_note TEXT;

DO $$ BEGIN
    ALTER TABLE agents ADD CONSTRAINT fk_agents_department_tenant
        FOREIGN KEY (department_id, owning_tenant_id)
        REFERENCES departments(id, tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE agents ADD CONSTRAINT fk_agents_source_agent
        FOREIGN KEY (source_agent_id) REFERENCES agents(id) ON DELETE SET NULL;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE agents ADD CONSTRAINT agents_v2_scope_valid
        CHECK (v2_scope IS NULL OR v2_scope IN ('platform_template', 'department'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE agents ADD CONSTRAINT agents_v2_migration_status_valid
        CHECK (v2_migration_status IN
            ('pending', 'resolved', 'quarantined_no_department',
             'quarantined_shared', 'manual_review'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE agents ADD CONSTRAINT agents_v2_scope_ownership_valid CHECK (
        (v2_scope IS NULL)
        OR (v2_scope = 'platform_template'
            AND owning_tenant_id IS NULL AND department_id IS NULL)
        OR (v2_scope = 'department'
            AND owning_tenant_id IS NOT NULL AND department_id IS NOT NULL)
    );
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE INDEX IF NOT EXISTS idx_agents_v2_scope
    ON agents(v2_scope, owning_tenant_id, department_id);
CREATE INDEX IF NOT EXISTS idx_agents_v2_migration_status
    ON agents(v2_migration_status) WHERE v2_migration_status <> 'resolved';
CREATE INDEX IF NOT EXISTS idx_agents_source_agent
    ON agents(source_agent_id) WHERE source_agent_id IS NOT NULL;

-- Existing system agents are compatibility-backed platform templates.
UPDATE agents
SET v2_scope = 'platform_template',
    department_id = NULL,
    v2_migration_status = 'resolved',
    v2_migration_note = '024: existing scope=system classified as platform template'
WHERE scope = 'system'
  AND owning_tenant_id IS NULL
  AND v2_scope IS NULL;

-- A tenant agent is department-owned only when exactly one distinct active
-- department entitlement exists. This deliberately does not inspect row
-- order, names, UUIDs, users, or default departments as tie-breakers.
WITH ownership AS (
    SELECT a.id AS agent_id, (array_agg(DISTINCT ada.department_id))[1] AS department_id
    FROM agents a
    JOIN agent_department_assignments ada
      ON ada.agent_id = a.id
     AND ada.tenant_id = a.owning_tenant_id
     AND ada.status = 'active'
    WHERE a.scope = 'tenant'
    GROUP BY a.id
    HAVING count(DISTINCT ada.department_id) = 1
)
UPDATE agents a
SET v2_scope = 'department',
    department_id = ownership.department_id,
    v2_migration_status = 'resolved',
    v2_migration_note = '024: exactly one active department entitlement'
FROM ownership
WHERE a.id = ownership.agent_id
  AND a.v2_scope IS NULL;

WITH shared AS (
    SELECT a.id
    FROM agents a
    JOIN agent_department_assignments ada
      ON ada.agent_id = a.id
     AND ada.tenant_id = a.owning_tenant_id
     AND ada.status = 'active'
    WHERE a.scope = 'tenant'
    GROUP BY a.id
    HAVING count(DISTINCT ada.department_id) > 1
)
UPDATE agents a
SET v2_migration_status = 'quarantined_shared',
    v2_migration_note = '024: multiple active departments; clone/template decision required in V2-G'
FROM shared
WHERE a.id = shared.id AND a.v2_scope IS NULL;

UPDATE agents a
SET v2_migration_status = 'quarantined_no_department',
    v2_migration_note = '024: no active department entitlement; manual ownership decision required'
WHERE a.scope = 'tenant'
  AND a.v2_scope IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM agent_department_assignments ada
      WHERE ada.agent_id = a.id
        AND ada.tenant_id = a.owning_tenant_id
        AND ada.status = 'active');

-- ---------------------------------------------------------------------------
-- B. Business capabilities: existing catalog rows are platform scope.
-- ---------------------------------------------------------------------------

ALTER TABLE business_capabilities
    ADD COLUMN IF NOT EXISTS scope TEXT NOT NULL DEFAULT 'platform',
    ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE,
    ADD COLUMN IF NOT EXISTS department_id UUID,
    ADD COLUMN IF NOT EXISTS created_by UUID REFERENCES users(id) ON DELETE SET NULL;

DO $$ BEGIN
    ALTER TABLE business_capabilities ADD CONSTRAINT fk_business_capabilities_department_tenant
        FOREIGN KEY (department_id, tenant_id)
        REFERENCES departments(id, tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE business_capabilities ADD CONSTRAINT business_capabilities_scope_ownership_valid CHECK (
        (scope = 'platform' AND tenant_id IS NULL AND department_id IS NULL)
        OR (scope = 'department' AND tenant_id IS NOT NULL AND department_id IS NOT NULL)
    );
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

UPDATE business_capabilities
SET scope = 'platform', tenant_id = NULL, department_id = NULL
WHERE tenant_id IS NULL AND department_id IS NULL;

CREATE INDEX IF NOT EXISTS idx_business_capabilities_department_scope
    ON business_capabilities(tenant_id, department_id, is_active)
    WHERE scope = 'department';

-- Existing global code uniqueness stays intact in V2-A for compatibility.
-- V2-D must replace it deliberately before same-code department entries are allowed.

-- ---------------------------------------------------------------------------
-- C. Workflows: ownership foundation only; engine and mutable behavior stay.
-- ---------------------------------------------------------------------------

ALTER TABLE workflows
    ADD COLUMN IF NOT EXISTS scope TEXT NOT NULL DEFAULT 'platform',
    ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE,
    ADD COLUMN IF NOT EXISTS department_id UUID;

DO $$ BEGIN
    ALTER TABLE workflows ADD CONSTRAINT fk_workflows_department_tenant
        FOREIGN KEY (department_id, tenant_id)
        REFERENCES departments(id, tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE workflows ADD CONSTRAINT workflows_scope_ownership_valid CHECK (
        (scope = 'platform' AND tenant_id IS NULL AND department_id IS NULL)
        OR (scope = 'department' AND tenant_id IS NOT NULL AND department_id IS NOT NULL)
    );
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

UPDATE workflows SET scope = 'platform', tenant_id = NULL, department_id = NULL
WHERE tenant_id IS NULL AND department_id IS NULL;

CREATE INDEX IF NOT EXISTS idx_workflows_department_scope
    ON workflows(tenant_id, department_id, is_active) WHERE scope = 'department';

-- ---------------------------------------------------------------------------
-- D. Skills: DB catalog foundation; filesystem runtime remains authoritative.
-- Department rows are constrained to declarative text and cannot identify an
-- executable/filesystem implementation.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS skills (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code                TEXT NOT NULL,
    name                TEXT NOT NULL,
    description         TEXT,
    scope               TEXT NOT NULL,
    tenant_id           UUID REFERENCES tenants(id) ON DELETE CASCADE,
    department_id       UUID,
    created_by          UUID REFERENCES users(id) ON DELETE SET NULL,
    source_kind         TEXT NOT NULL DEFAULT 'declarative',
    declarative_content TEXT,
    is_active           BOOLEAN NOT NULL DEFAULT TRUE,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT skills_code_format CHECK (code ~ '^[a-z0-9][a-z0-9._-]*$'),
    CONSTRAINT skills_name_not_blank CHECK (btrim(name) <> ''),
    CONSTRAINT skills_scope_ownership_valid CHECK (
        (scope = 'platform' AND tenant_id IS NULL AND department_id IS NULL
            AND source_kind IN ('filesystem', 'declarative'))
        OR (scope = 'department' AND tenant_id IS NOT NULL AND department_id IS NOT NULL
            AND source_kind = 'declarative' AND declarative_content IS NOT NULL)
    ),
    CONSTRAINT skills_source_kind_valid CHECK (source_kind IN ('filesystem', 'declarative')),
    CONSTRAINT fk_skills_department_tenant
        FOREIGN KEY (department_id, tenant_id)
        REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_skills_platform_code
    ON skills(code) WHERE scope = 'platform';
CREATE UNIQUE INDEX IF NOT EXISTS uq_skills_department_code
    ON skills(tenant_id, department_id, code) WHERE scope = 'department';
CREATE INDEX IF NOT EXISTS idx_skills_department_active
    ON skills(tenant_id, department_id, is_active) WHERE scope = 'department';

-- Preserve every skill name already referenced by an Agent version as a
-- compatibility-backed platform filesystem skill. Contents remain in the
-- existing Agent filesystem and are not copied or executed from this table.
INSERT INTO skills(code, name, scope, source_kind, declarative_content)
SELECT DISTINCT avsa.skill_name, avsa.skill_name, 'platform', 'filesystem', NULL
FROM agent_version_skill_assignments avsa
WHERE avsa.skill_name ~ '^[a-z0-9][a-z0-9._-]*$'
ON CONFLICT (code) WHERE scope = 'platform' DO NOTHING;

-- ---------------------------------------------------------------------------
-- E. Knowledge: notes are the canonical runtime content records today.
-- Existing notes remain exceptional tenant-scoped compatibility knowledge.
-- Platform rows need nullable tenant/user; current repositories keep writing
-- non-null tenant/user and therefore continue to behave exactly as before.
-- ---------------------------------------------------------------------------

ALTER TABLE notes
    ALTER COLUMN tenant_id DROP NOT NULL,
    ALTER COLUMN user_id DROP NOT NULL,
    ADD COLUMN IF NOT EXISTS scope TEXT NOT NULL DEFAULT 'tenant',
    ADD COLUMN IF NOT EXISTS department_id UUID,
    ADD COLUMN IF NOT EXISTS created_by UUID REFERENCES users(id) ON DELETE SET NULL;

UPDATE notes SET created_by = user_id WHERE created_by IS NULL;

DO $$ BEGIN
    ALTER TABLE notes ADD CONSTRAINT fk_notes_department_tenant
        FOREIGN KEY (department_id, tenant_id)
        REFERENCES departments(id, tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE notes ADD CONSTRAINT notes_scope_ownership_valid CHECK (
        (scope = 'platform' AND tenant_id IS NULL AND department_id IS NULL)
        OR (scope = 'tenant' AND tenant_id IS NOT NULL AND department_id IS NULL)
        OR (scope = 'department' AND tenant_id IS NOT NULL AND department_id IS NOT NULL)
    );
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE INDEX IF NOT EXISTS idx_notes_department_scope
    ON notes(tenant_id, department_id, updated_at DESC) WHERE scope = 'department';
CREATE INDEX IF NOT EXISTS idx_notes_platform_scope
    ON notes(updated_at DESC) WHERE scope = 'platform';

ALTER TABLE agent_knowledge
    ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE,
    ADD COLUMN IF NOT EXISTS department_id UUID,
    ADD COLUMN IF NOT EXISTS scope TEXT,
    ADD COLUMN IF NOT EXISTS created_by UUID REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS v2_migration_status TEXT NOT NULL DEFAULT 'pending';

UPDATE agent_knowledge ak
SET tenant_id = n.tenant_id,
    scope = CASE WHEN n.tenant_id IS NULL THEN 'platform' ELSE 'tenant' END,
    created_by = COALESCE(ak.attached_by, n.created_by),
    v2_migration_status = CASE
        WHEN n.tenant_id IS NULL AND a.v2_scope = 'platform_template' THEN 'resolved'
        WHEN n.tenant_id IS NOT NULL AND n.tenant_id = a.owning_tenant_id THEN 'legacy_tenant'
        ELSE 'manual_review'
    END
FROM notes n, agents a
WHERE n.id = ak.note_id AND a.id = ak.agent_id;

DO $$ BEGIN
    ALTER TABLE agent_knowledge ADD CONSTRAINT fk_agent_knowledge_department_tenant
        FOREIGN KEY (department_id, tenant_id)
        REFERENCES departments(id, tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE agent_knowledge ADD CONSTRAINT agent_knowledge_scope_valid
        CHECK (scope IS NULL OR scope IN ('platform', 'tenant', 'department'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE agent_knowledge ADD CONSTRAINT agent_knowledge_migration_status_valid
        CHECK (v2_migration_status IN ('pending', 'resolved', 'legacy_tenant', 'manual_review'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE INDEX IF NOT EXISTS idx_agent_knowledge_department_scope
    ON agent_knowledge(tenant_id, department_id, agent_id)
    WHERE department_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- F. HITL and review: nullable historical department snapshot foundations.
-- ---------------------------------------------------------------------------

ALTER TABLE hitl_gates ADD COLUMN IF NOT EXISTS department_id UUID;
ALTER TABLE hitl_pending_actions ADD COLUMN IF NOT EXISTS department_id UUID;
ALTER TABLE review_boards ADD COLUMN IF NOT EXISTS department_id UUID;
ALTER TABLE agent_review_requests ADD COLUMN IF NOT EXISTS department_id UUID;

DO $$ BEGIN
    ALTER TABLE hitl_gates ADD CONSTRAINT fk_hitl_gates_department_tenant
        FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
    ALTER TABLE hitl_pending_actions ADD CONSTRAINT fk_hitl_actions_department_tenant
        FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
    ALTER TABLE review_boards ADD CONSTRAINT fk_review_boards_department_tenant
        FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
    ALTER TABLE agent_review_requests ADD CONSTRAINT fk_review_requests_department_tenant
        FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

UPDATE hitl_gates h SET department_id = a.department_id
FROM agents a
WHERE a.id = h.agent_id AND a.v2_scope = 'department'
  AND a.owning_tenant_id = h.tenant_id AND h.department_id IS NULL;
UPDATE hitl_pending_actions h SET department_id = a.department_id
FROM agents a
WHERE a.id = h.agent_id AND a.v2_scope = 'department'
  AND a.owning_tenant_id = h.tenant_id AND h.department_id IS NULL;
UPDATE agent_review_requests r SET department_id = a.department_id
FROM agents a
WHERE a.id = r.agent_id AND a.v2_scope = 'department'
  AND a.owning_tenant_id = r.tenant_id AND r.department_id IS NULL;

CREATE INDEX IF NOT EXISTS idx_hitl_gates_department
    ON hitl_gates(tenant_id, department_id, is_active) WHERE department_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_hitl_actions_department
    ON hitl_pending_actions(tenant_id, department_id, status, created_at DESC)
    WHERE department_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_review_boards_department
    ON review_boards(tenant_id, department_id) WHERE department_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_review_requests_department
    ON agent_review_requests(tenant_id, department_id, status) WHERE department_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- G. Durable audit/usage foundations. NULL department explicitly represents
-- platform or tenant-governance events and remains valid for history.
-- ---------------------------------------------------------------------------

ALTER TABLE audit_log ADD COLUMN IF NOT EXISTS department_id UUID;
DO $$ BEGIN
    ALTER TABLE audit_log ADD CONSTRAINT fk_audit_log_department_tenant
        FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
CREATE INDEX IF NOT EXISTS idx_audit_log_department_time
    ON audit_log(tenant_id, department_id, created_at DESC)
    WHERE department_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS agent_usage_events (
    id              BIGSERIAL PRIMARY KEY,
    tenant_id       UUID REFERENCES tenants(id) ON DELETE RESTRICT,
    department_id   UUID,
    actor_id        UUID REFERENCES users(id) ON DELETE SET NULL,
    agent_id        UUID REFERENCES agents(id) ON DELETE SET NULL,
    agent_version_id UUID REFERENCES agent_versions(id) ON DELETE SET NULL,
    event_type      TEXT NOT NULL,
    request_count   BIGINT NOT NULL DEFAULT 1 CHECK (request_count >= 0),
    input_tokens    BIGINT CHECK (input_tokens IS NULL OR input_tokens >= 0),
    output_tokens   BIGINT CHECK (output_tokens IS NULL OR output_tokens >= 0),
    cost_amount     NUMERIC(18,6) CHECK (cost_amount IS NULL OR cost_amount >= 0),
    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata        JSONB NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(metadata) = 'object'),
    CONSTRAINT fk_agent_usage_department_tenant
        FOREIGN KEY (department_id, tenant_id)
        REFERENCES departments(id, tenant_id) ON DELETE RESTRICT,
    CONSTRAINT agent_usage_scope_valid CHECK (department_id IS NULL OR tenant_id IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS idx_agent_usage_tenant_time
    ON agent_usage_events(tenant_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_agent_usage_department_time
    ON agent_usage_events(tenant_id, department_id, occurred_at DESC)
    WHERE department_id IS NOT NULL;

-- Workspace is the department scope itself. This optional one-to-one table
-- stores future workspace metadata without creating a second ownership graph.
CREATE TABLE IF NOT EXISTS department_workspaces (
    department_id UUID PRIMARY KEY,
    tenant_id     UUID NOT NULL,
    created_by    UUID REFERENCES users(id) ON DELETE SET NULL,
    settings      JSONB NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(settings) = 'object'),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT fk_department_workspaces_department_tenant
        FOREIGN KEY (department_id, tenant_id)
        REFERENCES departments(id, tenant_id) ON DELETE CASCADE
);

INSERT INTO department_workspaces(department_id, tenant_id)
SELECT id, tenant_id FROM departments
ON CONFLICT (department_id) DO NOTHING;

-- ---------------------------------------------------------------------------
-- Validation/reporting views. These are live queries, not a one-time log.
-- V2-G consumes them before clone/migration; V2-A never auto-clones.
-- ---------------------------------------------------------------------------

CREATE OR REPLACE VIEW acl_v2_agent_scope_issues AS
SELECT a.id AS resource_id, a.owning_tenant_id AS tenant_id,
       NULL::UUID AS department_id,
       CASE
         WHEN a.v2_migration_status = 'quarantined_shared' THEN 'agent_multiple_departments'
         WHEN a.v2_migration_status = 'quarantined_no_department' THEN 'agent_no_resolvable_department'
         ELSE 'agent_manual_review'
       END AS issue_code,
       jsonb_build_object(
           'legacyScope', a.scope,
           'activeDepartmentCount', count(DISTINCT ada.department_id),
           'activeDepartmentIds', COALESCE(jsonb_agg(DISTINCT ada.department_id)
               FILTER (WHERE ada.department_id IS NOT NULL), '[]'::jsonb)
       ) AS details
FROM agents a
LEFT JOIN agent_department_assignments ada
  ON ada.agent_id = a.id AND ada.status = 'active'
WHERE a.v2_scope IS NULL
GROUP BY a.id, a.owning_tenant_id, a.scope, a.v2_migration_status;

CREATE OR REPLACE VIEW acl_v2_scope_validation_issues AS
SELECT 'agent'::TEXT AS resource_type, resource_id::TEXT AS resource_id, tenant_id, department_id,
       issue_code, details
FROM acl_v2_agent_scope_issues
UNION ALL
SELECT 'agent_department_assignment', ada.agent_id::TEXT, ada.tenant_id, ada.department_id,
       'cross_tenant_department_reference',
       jsonb_build_object('assignmentId', ada.id)
FROM agent_department_assignments ada
LEFT JOIN departments d ON d.id = ada.department_id AND d.tenant_id = ada.tenant_id
WHERE d.id IS NULL
UNION ALL
SELECT 'knowledge', n.id::TEXT, n.tenant_id, n.department_id,
       'knowledge_scope_unresolved',
       jsonb_build_object('scope', n.scope, 'createdBy', n.created_by)
FROM notes n
WHERE n.scope = 'tenant' OR n.created_by IS NULL
UNION ALL
SELECT 'workflow', w.id::TEXT, w.tenant_id, w.department_id,
       'workflow_ownership_ambiguous',
       jsonb_build_object('scope', w.scope,
          'referencingDepartments', count(DISTINCT a.department_id))
FROM workflows w
JOIN agent_versions av ON av.workflow_id = w.id
JOIN agents a ON a.id = av.agent_id
WHERE w.scope = 'platform' AND a.scope = 'tenant'
GROUP BY w.id, w.tenant_id, w.department_id, w.scope
UNION ALL
SELECT 'skill', s.id::TEXT, s.tenant_id, s.department_id,
       'skill_filesystem_content_not_cataloged',
       jsonb_build_object('code', s.code, 'sourceKind', s.source_kind)
FROM skills s
WHERE s.source_kind = 'filesystem'
UNION ALL
SELECT 'hitl_gate', h.id::TEXT, h.tenant_id, h.department_id,
       'hitl_department_unresolved', jsonb_build_object('agentId', h.agent_id)
FROM hitl_gates h WHERE h.department_id IS NULL
UNION ALL
SELECT 'hitl_action', h.id::TEXT, h.tenant_id, h.department_id,
       'hitl_department_unresolved', jsonb_build_object('agentId', h.agent_id)
FROM hitl_pending_actions h WHERE h.department_id IS NULL
UNION ALL
SELECT 'audit', a.id::TEXT, a.tenant_id, a.department_id,
       'audit_department_unresolved', jsonb_build_object('action', a.action)
FROM audit_log a
WHERE a.tenant_id IS NOT NULL AND a.department_id IS NULL
UNION ALL
SELECT 'usage', u.id::TEXT, u.tenant_id, u.department_id,
       'usage_department_unresolved', jsonb_build_object('eventType', u.event_type)
FROM agent_usage_events u
WHERE u.tenant_id IS NOT NULL AND u.department_id IS NULL;

COMMENT ON VIEW acl_v2_scope_validation_issues IS
    'Live V2-A validation queries. Rows require V2-G classification or an explicit governance/platform decision; they are never auto-fixed by this migration.';
