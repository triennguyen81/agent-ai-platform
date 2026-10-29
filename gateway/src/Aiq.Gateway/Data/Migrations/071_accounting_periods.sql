-- Chứng từ flow v2 PR4 (docs/flows/chung-tu-proposed.md §4.1, §10.1 P1–P12;
-- QĐ 12, 13). Additive; nothing is closed at deploy (QĐ 13: a period without a
-- row is open). Down: Data/Migrations/down/071_accounting_periods.down.sql (by hand).
--
--   accounting_periods        — one row per (tenant, department, month) once a
--                               v2 write or a close touches it; status open/closed.
--   accounting_period_events  — append-only close / reopen history.
--   accounting_period_assert_open(tenant, department, date)
--                             — the one check: takes the period row FOR SHARE in
--                               the caller's transaction and raises
--                               SQLSTATE AQ001 'period_locked:yyyy-MM' when closed.
--                               Closing takes the row FOR UPDATE, so a write and
--                               a close of the same period are serialised.
--   trg_vouchers_period_lock / trg_journal_period_lock
--                             — call it for every write to a voucher (or its
--                               journal lines) of a v2 tenant, old and new period.
-- The voucher's period is the month of COALESCE(accounting_date, document_date) (P1).

-- ---------------------------------------------------------------- tables
CREATE TABLE IF NOT EXISTS accounting_periods (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    -- P7: periods belong to the department that keeps the vouchers.
    scope         TEXT NOT NULL DEFAULT 'department',
    department_id UUID NOT NULL,
    year          INTEGER NOT NULL,
    month         INTEGER NOT NULL,
    status        TEXT NOT NULL DEFAULT 'open',
    closed_by     UUID REFERENCES users(id) ON DELETE SET NULL,
    closed_at     TIMESTAMPTZ,
    reopened_by   UUID REFERENCES users(id) ON DELETE SET NULL,
    reopened_at   TIMESTAMPTZ,
    reopen_reason TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_accounting_periods_scope CHECK (scope IN ('department')),
    CONSTRAINT chk_accounting_periods_year CHECK (year BETWEEN 2000 AND 2100),
    CONSTRAINT chk_accounting_periods_month CHECK (month BETWEEN 1 AND 12),
    CONSTRAINT chk_accounting_periods_status CHECK (status IN ('open', 'closed')),
    CONSTRAINT chk_accounting_periods_closed CHECK (status <> 'closed' OR closed_at IS NOT NULL),
    CONSTRAINT chk_accounting_periods_reopen CHECK (reopened_at IS NULL OR length(btrim(COALESCE(reopen_reason, ''))) > 0),
    CONSTRAINT uq_accounting_periods UNIQUE (tenant_id, department_id, year, month),
    FOREIGN KEY (department_id, tenant_id) REFERENCES departments(id, tenant_id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS accounting_period_events (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    -- No FK: history outlives nothing it describes, but the tenant cascade
    -- removes both tables in one statement.
    period_id     UUID NOT NULL,
    department_id UUID NOT NULL,
    year          INTEGER NOT NULL,
    month         INTEGER NOT NULL,
    event_type    TEXT NOT NULL,
    actor_type    TEXT NOT NULL,
    actor_id      UUID,
    reason        TEXT,
    details       JSONB NOT NULL DEFAULT '{}'::jsonb,
    request_id    TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_accounting_period_events_type CHECK (event_type IN ('period_closed', 'period_reopened')),
    CONSTRAINT chk_accounting_period_events_actor CHECK (actor_type IN ('user', 'agent', 'system')),
    CONSTRAINT chk_accounting_period_events_reason CHECK (event_type <> 'period_reopened' OR length(btrim(COALESCE(reason, ''))) > 0)
);
CREATE INDEX IF NOT EXISTS idx_accounting_period_events_period ON accounting_period_events (period_id, created_at);

-- ---------------------------------------------------------------- history guards
-- Same rule as voucher_events: never updated, never deleted, never truncated;
-- only the cascade from a deleted tenant removes rows.
CREATE OR REPLACE FUNCTION accounting_period_history_guard() RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'DELETE' AND NOT EXISTS (SELECT 1 FROM tenants WHERE id = OLD.tenant_id) THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION '% is append-only (% refused)', TG_TABLE_NAME, TG_OP USING ERRCODE = '23514';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_accounting_period_events_append_only ON accounting_period_events;
CREATE TRIGGER trg_accounting_period_events_append_only
BEFORE UPDATE OR DELETE ON accounting_period_events
FOR EACH ROW EXECUTE FUNCTION accounting_period_history_guard();

DROP TRIGGER IF EXISTS trg_accounting_period_events_no_truncate ON accounting_period_events;
CREATE TRIGGER trg_accounting_period_events_no_truncate
BEFORE TRUNCATE ON accounting_period_events
FOR EACH STATEMENT EXECUTE FUNCTION accounting_period_history_guard();

-- Period rows change status, but are never deleted.
DROP TRIGGER IF EXISTS trg_accounting_periods_no_delete ON accounting_periods;
CREATE TRIGGER trg_accounting_periods_no_delete
BEFORE DELETE ON accounting_periods
FOR EACH ROW EXECUTE FUNCTION accounting_period_history_guard();

DROP TRIGGER IF EXISTS trg_accounting_periods_no_truncate ON accounting_periods;
CREATE TRIGGER trg_accounting_periods_no_truncate
BEFORE TRUNCATE ON accounting_periods
FOR EACH STATEMENT EXECUTE FUNCTION accounting_period_history_guard();

-- ---------------------------------------------------------------- the check
CREATE OR REPLACE FUNCTION voucher_flow_v2_enabled(p_tenant UUID) RETURNS BOOLEAN AS $$
    SELECT EXISTS (SELECT 1 FROM tenant_feature_flags WHERE tenant_id = p_tenant AND flag = 'voucher_flow_v2' AND enabled)
$$ LANGUAGE sql STABLE;

CREATE OR REPLACE FUNCTION accounting_period_assert_open(p_tenant UUID, p_department UUID, p_date DATE) RETURNS VOID AS $$
DECLARE
    y INTEGER;
    m INTEGER;
    current_status TEXT;
BEGIN
    IF p_tenant IS NULL OR p_department IS NULL OR p_date IS NULL THEN
        RETURN;   -- no scope or no date yet: the voucher is in no period
    END IF;
    y := EXTRACT(YEAR FROM p_date);
    m := EXTRACT(MONTH FROM p_date);
    -- Materialise the row so there is something to lock (QĐ 13: a new row is open).
    INSERT INTO accounting_periods (tenant_id, department_id, year, month)
    VALUES (p_tenant, p_department, y, m)
    ON CONFLICT (tenant_id, department_id, year, month) DO NOTHING;
    SELECT status INTO current_status FROM accounting_periods
     WHERE tenant_id = p_tenant AND department_id = p_department AND year = y AND month = m
       FOR SHARE;
    IF current_status = 'closed' THEN
        RAISE EXCEPTION 'period_locked:%', to_char(p_date, 'YYYY-MM') USING ERRCODE = 'AQ001';
    END IF;
END;
$$ LANGUAGE plpgsql;

-- Both periods of a change (the old one: editing or moving out; the new one:
-- creating or moving in), earliest first so two writers lock in one order.
CREATE OR REPLACE FUNCTION accounting_period_assert_open_pair(p_tenant UUID, p_department UUID, a DATE, b DATE) RETURNS VOID AS $$
BEGIN
    IF a IS NOT NULL AND b IS NOT NULL AND date_trunc('month', a) <> date_trunc('month', b) THEN
        PERFORM accounting_period_assert_open(p_tenant, p_department, LEAST(a, b));
        PERFORM accounting_period_assert_open(p_tenant, p_department, GREATEST(a, b));
    ELSE
        PERFORM accounting_period_assert_open(p_tenant, p_department, COALESCE(a, b));
    END IF;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------- triggers
-- P2: columns whose change is not an accounting change (verification,
-- review, extraction diagnostics, bookkeeping of the row itself).
CREATE OR REPLACE FUNCTION voucher_period_exempt_columns() RETURNS TEXT[] AS $$
    SELECT ARRAY['updated_at', 'lifecycle_version',
                 'tct_verified', 'tct_verified_at', 'tct_response',
                 'tax_verification_status', 'tax_verification_reason', 'tax_verification_checked_at',
                 'tax_verification_evidence', 'tax_verification_fingerprint', 'tax_verified_by',
                 'document_review_status', 'processing_blockers',
                 'extraction_error_code', 'extraction_error_stage', 'extraction_failed_at', 'error_message']
$$ LANGUAGE sql IMMUTABLE;

CREATE OR REPLACE FUNCTION vouchers_period_lock() RETURNS TRIGGER AS $$
DECLARE
    exempt TEXT[] := voucher_period_exempt_columns();
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF voucher_flow_v2_enabled(NEW.tenant_id) THEN
            PERFORM accounting_period_assert_open(NEW.tenant_id, NEW.department_id, COALESCE(NEW.accounting_date, NEW.document_date));
        END IF;
        RETURN NEW;
    END IF;

    IF TG_OP = 'DELETE' THEN
        IF voucher_flow_v2_enabled(OLD.tenant_id) THEN
            PERFORM accounting_period_assert_open(OLD.tenant_id, OLD.department_id, COALESCE(OLD.accounting_date, OLD.document_date));
        END IF;
        RETURN OLD;
    END IF;

    IF NOT voucher_flow_v2_enabled(NEW.tenant_id) THEN
        RETURN NEW;
    END IF;
    -- P3: reversing a posted voucher is never refused (the reversal lines are
    -- dated in the nearest open period, PR5).
    IF OLD.lifecycle_state = 'posted' AND NEW.lifecycle_state = 'reversed'
       AND (to_jsonb(NEW) - exempt - ARRAY['lifecycle_state', 'status', 'reversed_by', 'reversed_at', 'reversal_reason'])
           = (to_jsonb(OLD) - exempt - ARRAY['lifecycle_state', 'status', 'reversed_by', 'reversed_at', 'reversal_reason']) THEN
        RETURN NEW;
    END IF;
    IF (to_jsonb(NEW) - exempt) = (to_jsonb(OLD) - exempt) THEN
        RETURN NEW;
    END IF;
    PERFORM accounting_period_assert_open_pair(NEW.tenant_id, NEW.department_id,
        COALESCE(OLD.accounting_date, OLD.document_date), COALESCE(NEW.accounting_date, NEW.document_date));
    IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.department_id IS DISTINCT FROM OLD.department_id THEN
        PERFORM accounting_period_assert_open(OLD.tenant_id, OLD.department_id, COALESCE(OLD.accounting_date, OLD.document_date));
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Named after trg_vouchers_lifecycle_sync (BEFORE triggers fire by name) so it
-- sees the final lifecycle_state / status of the row.
DROP TRIGGER IF EXISTS trg_vouchers_period_lock ON vouchers;
CREATE TRIGGER trg_vouchers_period_lock
BEFORE INSERT OR UPDATE OR DELETE ON vouchers
FOR EACH ROW EXECUTE FUNCTION vouchers_period_lock();

CREATE OR REPLACE FUNCTION journal_period_lock() RETURNS TRIGGER AS $$
DECLARE
    row_voucher UUID := CASE WHEN TG_OP = 'DELETE' THEN OLD.voucher_id ELSE NEW.voucher_id END;
    v RECORD;
BEGIN
    -- P3: reversal lines are written in the nearest open period, never refused here.
    IF TG_OP = 'INSERT' AND NEW.source = 'reversal' THEN
        RETURN NEW;
    END IF;
    SELECT tenant_id, department_id, COALESCE(accounting_date, document_date) AS period_date
      INTO v FROM vouchers WHERE id = row_voucher;
    IF FOUND AND voucher_flow_v2_enabled(v.tenant_id) THEN
        PERFORM accounting_period_assert_open(v.tenant_id, v.department_id, v.period_date);
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_journal_period_lock ON voucher_journal_suggestions;
CREATE TRIGGER trg_journal_period_lock
BEFORE INSERT OR UPDATE OR DELETE ON voucher_journal_suggestions
FOR EACH ROW EXECUTE FUNCTION journal_period_lock();

-- ---------------------------------------------------------------- extraction failure code
-- The extract job records an extraction whose date falls in a closed period as
-- a safe failure ('period_locked', stage 'save') instead of retrying forever.
ALTER TABLE vouchers DROP CONSTRAINT IF EXISTS chk_vouchers_extraction_error_code;
ALTER TABLE vouchers ADD CONSTRAINT chk_vouchers_extraction_error_code CHECK (
    extraction_error_code IS NULL OR extraction_error_code IN (
        'provider_unavailable', 'provider_rate_limited', 'provider_session_limit', 'provider_timeout',
        'agent_execution_failed', 'extraction_output_invalid',
        'processing_agent_unavailable', 'processing_agent_version_unavailable', 'processing_provenance_invalid',
        'period_locked'));
