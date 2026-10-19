-- Agent runtime configuration hardening.
--
-- A. sim_approval_queue: the legacy L2 queue was only ever created by
--    infra/postgres/init/20_approval_queue.sql, which runs on a FRESH
--    database only. Databases created before that script never got the
--    table, so VoucherExtractJobRunner's L2 enqueue failed (silently, until
--    this phase). Same shape as the init script; IF NOT EXISTS keeps fresh
--    databases unchanged. No historical rows are fabricated: enqueue attempts
--    that failed before this migration left no trace and are not replayed.
-- B. document_review_tasks: the Department-scoped review gate for a
--    document workflow's require_review step (the legacy queue above is
--    tenant-wide and deprecated for new triggers — see ApprovalEndpoints).
-- C. vouchers.document_review_status: the explicit gate state checked by
--    every posting path.
-- D. document_processing_runs: initial vs re-extraction, explicit error
--    codes, and the review task a run opened.
-- E. Skill snapshots for EVERY skill assigned to a published version (not
--    only document-type skills), backfilled for versions published before
--    this phase.

-- ---------------------------------------------------------------------------
-- A. Legacy L2 approval queue (missing on pre-existing databases)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sim_approval_queue (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    voucher_id      UUID NOT NULL REFERENCES vouchers(id) ON DELETE CASCADE,
    voucher_type    TEXT NOT NULL,
    document_number TEXT,
    total_amount    NUMERIC(18,2),
    currency        TEXT DEFAULT 'VND',
    trigger_reasons TEXT[] NOT NULL,
    approval_level  TEXT NOT NULL DEFAULT 'l2',
    status          TEXT NOT NULL DEFAULT 'pending',
    submitted_by    UUID REFERENCES users(id),
    reviewed_by     UUID REFERENCES users(id),
    reviewed_at     TIMESTAMPTZ,
    review_note     TEXT,
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (voucher_id)
);
CREATE INDEX IF NOT EXISTS idx_approval_queue_tenant_status
    ON sim_approval_queue(tenant_id, status, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_approval_queue_pending
    ON sim_approval_queue(tenant_id, created_at DESC) WHERE status = 'pending';

-- ---------------------------------------------------------------------------
-- B. Department-scoped document review tasks
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS document_review_tasks (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    department_id       UUID NOT NULL,
    voucher_id          UUID NOT NULL REFERENCES vouchers(id) ON DELETE CASCADE,
    agent_id            UUID REFERENCES agents(id) ON DELETE SET NULL,
    agent_version_id    UUID REFERENCES agent_versions(id) ON DELETE SET NULL,
    workflow_id         UUID,
    workflow_version_id UUID,
    -- Deterministic idempotency key: the pinned workflow version when there
    -- is one (department workflows), else the workflow id (platform).
    workflow_key        UUID NOT NULL,
    review_step         TEXT NOT NULL DEFAULT 'require_review',
    document_type       TEXT NOT NULL,
    reasons             TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    status              TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'approved', 'rejected')),
    created_by          UUID REFERENCES users(id) ON DELETE SET NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    decided_by          UUID REFERENCES users(id) ON DELETE SET NULL,
    decided_at          TIMESTAMPTZ,
    decision_note       TEXT,
    CONSTRAINT fk_document_review_department_tenant
        FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT,
    CONSTRAINT chk_document_review_decision CHECK (
        (status = 'open' AND decided_by IS NULL AND decided_at IS NULL)
        OR (status <> 'open' AND decided_by IS NOT NULL AND decided_at IS NOT NULL))
);

-- One OPEN task per document + workflow version + review step; concurrent
-- or repeated runs converge on the same row (INSERT … ON CONFLICT DO NOTHING).
CREATE UNIQUE INDEX IF NOT EXISTS uq_document_review_tasks_open
    ON document_review_tasks(tenant_id, department_id, voucher_id, workflow_key, review_step)
    WHERE status = 'open';
CREATE INDEX IF NOT EXISTS idx_document_review_tasks_department
    ON document_review_tasks(tenant_id, department_id, status, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_document_review_tasks_voucher
    ON document_review_tasks(voucher_id, created_at DESC);

-- A decided task is history: it can never be re-opened or re-decided.
CREATE OR REPLACE FUNCTION document_review_tasks_decided_immutable()
RETURNS TRIGGER AS $$
BEGIN
    IF OLD.status <> 'open' THEN
        RAISE EXCEPTION 'decided document review tasks are immutable' USING ERRCODE = '23514';
    END IF;
    IF NEW.tenant_id <> OLD.tenant_id OR NEW.department_id <> OLD.department_id OR NEW.voucher_id <> OLD.voucher_id
        OR NEW.workflow_key <> OLD.workflow_key OR NEW.review_step <> OLD.review_step THEN
        RAISE EXCEPTION 'document review task identity is immutable' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_document_review_tasks_decided_immutable ON document_review_tasks;
CREATE TRIGGER trg_document_review_tasks_decided_immutable
BEFORE UPDATE ON document_review_tasks
FOR EACH ROW EXECUTE FUNCTION document_review_tasks_decided_immutable();

-- ---------------------------------------------------------------------------
-- C. Explicit gate state on the document
-- ---------------------------------------------------------------------------
ALTER TABLE vouchers ADD COLUMN IF NOT EXISTS document_review_status TEXT;
DO $$ BEGIN
    ALTER TABLE vouchers ADD CONSTRAINT chk_vouchers_document_review_status CHECK (
        document_review_status IS NULL
        OR document_review_status IN ('awaiting_review', 'approved', 'rejected', 'review_task_creation_failed'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- ---------------------------------------------------------------------------
-- D. Run audit: initial vs re-extraction, explicit errors, opened review
-- ---------------------------------------------------------------------------
ALTER TABLE document_processing_runs
    ADD COLUMN IF NOT EXISTS trigger_kind TEXT NOT NULL DEFAULT 'initial',
    ADD COLUMN IF NOT EXISTS error_code TEXT,
    ADD COLUMN IF NOT EXISTS review_task_id UUID;
DO $$ BEGIN
    ALTER TABLE document_processing_runs ADD CONSTRAINT chk_document_processing_runs_trigger_kind
        CHECK (trigger_kind IN ('initial', 'reextract', 'test'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- ---------------------------------------------------------------------------
-- E. Skill snapshots for every assigned skill of already-published versions.
-- Department skill content was locked while referenced by a published
-- version's assignments (SkillRepository.UpdateDepartmentSkillContentAsync
-- before this phase), so the current content IS the content those versions
-- were published with. Skills unknown to the catalog are image (filesystem)
-- skills and are recorded by name only, as before.
-- ---------------------------------------------------------------------------
ALTER TABLE agent_version_skill_snapshots DISABLE TRIGGER trg_protect_published_version_skill_snapshots;

INSERT INTO agent_version_skill_snapshots(agent_version_id, skill_code, skill_id, source_kind, content, content_sha256)
SELECT avsa.agent_version_id, avsa.skill_name, s.id,
       COALESCE(s.source_kind, 'filesystem'),
       CASE WHEN s.source_kind = 'declarative' THEN s.declarative_content END,
       CASE WHEN s.source_kind = 'declarative' THEN encode(digest(s.declarative_content, 'sha256'), 'hex') END
FROM agent_version_skill_assignments avsa
JOIN agent_versions v ON v.id = avsa.agent_version_id AND v.status IN ('published', 'superseded')
JOIN agents a ON a.id = v.agent_id
LEFT JOIN LATERAL (
    SELECT sk.id, sk.source_kind, sk.declarative_content
    FROM skills sk
    WHERE sk.code = avsa.skill_name
      AND (sk.scope = 'platform'
           OR (sk.scope = 'department' AND sk.tenant_id = a.owning_tenant_id AND sk.department_id = a.department_id))
    ORDER BY (sk.scope = 'department') DESC
    LIMIT 1
) s ON TRUE
WHERE NOT EXISTS (
    SELECT 1 FROM agent_version_skill_snapshots x
    WHERE x.agent_version_id = avsa.agent_version_id AND x.skill_code = avsa.skill_name)
  AND (s.source_kind IS DISTINCT FROM 'declarative' OR s.declarative_content IS NOT NULL);

ALTER TABLE agent_version_skill_snapshots ENABLE TRIGGER trg_protect_published_version_skill_snapshots;
