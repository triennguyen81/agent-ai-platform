"""Durable usage/cost reporting for manifest-driven agent executions.

Hardening phase (docs/ACCOUNTING_AGENT_AUDIT.md, F-03): the Redis-backed
audit store (aiq_agent.audit.store) stays the realtime/capped log; this
module additionally reports each completed manifest-driven request to
Gateway's `POST /runtime/agents/{agentId}/usage`, which persists it into
Postgres `agent_usage_events` — the durable reporting source the audit
called for.

Fire-and-forget: a failure to report usage must never fail the user's
chat/agent-run request — same non-fatal posture the existing Redis audit
write already has (see api/routes.py's `if audit is not None` guards).

Uses the manifest's own short-lived toolExecutionToken (never a new or
broader credential) — Gateway derives tenant_id/department_id/actor_id from
that token's authenticated principal server-side, exactly like every other
resource_or_tool-gated call this runtime already makes (HITL evaluate). This
module never sends tenant/user identity as a request field.
"""

from __future__ import annotations

import logging
import uuid

import httpx
from opentelemetry import trace

from aiq_agent.llm.base import LlmUsage
from aiq_agent.observability.llm_usage import COST_SOURCE_CLAUDE_CLI
from aiq_agent.runtime.executors import ExecutionResult
from aiq_agent.runtime.manifest import ExecutionManifest

logger = logging.getLogger(__name__)


def current_trace_id() -> uuid.UUID | None:
    """The real, active OpenTelemetry trace id, or None when tracing isn't
    active (e.g. in tests) — never a fabricated substitute."""
    ctx = trace.get_current_span().get_span_context()
    if not ctx.is_valid:
        return None
    return uuid.UUID(int=ctx.trace_id)


async def report_usage(
    *,
    manifest: ExecutionManifest,
    result: ExecutionResult,
    event_type: str,
    total_latency_ms: int,
    gateway_base: str,
    timeout: float = 5.0,
    request_id: uuid.UUID | None = None,
) -> None:
    """Best-effort, non-blocking usage report. Swallows and logs every
    failure — never raises, so it can never turn a successful chat/agent-run
    into a failed HTTP response."""
    token = manifest.toolExecutionToken
    if token is None:
        # No minted tool_execution token means this is the departmentless
        # super_admin test/preview path (see ExecutionManifest.toolExecution
        # Token docstring) — not real tenant usage, so nothing to persist.
        return

    # llmLatencyMs is a derived approximation, not an independent
    # measurement — neither provider exposes a per-turn LLM-only span from
    # inside its SDK/HTTP black box (see ExecutionResult.rag_latency_ms /
    # tool_latency_ms docstring). Documented here and in the migration, not
    # silently presented as a real measurement.
    llm_latency_ms = max(
        total_latency_ms - (result.rag_latency_ms or 0) - (result.tool_latency_ms or 0), 0
    )
    trace_id = current_trace_id()
    payload = {
        "eventType": event_type,
        "agentVersionId": str(manifest.publishedVersionId),
        "traceId": str(trace_id) if trace_id is not None else None,
        "provider": manifest.provider,
        "model": manifest.model,
        "inputTokens": result.input_tokens,
        "outputTokens": result.output_tokens,
        "costAmount": result.estimated_cost_usd,
        "totalLatencyMs": total_latency_ms,
        "llmLatencyMs": llm_latency_ms,
        "toolLatencyMs": result.tool_latency_ms,
        "ragLatencyMs": result.rag_latency_ms,
        # Real distinct API calls (tool rounds), not executions.
        "llmCallCount": result.llm_call_count,
        "cacheCreationInputTokens": result.cache_creation_input_tokens,
        "cacheReadInputTokens": result.cache_read_input_tokens,
        "numTurns": result.num_turns,
        "costSource": result.cost_source,
        "requestId": str(request_id) if request_id is not None else None,
        # Sizes/counts only — never prompt, skill, knowledge or tool content.
        "metrics": dict(result.prompt_metrics),
    }
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            res = await client.post(
                f"{gateway_base.rstrip('/')}/runtime/agents/{manifest.agent.id}/usage",
                headers={"Authorization": f"Bearer {token}"},
                json=payload,
            )
        if res.status_code >= 400:
            logger.warning(
                "Usage report rejected: status=%s body=%s", res.status_code, res.text[:200]
            )
    except httpx.RequestError as exc:
        logger.warning("Usage report failed: %s: %s", exc.__class__.__name__, exc)


async def report_unscoped_usage(
    *,
    token: str,
    event_type: str,
    usage: LlmUsage | None,
    provider: str | None,
    model: str | None,
    total_latency_ms: int,
    gateway_base: str,
    request_id: uuid.UUID | None = None,
    timeout: float = 5.0,
) -> None:
    """Usage of an LLM call that has no agent manifest (plain / PEC chat,
    the legacy agentic runtime). Reported with the caller's own token —
    Gateway derives tenant/department/actor from it, never from this body.
    Best effort, never raises."""
    if usage is None:
        return
    trace_id = current_trace_id()
    payload = {
        "eventType": event_type,
        "traceId": str(trace_id) if trace_id is not None else None,
        "requestId": str(request_id) if request_id is not None else None,
        "provider": provider,
        "model": model,
        "inputTokens": usage.input_tokens,
        "outputTokens": usage.output_tokens,
        "cacheCreationInputTokens": usage.cache_creation_input_tokens,
        "cacheReadInputTokens": usage.cache_read_input_tokens,
        "costAmount": usage.cost_usd,
        "costSource": COST_SOURCE_CLAUDE_CLI if usage.cost_usd is not None else None,
        "llmCallCount": usage.llm_call_count,
        "totalLatencyMs": total_latency_ms,
    }
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            res = await client.post(
                f"{gateway_base.rstrip('/')}/runtime/usage",
                headers={"Authorization": f"Bearer {token}"},
                json=payload,
            )
        if res.status_code >= 400:
            logger.warning("Unscoped usage report rejected: status=%s", res.status_code)
    except httpx.RequestError as exc:
        logger.warning("Unscoped usage report failed: %s: %s", exc.__class__.__name__, exc)
