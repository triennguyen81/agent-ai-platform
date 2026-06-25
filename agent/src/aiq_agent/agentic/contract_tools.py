"""Contract domain MCP tools for /agent/run.

Two tools:
- extract_contract_fields(raw_text) — pure-LLM extraction (no I/O)
- save_contract(fields, raw_text?) — POST to Gateway

The extraction tool intentionally returns a structured-text wrapper instead
of asking the agent for JSON in a second LLM round-trip: the contract-
extractor skill teaches the model to produce JSON directly in its answer,
and the caller (web /api/contracts/upload) parses that JSON.
"""

from __future__ import annotations

import logging
from typing import Any

import httpx
from claude_agent_sdk import tool

logger = logging.getLogger(__name__)


def build_contract_tools(*, token: str, gateway_base: str) -> list[Any]:
    """Per-request factory bound to caller's JWT."""

    auth_headers = {"Authorization": f"Bearer {token}"}

    @tool(
        "extract_contract_fields",
        "Trích xuất các trường chính từ raw text của hợp đồng (sau OCR). "
        "Returns a JSON-shaped summary the caller can parse. The "
        "contract-extractor skill governs the schema.",
        {"raw_text": str},
    )
    async def extract_contract_fields(args: dict[str, Any]) -> dict[str, Any]:
        raw = str(args.get("raw_text", "")).strip()
        if not raw:
            return {"content": [{"type": "text", "text": "raw_text trống — không có gì để extract"}]}
        # The tool itself doesn't call the LLM — the agent SDK loop will
        # have the model produce the JSON when the skill is in context.
        # We just echo the input back so the model has it in the tool
        # response window. Keep first 50K chars max.
        snippet = raw[:50_000]
        return {
            "content": [
                {
                    "type": "text",
                    "text": (
                        f"Raw text hợp đồng ({len(raw)} ký tự). Hãy áp dụng skill "
                        "contract-extractor và trả về JSON đầy đủ theo schema. "
                        "Bắt buộc: title, party_a, party_b, dates, total_value, "
                        "currency, clauses[], tags[].\n\n"
                        "===== RAW TEXT =====\n"
                        f"{snippet}\n"
                        "===== END ====="
                    ),
                }
            ]
        }

    @tool(
        "save_contract",
        "Lưu hợp đồng đã trích xuất vào KB. fields là JSON object có các "
        "trường: title, party_a, party_b, signed_date, effective_date, "
        "end_date, total_value, currency, clauses, tags.",
        {"fields": dict, "raw_text": str},
    )
    async def save_contract(args: dict[str, Any]) -> dict[str, Any]:
        fields = args.get("fields") or {}
        raw_text = str(args.get("raw_text", ""))
        if not isinstance(fields, dict) or not fields.get("title"):
            return {
                "content": [
                    {"type": "text", "text": "fields.title là bắt buộc"}
                ]
            }

        # Normalize clauses payload — agent may send arbitrary shapes.
        clauses_in = fields.get("clauses") or []
        clauses_out: list[dict[str, Any]] = []
        if isinstance(clauses_in, list):
            for idx, c in enumerate(clauses_in, start=1):
                if not isinstance(c, dict):
                    continue
                clauses_out.append(
                    {
                        "ordinal": int(c.get("ordinal", idx)),
                        "heading": c.get("heading"),
                        "body": str(c.get("body", "")),
                        "hasDeadline": bool(c.get("has_deadline", False)),
                        "hasPenalty": bool(c.get("has_penalty", False)),
                    }
                )

        body = {
            "title": str(fields.get("title"))[:500],
            "partyA": fields.get("party_a"),
            "partyB": fields.get("party_b"),
            "signedDate": fields.get("signed_date"),
            "effectiveDate": fields.get("effective_date"),
            "endDate": fields.get("end_date"),
            "totalValue": fields.get("total_value"),
            "currency": fields.get("currency") or "VND",
            "sourcePdfPath": fields.get("source_pdf_path"),
            "rawText": raw_text or fields.get("raw_text"),
            "tags": [str(t) for t in (fields.get("tags") or []) if t],
            "clauses": clauses_out,
        }

        async with httpx.AsyncClient(timeout=20) as client:
            res = await client.post(
                f"{gateway_base}/contracts",
                headers=auth_headers,
                json=body,
            )

        if res.status_code not in (200, 201):
            return {
                "content": [
                    {
                        "type": "text",
                        "text": f"Lưu hợp đồng thất bại ({res.status_code}): {res.text[:200]}",
                    }
                ]
            }

        saved = res.json()
        contract = saved.get("contract") or {}
        return {
            "content": [
                {
                    "type": "text",
                    "text": (
                        f"✓ Đã lưu hợp đồng id={contract.get('id')} "
                        f"title={contract.get('title')!r} với "
                        f"{len(clauses_out)} clauses."
                    ),
                }
            ]
        }

    return [extract_contract_fields, save_contract]
