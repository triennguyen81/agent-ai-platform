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

    @tool(
        "get_contract",
        "Fetch a contract by id with all clauses (for checklist generation).",
        {"contract_id": str},
    )
    async def get_contract(args: dict[str, Any]) -> dict[str, Any]:
        cid = str(args.get("contract_id", "")).strip()
        if not cid:
            return {"content": [{"type": "text", "text": "contract_id required"}]}

        async with httpx.AsyncClient(timeout=10) as client:
            res = await client.get(
                f"{gateway_base}/contracts/{cid}",
                headers=auth_headers,
            )
        if res.status_code == 404:
            return {"content": [{"type": "text", "text": "Contract not found"}]}
        if res.status_code != 200:
            return {
                "content": [
                    {"type": "text", "text": f"Gateway returned {res.status_code}"}
                ]
            }
        return {"content": [{"type": "text", "text": res.text}]}

    @tool(
        "save_checklist_items",
        "Lưu danh sách checklist items cho 1 contract. items là list JSON "
        "với schema: {clause_ordinal, title, description, due_date, "
        "remind_days_before, assignee_email}. replace_existing=true sẽ xoá "
        "checklist cũ trước khi insert.",
        {"contract_id": str, "items": list, "replace_existing": bool},
    )
    async def save_checklist_items(args: dict[str, Any]) -> dict[str, Any]:
        contract_id = str(args.get("contract_id", "")).strip()
        items_in = args.get("items") or []
        replace = bool(args.get("replace_existing", True))

        if not contract_id:
            return {"content": [{"type": "text", "text": "contract_id required"}]}
        if not isinstance(items_in, list) or not items_in:
            return {"content": [{"type": "text", "text": "items must be a non-empty list"}]}

        # Fetch contract once to map clause_ordinal → clause_id
        async with httpx.AsyncClient(timeout=10) as client:
            contract_res = await client.get(
                f"{gateway_base}/contracts/{contract_id}",
                headers=auth_headers,
            )
        if contract_res.status_code != 200:
            return {
                "content": [
                    {
                        "type": "text",
                        "text": f"Cannot fetch contract for clause mapping: {contract_res.status_code}",
                    }
                ]
            }
        contract_data = contract_res.json()
        clauses_by_ordinal: dict[int, str] = {}
        for clause in contract_data.get("clauses", []):
            ord_val = clause.get("ordinal")
            if isinstance(ord_val, int):
                clauses_by_ordinal[ord_val] = clause.get("id")

        # Normalize each item — drop any without a title.
        normalized: list[dict[str, Any]] = []
        for raw in items_in:
            if not isinstance(raw, dict):
                continue
            title = str(raw.get("title", "")).strip()
            if not title:
                continue
            clause_ord = raw.get("clause_ordinal")
            clause_id = clauses_by_ordinal.get(clause_ord) if isinstance(clause_ord, int) else None
            normalized.append(
                {
                    "clauseId": clause_id,
                    "title": title[:500],
                    "description": raw.get("description"),
                    "dueDate": raw.get("due_date"),
                    "remindDaysBefore": int(raw.get("remind_days_before") or 7),
                    "assigneeEmail": raw.get("assignee_email"),
                }
            )

        if not normalized:
            return {"content": [{"type": "text", "text": "No valid items after normalization"}]}

        body = {"items": normalized, "replaceExisting": replace}
        async with httpx.AsyncClient(timeout=20) as client:
            res = await client.post(
                f"{gateway_base}/contracts/{contract_id}/checklist",
                headers=auth_headers,
                json=body,
            )
        if res.status_code not in (200, 201):
            return {
                "content": [
                    {
                        "type": "text",
                        "text": f"Save checklist failed ({res.status_code}): {res.text[:200]}",
                    }
                ]
            }
        payload = res.json()
        return {
            "content": [
                {
                    "type": "text",
                    "text": f"✓ Đã lưu {payload.get('total', len(normalized))} checklist items cho contract {contract_id}",
                }
            ]
        }

    return [
        extract_contract_fields,
        save_contract,
        get_contract,
        save_checklist_items,
    ]
