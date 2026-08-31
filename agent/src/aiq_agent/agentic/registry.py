"""Governance tool-registry client.

Fetches the per-agent whitelist of allowed MCP tool codes from the gateway's
`GET /agents/{id}/tools/allowed` endpoint (G2). The runtime uses this in place
of the hardcoded `allowed_tools` list so each agent only gets the tools it has
been explicitly granted AND whose `min_action_tier` matches the agent's
`action_tier`.

Falls back to `None` on any error so the caller can apply a legacy default
list — this keeps non-governance flows (chat, notes) working unchanged.
"""

from __future__ import annotations

import logging

import httpx

logger = logging.getLogger(__name__)


async def fetch_allowed_tool_codes(
    *,
    gateway_base: str,
    user_token: str,
    agent_id: str,
    timeout: float = 5.0,
) -> list[str] | None:
    """Return the list of tool codes the agent is allowed to invoke.

    Returns `None` (not `[]`) when the lookup fails so the caller knows to
    apply the legacy hardcoded list instead. An empty list means the agent
    legitimately has no grants.
    """
    url = f"{gateway_base.rstrip('/')}/runtime/agents/{agent_id}/authorization"
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            res = await client.get(
                url,
                headers={"Authorization": f"Bearer {user_token}"},
            )
        if res.status_code != 200:
            logger.warning(
                "Tool registry lookup for agent %s returned HTTP %s",
                agent_id,
                res.status_code,
            )
            return None
        body = res.json()
        codes = body.get("allowedToolCodes")
        if not isinstance(codes, list):
            return None
        return [str(c) for c in codes]
    except (httpx.RequestError, ValueError) as exc:
        logger.warning("Tool registry lookup failed for agent %s: %s", agent_id, exc)
        return None


async def fetch_agent_model(
    *,
    gateway_base: str,
    user_token: str,
    agent_id: str,
    timeout: float = 5.0,
) -> str | None:
    """Read the per-agent model override from the agents table.

    Returns None on any failure so the runner falls back to the CLI /
    settings default. The gateway exposes the full config document at
    GET /agents/{id}; we just pick the `model` field.
    """
    url = f"{gateway_base.rstrip('/')}/runtime/agents/{agent_id}/authorization"
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            res = await client.get(
                url,
                headers={"Authorization": f"Bearer {user_token}"},
            )
        if res.status_code != 200:
            return None
        body = res.json()
        import json

        config = json.loads(body.get("configJson") or "{}")
        model = config.get("model")
        return str(model) if model else None
    except (httpx.RequestError, ValueError) as exc:
        logger.warning("Agent config lookup failed for %s: %s", agent_id, exc)
        return None
