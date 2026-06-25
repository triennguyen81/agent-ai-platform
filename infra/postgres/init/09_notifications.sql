-- ----------------------------------------------------------------------------
-- Phase 3 — Notifications
--
-- Inbox for in-app alerts about contract / checklist events. A daily Hangfire
-- job (ContractDeadlineJob) scans contract_checklist_items and inserts one
-- row per user per checklist item that needs attention. Future channels
-- (email, telegram) can read the same table.
--
-- Dedupe strategy: (user_id, ref_id, kind) — we never want the daily job to
-- spam a user with the same overdue item every morning. Job uses ON CONFLICT
-- DO NOTHING so re-runs are idempotent.
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS notifications (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    kind        TEXT NOT NULL,        -- contract_due / checklist_overdue / checklist_due_soon / system
    ref_type    TEXT,                 -- 'contract' / 'checklist_item'
    ref_id      UUID,                 -- foreign id (no FK — survives deletes)
    title       TEXT NOT NULL,
    body        TEXT,
    read_at     TIMESTAMPTZ,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_notifications_user_unread
    ON notifications(user_id, created_at DESC)
    WHERE read_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_notifications_user_recent
    ON notifications(user_id, created_at DESC);

-- Dedupe key: same kind for the same ref to the same user. Allows the cron
-- to be re-run safely (insert is no-op if row already exists).
CREATE UNIQUE INDEX IF NOT EXISTS uq_notifications_dedupe
    ON notifications(user_id, kind, ref_id)
    WHERE ref_id IS NOT NULL;
