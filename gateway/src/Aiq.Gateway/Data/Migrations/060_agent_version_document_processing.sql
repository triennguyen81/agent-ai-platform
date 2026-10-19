-- Configurable document processing through Agent Builder + published Agent
-- version.
--
-- Document-processing configuration (which SYSTEM document types an Agent
-- version accepts, their extraction field schema, extraction instructions,
-- the workflow and skills used for extraction) becomes part of the Agent
-- VERSION, exactly like tool grants / skill assignments / capabilities
-- (migration 010): editable only while the version is a draft, immutable
-- once published or superseded — the same protect_published_agent_capability
-- trigger enforces it.
--
-- What is deliberately NOT here: accounting handlers. The canonical type
-- code (invoice_vat_out, receipt, ...) identifies a server-owned handler in
-- Gateway code (Vouchers/DocumentProcessing/DocumentTypeCatalog.cs); there is
-- no column through which a version can name, replace or parameterise
-- posting/settlement/AR/AP logic.
--
-- Additive only: a version with no rows here keeps the legacy built-in
-- extraction (document_config_source = 'legacy'). Voucher provenance and the
-- run audit live in migration 061.

-- ---------------------------------------------------------------------------
-- A. Per-version document type configuration.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS agent_version_document_types (
    agent_version_id        UUID NOT NULL REFERENCES agent_versions(id) ON DELETE CASCADE,
    code                    TEXT NOT NULL,
    enabled                 BOOLEAN NOT NULL DEFAULT TRUE,
    display_name            TEXT NOT NULL,
    -- [{name,label,type,required,description}] — the structured schema
    -- source; JSON Schema is derived deterministically from it.
    fields                  JSONB NOT NULL DEFAULT '[]'::jsonb,
    extraction_instructions TEXT NOT NULL DEFAULT '',
    workflow_id             UUID REFERENCES workflows(id) ON DELETE RESTRICT,
    -- Pinned at publish time (department workflows only — platform
    -- workflows have no version rows). NULL on drafts.
    -- Pinned ids are informational (no FK): the frozen content lives in
    -- workflow_snapshot / agent_version_skill_snapshots.content.
    workflow_version_id     UUID,
    -- {name, definition} copied at publish time, so a published version's
    -- workflow can never drift — platform workflows have no version rows
    -- and remain directly editable by SUPER_ADMIN. NULL on drafts.
    workflow_snapshot       JSONB,
    skill_codes            TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    sort_order              INTEGER NOT NULL DEFAULT 0,
    updated_by              UUID REFERENCES users(id) ON DELETE SET NULL,
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (agent_version_id, code),
    CONSTRAINT avdt_code_format CHECK (code ~ '^[a-z][a-z0-9_]{1,62}$'),
    CONSTRAINT avdt_display_name_not_blank CHECK (btrim(display_name) <> '' AND length(display_name) <= 160),
    CONSTRAINT avdt_fields_array CHECK (jsonb_typeof(fields) = 'array'),
    CONSTRAINT avdt_instructions_len CHECK (length(extraction_instructions) <= 20000)
);

DROP TRIGGER IF EXISTS trg_protect_published_version_document_types ON agent_version_document_types;
CREATE TRIGGER trg_protect_published_version_document_types
BEFORE INSERT OR UPDATE OR DELETE ON agent_version_document_types
FOR EACH ROW EXECUTE FUNCTION protect_published_agent_capability();

-- ---------------------------------------------------------------------------
-- B. Skill content snapshots, captured when a version is published.
-- Declarative skill content is copied so the published version's extraction
-- guidance can never drift; filesystem skills are recorded by name only
-- (their content ships with the Agent image, as before).
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS agent_version_skill_snapshots (
    agent_version_id UUID NOT NULL REFERENCES agent_versions(id) ON DELETE CASCADE,
    skill_code       TEXT NOT NULL,
    skill_id         UUID,
    source_kind      TEXT NOT NULL CHECK (source_kind IN ('declarative', 'filesystem')),
    content          TEXT,
    content_sha256   TEXT,
    captured_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (agent_version_id, skill_code),
    CHECK ((source_kind = 'declarative' AND content IS NOT NULL AND content_sha256 IS NOT NULL)
        OR (source_kind = 'filesystem' AND content IS NULL))
);

-- Snapshots are written in the publish transaction while the version is
-- still 'draft' (the same trigger then freezes them).
DROP TRIGGER IF EXISTS trg_protect_published_version_skill_snapshots ON agent_version_skill_snapshots;
CREATE TRIGGER trg_protect_published_version_skill_snapshots
BEFORE INSERT OR UPDATE OR DELETE ON agent_version_skill_snapshots
FOR EACH ROW EXECUTE FUNCTION protect_published_agent_capability();
