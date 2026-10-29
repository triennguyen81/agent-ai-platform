-- Chứng từ flow v2 PR3 (docs/flows/chung-tu-proposed.md §2, §3.7, §4.1, §4.2;
-- QĐ 2, 3, 20, 24). Additive: the legacy `status` column keeps its values
-- (except legacy 'journalized' rows, QĐ 24) and every legacy write path keeps
-- working. Down: Data/Migrations/down/070_voucher_lifecycle.down.sql (run by hand).
--
--   lifecycle_state   — the v2 state (VoucherStateMachine). Legacy writes of
--                       `status` are mapped onto it by trg_vouchers_lifecycle_sync;
--                       v2 writes set lifecycle_state and the trigger mirrors status.
--   voucher_events    — append-only history. Every lifecycle change is written by
--                       ONE trigger in the same transaction as the change; the v2
--                       code names the actor via set_config('aiq.actor_*', …, true).
--   tenant_feature_flags — per-tenant switches; 'voucher_flow_v2' (default off).

-- ---------------------------------------------------------------- columns
ALTER TABLE vouchers
    ADD COLUMN IF NOT EXISTS lifecycle_state   TEXT NOT NULL DEFAULT 'draft',
    ADD COLUMN IF NOT EXISTS lifecycle_version INTEGER NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS submitted_by      UUID REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS submitted_at      TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS approved_by       UUID REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS approved_at       TIMESTAMPTZ,
    -- posted_at: first time the voucher was ever posted; never cleared (QĐ 3).
    ADD COLUMN IF NOT EXISTS posted_by         UUID REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS posted_at         TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS accounting_date   DATE,
    ADD COLUMN IF NOT EXISTS reversed_by       UUID REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS reversed_at       TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS reversal_reason   TEXT,
    ADD COLUMN IF NOT EXISTS deleted_at        TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS deleted_by        UUID REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS supersedes_id     UUID REFERENCES vouchers(id) ON DELETE RESTRICT;

DO $$ BEGIN
  ALTER TABLE vouchers ADD CONSTRAINT chk_vouchers_lifecycle_state CHECK (lifecycle_state IN
    ('draft','extracted','needs_fix','validated','pending_approval','approved','posted','reversed','rejected'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE INDEX IF NOT EXISTS idx_vouchers_lifecycle
    ON vouchers (tenant_id, department_id, lifecycle_state) WHERE deleted_at IS NULL;

-- ---------------------------------------------------------- voucher_events
-- voucher_id has no FK on purpose: history outlives a hard-deleted voucher
-- (tenants without the v2 flag still hard-delete).
CREATE TABLE IF NOT EXISTS voucher_events (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id            UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    voucher_id           UUID,
    accounting_period_id UUID,
    event_type           TEXT NOT NULL,
    actor_type           TEXT NOT NULL,
    actor_id             UUID,
    before               JSONB,
    after                JSONB,
    reason               TEXT,
    request_id           TEXT,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_voucher_events_type CHECK (event_type IN ('state_change','field_edit','journal_edit','extraction',
        'validation','file_attached','tax_override','period_closed','period_reopened','legacy_backfill','deleted')),
    CONSTRAINT chk_voucher_events_actor CHECK (actor_type IN ('user','agent','system'))
);
CREATE INDEX IF NOT EXISTS idx_voucher_events_voucher ON voucher_events (voucher_id, created_at);
CREATE INDEX IF NOT EXISTS idx_voucher_events_tenant ON voucher_events (tenant_id, created_at);

CREATE OR REPLACE FUNCTION voucher_events_append_only() RETURNS TRIGGER AS $$
BEGIN
    -- Only the cascade from a deleted tenant may remove history.
    IF TG_OP = 'DELETE' AND NOT EXISTS (SELECT 1 FROM tenants WHERE id = OLD.tenant_id) THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION 'voucher_events is append-only (% refused)', TG_OP USING ERRCODE = '23514';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_voucher_events_append_only ON voucher_events;
CREATE TRIGGER trg_voucher_events_append_only
BEFORE UPDATE OR DELETE ON voucher_events
FOR EACH ROW EXECUTE FUNCTION voucher_events_append_only();

-- TRUNCATE skips row triggers; refuse it at statement level.
DROP TRIGGER IF EXISTS trg_voucher_events_no_truncate ON voucher_events;
CREATE TRIGGER trg_voucher_events_no_truncate
BEFORE TRUNCATE ON voucher_events
FOR EACH STATEMENT EXECUTE FUNCTION voucher_events_append_only();

-- ---------------------------------------------------- tenant_feature_flags
CREATE TABLE IF NOT EXISTS tenant_feature_flags (
    tenant_id  UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    flag       TEXT NOT NULL,
    enabled    BOOLEAN NOT NULL DEFAULT FALSE,
    updated_by UUID REFERENCES users(id) ON DELETE SET NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, flag),
    CONSTRAINT chk_tenant_feature_flags_flag CHECK (flag ~ '^[a-z0-9_]+$')
);

-- ------------------------------------------------------------- functions
-- Legacy status → lifecycle_state (§2.1). NULL = unknown status, leave as is.
CREATE OR REPLACE FUNCTION voucher_lifecycle_from_status(s TEXT) RETURNS TEXT AS $$
    SELECT CASE
        WHEN s IN ('processing','ocr_complete','ocr_unavailable','extraction_failed','uploading') THEN 'draft'
        WHEN s = 'extracted' THEN 'extracted'
        WHEN s IN ('verified','journalized') THEN 'posted'
        WHEN s = 'rejected' THEN 'rejected'
    END
$$ LANGUAGE sql IMMUTABLE;

-- lifecycle_state → legacy status for v2 writes, so the web, the agent's report
-- tools and status-report keep reading `status`. NULL = keep the current status.
CREATE OR REPLACE FUNCTION voucher_status_from_lifecycle(l TEXT) RETURNS TEXT AS $$
    SELECT CASE
        WHEN l IN ('extracted','needs_fix','validated','pending_approval','approved','reversed') THEN 'extracted'
        WHEN l = 'posted' THEN 'verified'
        WHEN l = 'rejected' THEN 'rejected'
    END
$$ LANGUAGE sql IMMUTABLE;

-- A backfill sets aiq.backfill so updated_at keeps its meaning (last real change).
CREATE OR REPLACE FUNCTION vouchers_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    IF current_setting('aiq.backfill', true) = 'on' THEN
        NEW.updated_at = OLD.updated_at;
    ELSE
        NEW.updated_at = NOW();
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------- backfill
SELECT set_config('aiq.backfill', 'on', true);

-- QĐ 24: legacy 'journalized' rows (code no longer writes them). With posting
-- evidence → verified/posted; otherwise extracted/needs_fix (a human re-checks).
CREATE TEMP TABLE _journalized ON COMMIT DROP AS
SELECT v.id, v.tenant_id,
       (EXISTS (SELECT 1 FROM voucher_journal_suggestions j WHERE j.voucher_id = v.id AND j.source = 'posting')
        OR EXISTS (SELECT 1 FROM sim_ar_ledger a WHERE a.voucher_id = v.id)
        OR EXISTS (SELECT 1 FROM sim_ap_ledger p WHERE p.voucher_id = v.id)
        OR EXISTS (SELECT 1 FROM cash_transactions c WHERE c.voucher_id = v.id AND c.status = 'posted')) AS has_posting
  FROM vouchers v WHERE v.status = 'journalized';

UPDATE vouchers v
   SET status          = CASE WHEN j.has_posting THEN 'verified' ELSE 'extracted' END,
       lifecycle_state = CASE WHEN j.has_posting THEN 'posted' ELSE 'needs_fix' END,
       posted_at       = CASE WHEN j.has_posting THEN v.updated_at END
  FROM _journalized j WHERE v.id = j.id;

INSERT INTO voucher_events (tenant_id, voucher_id, event_type, actor_type, before, after, reason)
SELECT j.tenant_id, j.id, 'legacy_backfill', 'system',
       jsonb_build_object('status', 'journalized'),
       jsonb_build_object('status', v.status, 'lifecycle_state', v.lifecycle_state),
       'migration 070: journalized removed (QĐ 24)'
  FROM _journalized j JOIN vouchers v ON v.id = j.id;

-- Everything else: straight map; verified = posted (first posting time unknown → updated_at).
UPDATE vouchers
   SET lifecycle_state = COALESCE(voucher_lifecycle_from_status(status), 'draft'),
       posted_at       = CASE WHEN status = 'verified' THEN updated_at END
 WHERE id NOT IN (SELECT id FROM _journalized);

-- Receipts / payments posted then cancelled through the legacy path are back at
-- 'extracted'; they were posted once, so they can never be deleted (QĐ 3).
CREATE TEMP TABLE _legacy_reversed ON COMMIT DROP AS
SELECT v.id, v.tenant_id, MIN(c.posted_at) AS first_posted_at,
       jsonb_agg(c.id ORDER BY c.posted_at) AS reversed_transactions
  FROM vouchers v JOIN cash_transactions c ON c.voucher_id = v.id AND c.status = 'reversed'
 WHERE v.status = 'extracted'
   AND NOT EXISTS (SELECT 1 FROM cash_transactions p WHERE p.voucher_id = v.id AND p.status = 'posted')
 GROUP BY v.id, v.tenant_id;

UPDATE vouchers v SET posted_at = r.first_posted_at FROM _legacy_reversed r WHERE v.id = r.id;

INSERT INTO voucher_events (tenant_id, voucher_id, event_type, actor_type, before, after, reason)
SELECT r.tenant_id, r.id, 'legacy_backfill', 'system',
       jsonb_build_object('status', 'extracted'),
       jsonb_build_object('lifecycle_state', 'extracted', 'reversed_cash_transactions', r.reversed_transactions),
       'migration 070: posted then reversed through the legacy path'
  FROM _legacy_reversed r;

SELECT set_config('aiq.backfill', 'off', true);

-- ---------------------------------------------------------------- triggers
-- BEFORE: keep status and lifecycle_state in step, bump lifecycle_version on
-- every lifecycle change, stamp posted_at the first time a voucher is posted.
CREATE OR REPLACE FUNCTION vouchers_lifecycle_sync() RETURNS TRIGGER AS $$
DECLARE mapped TEXT;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.lifecycle_state = 'draft' THEN
            NEW.lifecycle_state := COALESCE(voucher_lifecycle_from_status(NEW.status), 'draft');
        END IF;
    ELSIF NEW.lifecycle_state IS DISTINCT FROM OLD.lifecycle_state THEN
        -- v2 write: mirror the legacy status.
        NEW.status := COALESCE(voucher_status_from_lifecycle(NEW.lifecycle_state), NEW.status);
        NEW.lifecycle_version := OLD.lifecycle_version + 1;
    ELSIF NEW.status IS DISTINCT FROM OLD.status THEN
        -- Legacy write (job, cash settlement, apply-*): follow status.
        mapped := voucher_lifecycle_from_status(NEW.status);
        IF mapped IS NOT NULL AND mapped IS DISTINCT FROM OLD.lifecycle_state THEN
            NEW.lifecycle_state := mapped;
            NEW.lifecycle_version := OLD.lifecycle_version + 1;
        END IF;
    END IF;
    IF NEW.lifecycle_state = 'posted' AND NEW.posted_at IS NULL THEN
        NEW.posted_at := NOW();
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_vouchers_lifecycle_sync ON vouchers;
CREATE TRIGGER trg_vouchers_lifecycle_sync
BEFORE INSERT OR UPDATE OF status, lifecycle_state ON vouchers
FOR EACH ROW EXECUTE FUNCTION vouchers_lifecycle_sync();

-- AFTER: the single writer of state_change events. Actor and reason come from
-- transaction-local settings set by the v2 code; anything else is 'system'.
CREATE OR REPLACE FUNCTION vouchers_lifecycle_event() RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.lifecycle_state IS NOT DISTINCT FROM OLD.lifecycle_state THEN
        RETURN NULL;
    END IF;
    INSERT INTO voucher_events (tenant_id, voucher_id, event_type, actor_type, actor_id, before, after, reason, request_id)
    VALUES (
        NEW.tenant_id, NEW.id, 'state_change',
        COALESCE(NULLIF(current_setting('aiq.actor_type', true), ''), 'system'),
        NULLIF(current_setting('aiq.actor_id', true), '')::uuid,
        CASE WHEN TG_OP = 'UPDATE' THEN jsonb_build_object('lifecycle_state', OLD.lifecycle_state, 'status', OLD.status) END,
        jsonb_build_object('lifecycle_state', NEW.lifecycle_state, 'status', NEW.status),
        NULLIF(current_setting('aiq.reason', true), ''),
        NULLIF(current_setting('aiq.request_id', true), ''));
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_vouchers_lifecycle_event ON vouchers;
CREATE TRIGGER trg_vouchers_lifecycle_event
AFTER INSERT OR UPDATE OF status, lifecycle_state ON vouchers
FOR EACH ROW EXECUTE FUNCTION vouchers_lifecycle_event();
