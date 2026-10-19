-- Configurable document processing — provenance + audit (pairs with 060).
-- Every processed document records which Agent / version / configuration
-- source handled it; every extraction run (production and Agent Builder test
-- mode) is appended to document_processing_runs.

-- ---------------------------------------------------------------------------
-- C. Provenance: which Agent / version / configuration processed a document.
-- ---------------------------------------------------------------------------
ALTER TABLE vouchers
    ADD COLUMN IF NOT EXISTS processed_by_agent_id UUID REFERENCES agents(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS processed_by_agent_version_id UUID REFERENCES agent_versions(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS document_config_source TEXT,
    -- Upload-time "Xử lý bằng trợ lý" choice: a HINT only. The extract job
    -- re-authorizes it through the capability resolver every run; it never
    -- grants anything by itself.
    ADD COLUMN IF NOT EXISTS requested_agent_id UUID;

DO $$ BEGIN
    ALTER TABLE vouchers ADD CONSTRAINT chk_vouchers_document_config_source
        CHECK (document_config_source IS NULL OR document_config_source IN ('manifest', 'legacy'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- Append-only audit of every extraction run (production and Agent Builder
-- test mode). Test runs have no voucher.
CREATE TABLE IF NOT EXISTS document_processing_runs (
    id                         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    mode                       TEXT NOT NULL CHECK (mode IN ('production', 'test')),
    voucher_id                 UUID REFERENCES vouchers(id) ON DELETE CASCADE,
    tenant_id                  UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    department_id              UUID,
    actor_user_id              UUID REFERENCES users(id) ON DELETE SET NULL,
    agent_id                   UUID REFERENCES agents(id) ON DELETE SET NULL,
    agent_version_id           UUID REFERENCES agent_versions(id) ON DELETE SET NULL,
    agent_version              INTEGER,
    config_source              TEXT NOT NULL CHECK (config_source IN ('manifest', 'legacy')),
    document_type              TEXT,
    schema_sha256              TEXT,
    workflow_id                UUID,
    workflow_version_id        UUID,
    skills                     JSONB NOT NULL DEFAULT '[]'::jsonb,
    provider                   TEXT,
    model                      TEXT,
    classification_source      TEXT,
    classification_status      TEXT,
    classification_confidence  NUMERIC(4,2),
    schema_valid               BOOLEAN,
    missing_required           TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    unsupported_document_type  BOOLEAN NOT NULL DEFAULT FALSE,
    created_at                 TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CHECK ((mode = 'production' AND voucher_id IS NOT NULL) OR (mode = 'test' AND voucher_id IS NULL))
);

CREATE INDEX IF NOT EXISTS idx_document_processing_runs_voucher
    ON document_processing_runs(voucher_id, created_at DESC) WHERE voucher_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_document_processing_runs_agent
    ON document_processing_runs(agent_id, created_at DESC);

CREATE OR REPLACE FUNCTION document_processing_runs_append_only()
RETURNS TRIGGER AS $$
BEGIN
    -- Only cascades from a deleted voucher (production run) or a deleted
    -- tenant (test run) are allowed; nothing else may rewrite history.
    IF TG_OP = 'DELETE' AND (
        (OLD.voucher_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM vouchers WHERE id = OLD.voucher_id))
        OR (OLD.voucher_id IS NULL AND NOT EXISTS (SELECT 1 FROM tenants WHERE id = OLD.tenant_id))
    ) THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION 'document_processing_runs is append-only' USING ERRCODE = '23514';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_document_processing_runs_append_only ON document_processing_runs;
CREATE TRIGGER trg_document_processing_runs_append_only
BEFORE UPDATE OR DELETE ON document_processing_runs
FOR EACH ROW EXECUTE FUNCTION document_processing_runs_append_only();
