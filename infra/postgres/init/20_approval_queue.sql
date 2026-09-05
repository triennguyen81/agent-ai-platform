-- ---------------------------------------------------------------------------
-- Migration 20 — sim_approval_queue (HITL — Human-In-The-Loop)
--
-- Sprint 5: khi voucher extract phát hiện risk cao (invoice > 500M,
-- expense_over_30m_cash_payment, has_overdue_90d), route qua approval
-- queue thay vì tự apply. Kế toán trưởng duyệt qua /admin/approval-queue.
--
-- Voucher status flow:
--   extracted → (requires_l2_approval) → awaiting_approval
--     → approved → verified (auto-apply)
--     → rejected → final (không apply)
--
-- Idempotent. Apply thủ công qua psql -f.
-- ---------------------------------------------------------------------------

BEGIN;

CREATE TABLE IF NOT EXISTS sim_approval_queue (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    voucher_id      UUID NOT NULL REFERENCES vouchers(id) ON DELETE CASCADE,
    voucher_type    TEXT NOT NULL,           -- invoice_vat_out / invoice_vat_in / payment / bank_transfer / expense_claim
    document_number TEXT,
    total_amount    NUMERIC(18,2),
    currency        TEXT DEFAULT 'VND',
    trigger_reasons TEXT[] NOT NULL,         -- ['invoice_over_500m_requires_l2_approval', ...]
    approval_level  TEXT NOT NULL DEFAULT 'l2',  -- l2 = kế toán trưởng
    status          TEXT NOT NULL DEFAULT 'pending', -- pending / approved / rejected
    submitted_by    UUID REFERENCES users(id),
    reviewed_by     UUID REFERENCES users(id),
    reviewed_at     TIMESTAMPTZ,
    review_note     TEXT,
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (voucher_id)  -- 1 voucher chỉ có 1 entry pending tối đa
);

CREATE INDEX IF NOT EXISTS idx_approval_queue_tenant_status
    ON sim_approval_queue(tenant_id, status, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_approval_queue_pending
    ON sim_approval_queue(tenant_id, created_at DESC)
    WHERE status = 'pending';

COMMIT;

-- Verify
\echo ''
\echo '=== sim_approval_queue schema ==='
\d sim_approval_queue

\echo ''
\echo '=== rows (empty on fresh install) ==='
SELECT count(*) AS total FROM sim_approval_queue;
