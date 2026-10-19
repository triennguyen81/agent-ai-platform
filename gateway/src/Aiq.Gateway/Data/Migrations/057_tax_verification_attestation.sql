-- Tax verification gate hardening.
--
-- Before: any department member (in practice the Agent, acting with the
-- user's token after an LLM "read" a truncated portal response) could flip
-- tax_verification_status to 'verified' with no evidence and no actor, and
-- the flag survived later edits of the invoice's own identity fields.
--
-- After:
--   * 'verified' is set only by a department ADMIN, with structured evidence
--     whose invoice identities the Gateway compares to the voucher's current
--     data, and never by the expense claim's own submitter;
--   * the Agent (or anyone) may only PROPOSE ('pending_review');
--   * the verification is bound to a fingerprint of the invoice identities;
--     a later change of those fields invalidates it;
--   * every transition is kept in an append-only event table.

ALTER TABLE vouchers
  ADD COLUMN IF NOT EXISTS tax_verified_by UUID REFERENCES users(id) ON DELETE SET NULL,
  ADD COLUMN IF NOT EXISTS tax_verification_fingerprint TEXT,
  ADD COLUMN IF NOT EXISTS tax_verification_evidence JSONB;

ALTER TABLE vouchers DROP CONSTRAINT IF EXISTS chk_tax_verification_status;
DO $$ BEGIN
  ALTER TABLE vouchers ADD CONSTRAINT chk_tax_verification_status
    CHECK (tax_verification_status IS NULL
           OR tax_verification_status IN ('pending_review', 'verified', 'invalid', 'unavailable'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- A 'verified' row must name its ADMIN, its evidence and its fingerprint.
-- NOT VALID: enforced for every new write; legacy rows are fixed just below.
DO $$ BEGIN
  ALTER TABLE vouchers ADD CONSTRAINT chk_tax_verified_is_attested
    CHECK (tax_verification_status IS DISTINCT FROM 'verified'
           OR (tax_verified_by IS NOT NULL AND tax_verification_evidence IS NOT NULL
               AND tax_verification_fingerprint IS NOT NULL))
    NOT VALID;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE TABLE IF NOT EXISTS voucher_tax_verification_events (
  id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id      UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  department_id  UUID,
  voucher_id     UUID NOT NULL REFERENCES vouchers(id) ON DELETE CASCADE,
  event          TEXT NOT NULL,
  actor_type     TEXT NOT NULL,
  actor_id       UUID REFERENCES users(id) ON DELETE SET NULL,
  actor_role     TEXT,
  source         TEXT,
  status_before  TEXT,
  status_after   TEXT,
  fingerprint    TEXT,
  evidence       JSONB,
  reason         TEXT,
  created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
DO $$ BEGIN
  ALTER TABLE voucher_tax_verification_events ADD CONSTRAINT chk_tax_event
    CHECK (event IN ('proposed', 'verified', 'invalid', 'unavailable', 'invalidated', 'legacy_unattested'));
  ALTER TABLE voucher_tax_verification_events ADD CONSTRAINT chk_tax_event_actor
    CHECK (actor_type IN ('user', 'agent', 'system'));
  -- admin_manual: an ADMIN attestation with evidence (the only VERIFIED source
  -- today — AIQ has no trusted server-side tax integration). agent_proposal:
  -- an Agent/LLM lookup, never authoritative. system: invalidation/migration.
  ALTER TABLE voucher_tax_verification_events ADD CONSTRAINT chk_tax_event_source
    CHECK (source IS NULL OR source IN ('admin_manual', 'agent_proposal', 'user_proposal', 'user_report', 'system'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
CREATE INDEX IF NOT EXISTS idx_tax_events_voucher ON voucher_tax_verification_events (voucher_id, created_at);

-- Legacy 'verified' rows were never attested by an ADMIN (the only writer
-- was the Agent tool). Downgrade them to a proposal, keeping the trace.
WITH legacy AS (
  UPDATE vouchers SET
      tax_verification_status = 'pending_review',
      tct_verified = FALSE,
      tax_verification_reason = 'Xác thực cũ không có bằng chứng/ADMIN — cần ADMIN xác nhận lại'
  WHERE tax_verification_status = 'verified' AND tax_verified_by IS NULL
  RETURNING id, tenant_id, department_id
)
INSERT INTO voucher_tax_verification_events (tenant_id, department_id, voucher_id, event, actor_type, source, status_before, status_after, reason)
SELECT tenant_id, department_id, id, 'legacy_unattested', 'system', 'system', 'verified', 'pending_review',
       'migration 057: verified without ADMIN attestation'
FROM legacy;

-- tct_verified (legacy boolean) mirrors only an attested verification.
UPDATE vouchers SET tct_verified = FALSE
WHERE tct_verified AND tax_verification_status IS DISTINCT FROM 'verified';

-- Every remaining row now satisfies the attestation rule.
ALTER TABLE vouchers VALIDATE CONSTRAINT chk_tax_verified_is_attested;
