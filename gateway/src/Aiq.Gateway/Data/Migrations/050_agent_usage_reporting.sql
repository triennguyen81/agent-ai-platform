-- Accounting Agent hardening (docs/ACCOUNTING_AGENT_AUDIT.md, F-03): the
-- Redis-backed audit store (agent/src/aiq_agent/audit/store.py) is a capped,
-- realtime-only list — this migration makes agent_usage_events (created in
-- 024_acl_v2_scope_foundations.sql but never written to — 0 rows in
-- production, confirmed during the audit) the durable, queryable reporting
-- source the audit called for. Columns added are additive/nullable so the
-- existing (empty) table and its FKs are untouched.
ALTER TABLE agent_usage_events
    ADD COLUMN IF NOT EXISTS trace_id         UUID,
    ADD COLUMN IF NOT EXISTS provider         TEXT,
    ADD COLUMN IF NOT EXISTS model            TEXT,
    ADD COLUMN IF NOT EXISTS total_latency_ms INTEGER,
    ADD COLUMN IF NOT EXISTS llm_latency_ms   INTEGER,
    ADD COLUMN IF NOT EXISTS tool_latency_ms  INTEGER,
    ADD COLUMN IF NOT EXISTS rag_latency_ms   INTEGER,
    ADD COLUMN IF NOT EXISTS llm_call_count   INTEGER;

CREATE INDEX IF NOT EXISTS idx_agent_usage_events_trace_id
    ON agent_usage_events (trace_id) WHERE trace_id IS NOT NULL;

COMMENT ON COLUMN agent_usage_events.trace_id IS
    'OpenTelemetry trace id for this request, when tracing is active; NULL otherwise (never fabricated).';
COMMENT ON COLUMN agent_usage_events.llm_call_count IS
    'Number of top-level executor.execute() invocations this usage row represents (always 1 today — the '
    'Claude Agent SDK''s own internal per-turn call count inside one query() is not observable from '
    'application code, per the audit''s NOT VERIFIED finding on this point; not fabricated here either).';
