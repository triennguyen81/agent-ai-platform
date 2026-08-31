"""Bounded Redis-backed audit log for agent events.

Storage:
    KEY:   aiq:audit
    TYPE:  LIST of JSON entries (newest at left via LPUSH + LTRIM cap)
    TTL:   none (capped by length)

Each entry: {ts, event, user_prefix, name, ms, ok, detail?}
"""

from __future__ import annotations

import json
import logging
import time
from dataclasses import asdict, dataclass
from typing import Any

logger = logging.getLogger(__name__)

DEFAULT_KEY = "aiq:audit"
DEFAULT_CAP = 5_000


@dataclass(frozen=True, slots=True)
class AuditEntry:
    ts: float  # unix seconds
    event: str  # "tool" | "skill" | "chat" | "agentic"
    user_prefix: str  # first 12 chars of user id, or "anon"
    name: str  # tool name / skill name / event tag
    ms: int  # duration milliseconds
    ok: bool
    detail: str | None = None
    user_id: str | None = None
    tenant_id: str | None = None
    agent_id: str | None = None
    version: int | None = None
    provider: str | None = None
    model: str | None = None
    # Phase 5E: usage/cost/audit. Identifiers and counts only — never prompt
    # text, tool arguments, tool output content, or knowledge content.
    input_tokens: int | None = None
    output_tokens: int | None = None
    estimated_cost_usd: float | None = None
    tools_used: list[str] | None = None
    delegations: list[str] | None = None
    workflow_id: str | None = None


class AuditStore:
    """Thin wrapper around Redis LIST for capped append + range read."""

    def __init__(self, *, redis: Any, key: str = DEFAULT_KEY, cap: int = DEFAULT_CAP) -> None:
        self._redis = redis
        self._key = key
        self._cap = cap

    async def write(
        self,
        *,
        event: str,
        name: str,
        user_prefix: str,
        ms: int,
        ok: bool,
        detail: str | None = None,
        user_id: str | None = None,
        tenant_id: str | None = None,
        agent_id: str | None = None,
        version: int | None = None,
        provider: str | None = None,
        model: str | None = None,
        input_tokens: int | None = None,
        output_tokens: int | None = None,
        estimated_cost_usd: float | None = None,
        tools_used: list[str] | None = None,
        delegations: list[str] | None = None,
        workflow_id: str | None = None,
    ) -> None:
        entry = AuditEntry(
            ts=time.time(),
            event=event,
            user_prefix=(user_prefix or "anon")[:12],
            name=name[:120],
            ms=ms,
            ok=ok,
            detail=(detail[:300] if detail else None),
            user_id=user_id,
            tenant_id=tenant_id,
            agent_id=agent_id,
            version=version,
            provider=provider,
            model=model,
            input_tokens=input_tokens,
            output_tokens=output_tokens,
            estimated_cost_usd=estimated_cost_usd,
            tools_used=(tools_used[:50] if tools_used else None),
            delegations=(delegations[:50] if delegations else None),
            workflow_id=workflow_id,
        )
        try:
            payload = json.dumps(asdict(entry))
            pipe = self._redis.pipeline()
            pipe.lpush(self._key, payload)
            pipe.ltrim(self._key, 0, self._cap - 1)
            await pipe.execute()
        except Exception as exc:  # noqa: BLE001 — audit must never break callers
            logger.warning("Audit write failed: %s", exc)

    async def list(
        self,
        *,
        limit: int = 100,
        event: str | None = None,
        tenant_id: str | None = None,
    ) -> list[AuditEntry]:
        try:
            raw = await self._redis.lrange(self._key, 0, max(0, limit * 4 - 1))
        except Exception as exc:  # noqa: BLE001
            logger.warning("Audit read failed: %s", exc)
            return []

        out: list[AuditEntry] = []
        for item in raw:
            try:
                data = json.loads(item)
                if event and data.get("event") != event:
                    continue
                if tenant_id and data.get("tenant_id") != tenant_id:
                    continue
                out.append(AuditEntry(**data))
                if len(out) >= limit:
                    break
            except (json.JSONDecodeError, TypeError) as exc:
                logger.debug("Skipping bad audit entry: %s", exc)
        return out


def build_audit_store(*, redis: Any) -> AuditStore:
    """Factory — `redis` can be the redis.asyncio client already configured for memory."""
    return AuditStore(redis=redis)
