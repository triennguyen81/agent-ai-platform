-- Contract payment (early-settlement) discount — structured terms + audit.
--
-- A. contract_payment_discount_terms: the ONLY authoritative source of a
--    payment-discount term. Owned by the Contract, revisioned: a change
--    never edits a row, it supersedes the current revision and inserts the
--    next one, so a historical calculation always points at the exact
--    revision it used. Narrow first version (Xuly-ctkt.pdf tr.6 only says
--    "lấy chiết khấu thanh toán từ HĐ, validate time & amount"):
--      early_payment, X% if received within N days of the invoice date.
--    No formulas, no other bases, no GL accounts on the contract.
-- B. payment_discount_decisions: audit of every ADMIN confirmation attempt
--    (the deterministic re-evaluation and its outcome). No document content.
--
-- Contracts without a term are unaffected.

CREATE TABLE IF NOT EXISTS contract_payment_discount_terms (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id        UUID NOT NULL,
    department_id    UUID NOT NULL,
    contract_id      UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
    revision         INT  NOT NULL CHECK (revision >= 1),
    enabled          BOOLEAN NOT NULL,
    discount_type    TEXT NOT NULL DEFAULT 'early_payment' CHECK (discount_type = 'early_payment'),
    rate_percent     NUMERIC(7,4) NOT NULL CHECK (rate_percent > 0 AND rate_percent < 100),
    basis            TEXT NOT NULL CHECK (basis IN ('invoice_date')),
    days_count       INT  NOT NULL CHECK (days_count BETWEEN 0 AND 3650),
    currency         TEXT NOT NULL,
    source           TEXT NOT NULL DEFAULT 'manual' CHECK (source IN ('manual')),
    source_reference TEXT,
    note             TEXT,
    created_by       UUID NOT NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    superseded_at    TIMESTAMPTZ,
    superseded_by    UUID,
    UNIQUE (contract_id, revision)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_contract_discount_term_current
    ON contract_payment_discount_terms (contract_id) WHERE superseded_at IS NULL;

-- A revision is immutable: only the supersede stamp may be set, once.
CREATE OR REPLACE FUNCTION protect_contract_discount_term_revision() RETURNS trigger AS $$
BEGIN
    IF OLD.superseded_at IS NOT NULL
       OR NEW.id IS DISTINCT FROM OLD.id OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
       OR NEW.department_id IS DISTINCT FROM OLD.department_id OR NEW.contract_id IS DISTINCT FROM OLD.contract_id
       OR NEW.revision IS DISTINCT FROM OLD.revision OR NEW.enabled IS DISTINCT FROM OLD.enabled
       OR NEW.discount_type IS DISTINCT FROM OLD.discount_type OR NEW.rate_percent IS DISTINCT FROM OLD.rate_percent
       OR NEW.basis IS DISTINCT FROM OLD.basis OR NEW.days_count IS DISTINCT FROM OLD.days_count
       OR NEW.currency IS DISTINCT FROM OLD.currency OR NEW.source IS DISTINCT FROM OLD.source
       OR NEW.source_reference IS DISTINCT FROM OLD.source_reference OR NEW.note IS DISTINCT FROM OLD.note
       OR NEW.created_by IS DISTINCT FROM OLD.created_by OR NEW.created_at IS DISTINCT FROM OLD.created_at
       OR NEW.superseded_at IS NULL THEN
        RAISE EXCEPTION 'contract_discount_term_revision_immutable';
    END IF;
    RETURN NEW;
END $$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_protect_contract_discount_term_revision ON contract_payment_discount_terms;
CREATE TRIGGER trg_protect_contract_discount_term_revision
    BEFORE UPDATE ON contract_payment_discount_terms
    FOR EACH ROW EXECUTE FUNCTION protect_contract_discount_term_revision();

CREATE TABLE IF NOT EXISTS payment_discount_decisions (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL,
    department_id       UUID NOT NULL,
    source_type         TEXT NOT NULL CHECK (source_type IN ('voucher', 'bank_statement_line')),
    voucher_id          UUID NOT NULL,
    bank_transaction_id UUID,
    ar_invoice_id       UUID NOT NULL,
    contract_id         UUID,
    term_id             UUID,
    term_revision       INT,
    rate_percent        NUMERIC(7,4),
    basis               TEXT,
    basis_date          DATE,
    qualifying_deadline DATE,
    receipt_date        DATE,
    cash_amount         NUMERIC(18,2) NOT NULL,
    open_amount         NUMERIC(18,2) NOT NULL,
    calculated_discount NUMERIC(18,2) NOT NULL,
    eligible            BOOLEAN NOT NULL,
    reason_code         TEXT NOT NULL,
    outcome             TEXT NOT NULL CHECK (outcome IN (
                            'blocked_accounting_policy_missing', 'rejected_stale', 'rejected_not_eligible')),
    conflict_code       TEXT,
    actor_id            UUID NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_payment_discount_decisions_invoice
    ON payment_discount_decisions (tenant_id, ar_invoice_id, created_at DESC);
