-- ----------------------------------------------------------------------------
-- Contracts Phase 2 — Checklist items
--
-- One row per actionable item generated from contract clauses (payment
-- schedule, deadline obligation, penalty trigger, deliverable, etc.).
--
-- Status lifecycle:
--   open       → in_progress → done
--                            ↘ skipped
--
-- Phase 3 will add `notifications` table that watches this table for
-- items where (status='open' AND due_date - remind_days_before <= today).
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS contract_checklist_items (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    contract_id          UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
    clause_id            UUID REFERENCES contract_clauses(id) ON DELETE SET NULL,
    title                TEXT NOT NULL,
    description          TEXT,
    due_date             DATE,
    remind_days_before   INT NOT NULL DEFAULT 7 CHECK (remind_days_before >= 0),
    assignee_email       TEXT,
    status               TEXT NOT NULL DEFAULT 'open',  -- open / in_progress / done / skipped
    completed_at         TIMESTAMPTZ,
    completed_by         UUID REFERENCES users(id) ON DELETE SET NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_checklist_contract ON contract_checklist_items(contract_id);
CREATE INDEX IF NOT EXISTS idx_checklist_status_due
    ON contract_checklist_items(status, due_date)
    WHERE status IN ('open', 'in_progress');
CREATE INDEX IF NOT EXISTS idx_checklist_assignee
    ON contract_checklist_items(assignee_email)
    WHERE assignee_email IS NOT NULL;

-- Auto-bump updated_at
CREATE OR REPLACE FUNCTION checklist_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    -- When transitioning to done, stamp completed_at if not provided
    IF NEW.status = 'done' AND OLD.status <> 'done' AND NEW.completed_at IS NULL THEN
        NEW.completed_at = NOW();
    END IF;
    -- Reverse: if moved out of done, clear completed_*
    IF OLD.status = 'done' AND NEW.status <> 'done' THEN
        NEW.completed_at = NULL;
        NEW.completed_by = NULL;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_checklist_updated_at ON contract_checklist_items;
CREATE TRIGGER trg_checklist_updated_at
    BEFORE UPDATE ON contract_checklist_items
    FOR EACH ROW
    EXECUTE FUNCTION checklist_touch_updated_at();
