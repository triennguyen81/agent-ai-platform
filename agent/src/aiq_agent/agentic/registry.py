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
    url = f"{gateway_base.rstrip('/')}/agents/{agent_id}/tools/allowed"
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
        codes = body.get("codes")
        if not isinstance(codes, list):
            return None
        return [str(c) for c in codes]
    except (httpx.RequestError, ValueError) as exc:
        logger.warning("Tool registry lookup failed for agent %s: %s", agent_id, exc)
        return None
