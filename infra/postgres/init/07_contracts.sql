-- ----------------------------------------------------------------------------
-- Contracts module — Phase 1 (Foundation)
--
-- Idempotent: safe to re-run. Two tables:
--   contracts          — one row per uploaded contract
--   contract_clauses   — one row per clause/section extracted from contract
--
-- Future phases add: contract_checklist_items (Phase 2) + notifications
-- (Phase 3). Kept in separate migration files so each phase can ship
-- independently.
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS contracts (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id           UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    title             TEXT NOT NULL,
    party_a           TEXT,
    party_b           TEXT,
    signed_date       DATE,
    effective_date    DATE,
    end_date          DATE,
    total_value       NUMERIC(18, 2),
    currency          TEXT NOT NULL DEFAULT 'VND',
    source_pdf_path   TEXT,
    raw_text          TEXT,
    status            TEXT NOT NULL DEFAULT 'active',  -- active / expired / terminated / archived
    tags              TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_contracts_user_id ON contracts(user_id);
CREATE INDEX IF NOT EXISTS idx_contracts_status ON contracts(status);
CREATE INDEX IF NOT EXISTS idx_contracts_end_date ON contracts(end_date) WHERE end_date IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_contracts_title_trgm ON contracts USING gin (title gin_trgm_ops);

CREATE TABLE IF NOT EXISTS contract_clauses (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    contract_id     UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
    ordinal         INT NOT NULL,
    heading         TEXT,
    body            TEXT NOT NULL,
    has_deadline    BOOLEAN NOT NULL DEFAULT FALSE,
    has_penalty     BOOLEAN NOT NULL DEFAULT FALSE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_clauses_contract_id ON contract_clauses(contract_id);
CREATE INDEX IF NOT EXISTS idx_clauses_has_deadline ON contract_clauses(contract_id) WHERE has_deadline = TRUE;

-- Auto-bump updated_at on contracts table
CREATE OR REPLACE FUNCTION contracts_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_contracts_updated_at ON contracts;
CREATE TRIGGER trg_contracts_updated_at
    BEFORE UPDATE ON contracts
    FOR EACH ROW
    EXECUTE FUNCTION contracts_touch_updated_at();
