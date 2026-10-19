"""In-process MCP tools for the agentic runner.

Tool factories take the per-request context (user JWT, gateway base URL)
and return Claude Agent SDK `@tool`-decorated functions that close over it.
"""

from __future__ import annotations

import json
import logging
import re
from typing import Any

import httpx
from claude_agent_sdk import tool

from aiq_agent.agentic.accounting_tools import build_accounting_tools
from aiq_agent.agentic.contract_tools import build_contract_tools
from aiq_agent.agentic.voucher_tools import build_voucher_tools
from aiq_agent.security.ssrf import OutboundUrlError, safe_get

logger = logging.getLogger(__name__)

# Defensive caps so a misbehaving model can't ingest gigabytes. An oversized
# page is now rejected (response_too_large) rather than silently truncated,
# so the cap is 1 MB to keep ordinary HTML pages fetchable.
_FETCH_MAX_BYTES = 1024 * 1024  # 1 MB
_FETCH_TIMEOUT_S = 15.0  # overall deadline: DNS + every redirect hop + body
_FETCH_MAX_REDIRECTS = 3
_FETCH_ALLOWED_PORTS = frozenset({80, 443})
_FETCH_TEXT_TYPES = frozenset(
    {
        "application/xhtml+xml",
        "application/xml",
        "application/json",
        "application/ld+json",
        "application/rss+xml",
        "application/atom+xml",
    }
)


def _is_text_content_type(content_type: str) -> bool:
    media_type = content_type.split(";", 1)[0].strip().lower()
    return not media_type or media_type.startswith("text/") or media_type in _FETCH_TEXT_TYPES


def _fetch_error(code: str, message: str) -> dict[str, Any]:
    payload = {"ok": False, "error": code, "message": message}
    return {"content": [{"type": "text", "text": json.dumps(payload)}], "is_error": True}


async def fetch_public_url(url: str) -> dict[str, Any]:
    """Body of the `fetch_url` tool. Every request — and every redirect hop —
    goes through the shared SSRF boundary (aiq_agent.security.ssrf); no user
    JWT, gateway token or cookie is ever sent."""
    url = url.strip()
    try:
        fetched = await safe_get(
            url,
            timeout=_FETCH_TIMEOUT_S,
            max_bytes=_FETCH_MAX_BYTES,
            max_redirects=_FETCH_MAX_REDIRECTS,
            allowed_ports=_FETCH_ALLOWED_PORTS,
            accept_content_type=_is_text_content_type,
            headers={"User-Agent": "AIQ-Agent/0.1 (+url-ingest)"},
        )
    except OutboundUrlError as exc:
        logger.warning(
            "fetch_url rejected code=%s url=%r detail=%s", exc.code, url[:500], exc.detail
        )
        return _fetch_error(exc.code, exc.message)

    if fetched.status_code >= 400:
        return _fetch_error(
            "http_error", f"HTTP {fetched.status_code} fetching {fetched.final_url}"
        )

    raw = fetched.content.decode("utf-8", errors="replace")
    # Cheap HTML strip — the model handles the rest. Nothing is executed.
    text = re.sub(r"<script[\s\S]*?</script>", " ", raw, flags=re.IGNORECASE)
    text = re.sub(r"<style[\s\S]*?</style>", " ", text, flags=re.IGNORECASE)
    text = re.sub(r"<[^>]+>", " ", text)
    text = re.sub(r"\s+", " ", text).strip()

    return {
        "content": [
            {
                "type": "text",
                "text": f"URL: {fetched.final_url}\nLength: {len(text)} chars\n\n{text[:20000]}",
            }
        ]
    }


def build_tools(*, token: str, gateway_base: str) -> list[Any]:
    """Build a fresh tool set bound to the current user + gateway base.

    Each call to /agent/run gets its own tools so the JWT closure can't
    leak across users.
    """

    auth_headers = {"Authorization": f"Bearer {token}"}

    @tool(
        "search_notes",
        "Search the user's saved notes (knowledge base). Returns up to N notes.",
        {"query": str, "limit": int},
    )
    async def search_notes(args: dict[str, Any]) -> dict[str, Any]:
        query = str(args.get("query", "")).strip()
        limit = int(args.get("limit", 20))
        async with httpx.AsyncClient(timeout=10) as client:
            res = await client.get(
                f"{gateway_base}/notes",
                headers=auth_headers,
            )
        if res.status_code != 200:
            return {
                "content": [
                    {"type": "text", "text": f"Notes API returned {res.status_code}"}
                ]
            }
        payload = res.json()
        items = payload.get("items", [])
        # Simple in-memory keyword filter — full-text search lives at the
        # Postgres layer, not exposed yet. Phase 6 (Qdrant) replaces this.
        if query:
            q = query.lower()
            items = [
                n
                for n in items
                if q in (n.get("title", "") + " " + n.get("content", "")).lower()
            ]
        items = items[: max(1, min(limit, 50))]
        summary = [
            {
                "id": n.get("id"),
                "title": n.get("title"),
                "tags": n.get("tags") or [],
                "snippet": (n.get("content") or "")[:300],
            }
            for n in items
        ]
        return {
            "content": [
                {
                    "type": "text",
                    "text": (
                        f"Found {len(summary)} notes for '{query}':\n"
                        + "\n".join(
                            f"- [{i+1}] {n['title']} (tags: {', '.join(n['tags']) or 'none'})"
                            for i, n in enumerate(summary)
                        )
                        + "\n\nUse get_note(id) for full content."
                    ),
                },
                {"type": "text", "text": str(summary)},
            ]
        }

    @tool(
        "get_note",
        "Fetch a single note by id with full content.",
        {"note_id": str},
    )
    async def get_note(args: dict[str, Any]) -> dict[str, Any]:
        note_id = str(args.get("note_id", "")).strip()
        if not note_id:
            return {"content": [{"type": "text", "text": "note_id is required"}]}
        async with httpx.AsyncClient(timeout=10) as client:
            res = await client.get(
                f"{gateway_base}/notes/{note_id}",
                headers=auth_headers,
            )
        if res.status_code == 404:
            return {"content": [{"type": "text", "text": "Note not found"}]}
        if res.status_code != 200:
            return {
                "content": [
                    {"type": "text", "text": f"Notes API returned {res.status_code}"}
                ]
            }
        return {"content": [{"type": "text", "text": res.text}]}

    @tool(
        "save_note",
        "Save a new note into the user's knowledge base.",
        {"title": str, "content": str, "tags": list},
    )
    async def save_note(args: dict[str, Any]) -> dict[str, Any]:
        title = str(args.get("title", "")).strip()
        content = str(args.get("content", ""))
        tags = args.get("tags") or []
        if not isinstance(tags, list):
            tags = []
        if not title:
            return {"content": [{"type": "text", "text": "title is required"}]}

        body = {"title": title[:300], "content": content, "tags": tags}
        async with httpx.AsyncClient(timeout=15) as client:
            res = await client.post(
                f"{gateway_base}/notes",
                headers=auth_headers,
                json=body,
            )
        if res.status_code not in (200, 201):
            return {
                "content": [
                    {
                        "type": "text",
                        "text": f"Save failed ({res.status_code}): {res.text[:200]}",
                    }
                ]
            }
        saved = res.json()
        return {
            "content": [
                {
                    "type": "text",
                    "text": f"Saved note id={saved.get('id')} title={saved.get('title')!r}",
                }
            ]
        }

    @tool(
        "fetch_url",
        "Fetch a public http(s) web page (ports 80/443 only) and return its text "
        f"content (HTML stripped). Text content types only. Max {_FETCH_MAX_BYTES // 1024} KB.",
        {"url": str},
    )
    async def fetch_url(args: dict[str, Any]) -> dict[str, Any]:
        return await fetch_public_url(str(args.get("url", "")))

    accounting_tools = build_accounting_tools(token=token, gateway_base=gateway_base)
    contract_tools = build_contract_tools(token=token, gateway_base=gateway_base)
    voucher_tools = build_voucher_tools(token=token, gateway_base=gateway_base)
    return [
        search_notes,
        get_note,
        save_note,
        fetch_url,
        *accounting_tools,
        *contract_tools,
        *voucher_tools,
    ]
