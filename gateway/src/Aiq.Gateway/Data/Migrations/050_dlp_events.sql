-- Sprint 7 Phase B: DLP audit events. INSERT-only (immutable log) để
-- đáp ứng compliance NĐ13/2023 + Spec AIQ "mọi hành động có nhật ký
-- bất biến". KHÔNG lưu PII value hay masked text — chỉ metadata:
-- count, types, length.

CREATE TABLE IF NOT EXISTS sim_dlp_events (
    id                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id          UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id            UUID REFERENCES users(id) ON DELETE SET NULL,
    request_id         TEXT,                        -- correlate với OpenTelemetry trace nếu có
    direction          TEXT NOT NULL,               -- 'scrub' (prompt out) hoặc 'unmask' (response in)
    pii_count          INT NOT NULL DEFAULT 0,     -- số PII bị mask / unmask
    pii_types          TEXT[] NOT NULL DEFAULT '{}', -- array ["Cmnd", "Phone", ...]
    text_length_chars  INT NOT NULL DEFAULT 0,
    capability         TEXT,                        -- capability code của agent call (nếu có)
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT dlp_direction_check CHECK (direction IN ('scrub', 'unmask'))
);

CREATE INDEX IF NOT EXISTS idx_dlp_events_tenant_date
    ON sim_dlp_events(tenant_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_dlp_events_user
    ON sim_dlp_events(user_id, created_at DESC)
    WHERE user_id IS NOT NULL;

COMMENT ON TABLE sim_dlp_events IS
    'Sprint 7 Phase B — audit log cho DLP scrub/unmask events. INSERT-only, không lưu PII values hay text thật.';
COMMENT ON COLUMN sim_dlp_events.direction IS
    '"scrub" = prompt sắp gửi ra LLM; "unmask" = response từ LLM trả về user';
