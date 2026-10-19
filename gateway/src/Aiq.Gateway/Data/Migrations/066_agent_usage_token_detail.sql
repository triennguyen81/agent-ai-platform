-- Token usage audit (docs/audits/TOKEN_USAGE_AUDIT.md, Phase 1 of
-- docs/audits/TOKEN_OPTIMIZATION_PLAN.md). Additive and nullable only.
--
-- Token semantics (Anthropic): input_tokens is the UNCACHED prompt part;
-- the full prompt is input_tokens + cache_creation_input_tokens +
-- cache_read_input_tokens. The three are disjoint — summing them never
-- double counts. cost_amount is the provider-reported cost (already priced
-- per token class); it must never be recomputed from these columns.
ALTER TABLE agent_usage_events
    ADD COLUMN IF NOT EXISTS cache_creation_input_tokens BIGINT,
    ADD COLUMN IF NOT EXISTS cache_read_input_tokens     BIGINT,
    ADD COLUMN IF NOT EXISTS num_turns                   INTEGER,
    ADD COLUMN IF NOT EXISTS request_id                  UUID,
    ADD COLUMN IF NOT EXISTS cost_source                 TEXT;

ALTER TABLE agent_usage_events DROP CONSTRAINT IF EXISTS agent_usage_cache_tokens_check;
ALTER TABLE agent_usage_events ADD CONSTRAINT agent_usage_cache_tokens_check CHECK (
    (cache_creation_input_tokens IS NULL OR cache_creation_input_tokens >= 0)
    AND (cache_read_input_tokens IS NULL OR cache_read_input_tokens >= 0)
    AND (num_turns IS NULL OR num_turns >= 0)
    AND (llm_call_count IS NULL OR llm_call_count >= 0));

CREATE INDEX IF NOT EXISTS idx_agent_usage_events_request_id
    ON agent_usage_events (request_id) WHERE request_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_agent_usage_event_type_time
    ON agent_usage_events (event_type, occurred_at DESC);

COMMENT ON COLUMN agent_usage_events.input_tokens IS
    'Uncached prompt tokens only (Anthropic semantics). Full prompt = input + cache_creation + cache_read.';
COMMENT ON COLUMN agent_usage_events.cache_creation_input_tokens IS
    'Prompt tokens written to the prompt cache (priced above uncached input).';
COMMENT ON COLUMN agent_usage_events.cache_read_input_tokens IS
    'Prompt tokens read from the prompt cache (priced below uncached input).';
COMMENT ON COLUMN agent_usage_events.llm_call_count IS
    'Distinct LLM API calls (tool rounds / pages) this row covers, counted by API message id. '
    'Rows written before migration 066 always carry 1.';
COMMENT ON COLUMN agent_usage_events.num_turns IS 'Agent SDK turn count reported by the CLI ResultMessage.';
COMMENT ON COLUMN agent_usage_events.request_id IS 'One id per runtime HTTP request; groups the rows of one request.';
COMMENT ON COLUMN agent_usage_events.cost_source IS
    'Where cost_amount comes from, e.g. claude_cli_total_cost_usd (CLI estimate; on subscription auth this is '
    'not an invoice amount). NULL cost_amount = unknown, never zero-filled.';
COMMENT ON COLUMN agent_usage_events.metadata IS
    'Numeric size/count metrics only (prompt composition, tool result sizes) — never prompt or document content.';
