"""Contract domain MCP tools for /agent/run.

- extract_contract_fields(raw_text) — pure-LLM extraction (no I/O)
- save_contract(fields, raw_text?) — POST to Gateway, then immediately
  chains into POST .../process (classify/extract/validate/schedule/
  milestones/alerts) so the Chat flow never stalls at RECEIVED after a
  save — see docs/accagent-spec-v0.1-quy-trinh-nghiep-vu.md "Luồng 01".
  Gateway (IContractRepository.ProcessAsync) owns every deterministic rule;
  this tool only maps the model's extracted fields onto the exact request
  shape ProcessAsync already expects, never re-implements a rule itself.

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

from aiq_agent.agentic.contract_normalize import (
    coerce_number,
    coerce_positive_int,
    normalize_payment_percentages,
    normalize_vat_rate,
    repair_milestone,
    repair_payment_term,
    split_clauses_from_raw_text,
    validate_extracted_payload,
)

logger = logging.getLogger(__name__)


def _derive_process_parties(fields: dict[str, Any]) -> list[dict[str, Any]]:
    """Structured `parties[]` if the model extracted them; otherwise a
    best-effort fallback from the flat party_a/party_b strings so a simply
    extracted contract can still reach VALIDATED/SCHEDULE_GENERATED instead
    of stalling at NEEDS_DOCUMENT for lack of >= 2 parties."""
    parties = fields.get("parties")
    out: list[dict[str, Any]] = []
    if isinstance(parties, list):
        for p in parties:
            if not isinstance(p, dict) or not p.get("name"):
                continue
            out.append(
                {
                    "partyRole": str(p.get("party_role") or "party"),
                    "name": str(p["name"]),
                    "taxCode": p.get("tax_code"),
                    "address": p.get("address"),
                    "representative": p.get("representative"),
                }
            )
    if out:
        return out
    for key, role in (("party_a", "party_a"), ("party_b", "party_b")):
        if fields.get(key):
            out.append({"partyRole": role, "name": str(fields[key]), "taxCode": None, "address": None, "representative": None})
    return out


def _normalize_payment_terms(raw: Any) -> list[dict[str, Any]]:
    """Maps the model's payment_terms[] onto ProcessContractRequest's
    PaymentTermDto shape, running every entry through repair_payment_term
    first (contract_normalize.py) so an invalid combination — trigger_event
    + due_date both present, or offset_days/expected_date with no
    trigger_event — is deterministically repaired here rather than relying
    solely on Gateway's own due_date=NULL enforcement (which still applies
    server-side regardless; this is defense in depth, not a replacement).
    Percentages are normalized as a whole list (fraction-vs-percent
    detection needs the full set to check the sum)."""
    if not isinstance(raw, list):
        return []
    dicts = [p for p in raw if isinstance(p, dict)]
    percentages = normalize_payment_percentages([p.get("percentage") for p in dicts])
    out: list[dict[str, Any]] = []
    for idx, (p, percentage) in enumerate(zip(dicts, percentages), start=1):
        repaired = repair_payment_term(p)
        out.append(
            {
                "ordinal": coerce_positive_int(p.get("ordinal")) or idx,
                "description": str(p.get("description") or f"Đợt {idx}"),
                "percentage": percentage,
                "dueDate": repaired["due_date"],
                "triggerEvent": repaired["trigger_event"],
                "offsetDays": repaired["offset_days"],
                "expectedDate": repaired["expected_date"],
            }
        )
    return out


def _normalize_clauses(clauses_in: Any, raw_text: str) -> list[dict[str, Any]]:
    """Real bug fix: clause body/heading must never depend on the LLM
    faithfully reproducing (or even summarizing) the source text — a
    production extraction returned clauses with ordinal only, no heading
    or body, for every single clause, even though raw_text held the full
    original wording. split_clauses_from_raw_text (contract_normalize.py)
    deterministically slices the ACTUAL contract text by its own "Điều N"
    markers; that becomes the authoritative heading/body for every clause
    it finds. The model's own clauses_in is only ever used for two things:
      (a) the semantic classification fields a regex cannot determine
          (has_deadline/has_penalty/clause_type/is_open/trigger_event),
          merged in here by ordinal;
      (b) a fallback source of heading/body for a document that doesn't
          use the "Điều N" convention at all (deterministic split empty) —
          never silently dropping clauses for those contracts.
    """
    llm_by_ordinal: dict[int, dict[str, Any]] = {}
    if isinstance(clauses_in, list):
        for idx, c in enumerate(clauses_in, start=1):
            if not isinstance(c, dict):
                continue
            ordinal = coerce_positive_int(c.get("ordinal")) or idx
            llm_by_ordinal[ordinal] = c

    def classification_for(ordinal: int) -> dict[str, Any]:
        llm_clause = llm_by_ordinal.get(ordinal) or {}
        return {
            "hasDeadline": bool(llm_clause.get("has_deadline", False)),
            "hasPenalty": bool(llm_clause.get("has_penalty", False)),
            "clauseType": llm_clause.get("clause_type"),
            "isOpen": bool(llm_clause.get("is_open", False)),
            "triggerEvent": llm_clause.get("trigger_event"),
        }

    deterministic = split_clauses_from_raw_text(raw_text)
    if deterministic:
        return [
            {
                "ordinal": d["ordinal"],
                "heading": d["heading"],
                "body": d["body"],
                **classification_for(d["ordinal"]),
            }
            for d in deterministic
        ]

    # No "Điều N" structure found in raw_text (e.g. a different contract
    # template) — fall back to whatever the model itself provided, exactly
    # as before, rather than returning zero clauses.
    out: list[dict[str, Any]] = []
    for ordinal, c in sorted(llm_by_ordinal.items()):
        out.append(
            {
                "ordinal": ordinal,
                "heading": c.get("heading"),
                "body": str(c.get("body", "")),
                **classification_for(ordinal),
            }
        )
    return out


def _normalize_milestones(raw: Any) -> list[dict[str, Any]]:
    """Same deterministic repair as _normalize_payment_terms, via
    repair_milestone (duration_months instead of offset_days).

    Bug fix: this used to `continue` (silently drop) any milestone whose
    `name` was missing/blank — MilestoneDto.Name is a required, non-null
    Gateway field, so a model extraction that named some milestones "M2",
    "M3"... in a different field (or omitted a name outright) lost those
    milestones entirely before /process ever saw them, rather than being
    normalized. A missing name is now repaired with a generic, ordinal-
    based fallback ("Mốc {ordinal}") — never a hardcoded specific-contract
    label — so every extracted milestone is preserved and reaches the
    canonical schema the processor expects."""
    if not isinstance(raw, list):
        return []
    out: list[dict[str, Any]] = []
    for idx, m in enumerate(raw, start=1):
        if not isinstance(m, dict):
            continue
        repaired = repair_milestone(m)
        ordinal = coerce_positive_int(m.get("ordinal")) or idx
        name = str(m.get("name") or "").strip() or f"Mốc {ordinal}"
        out.append(
            {
                "ordinal": ordinal,
                "name": name,
                "dueDate": repaired["due_date"],
                "reminderDays": coerce_positive_int(m.get("reminder_days")) or 7,
                "triggerEvent": repaired["trigger_event"],
                "durationMonths": repaired["duration_months"],
                "expectedDate": repaired["expected_date"],
            }
        )
    return out


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
                        "currency, clauses[], tags[]. Nếu văn bản nêu rõ, điền thêm "
                        "document_type, contract_number, subtotal, vat_rate, "
                        "retention_percent, penalty_percent, parties[], "
                        "payment_terms[], milestones[] — các trường này nuôi bước "
                        "sinh lịch thanh toán/mốc theo dõi khi lưu.\n\n"
                        "===== RAW TEXT =====\n"
                        f"{snippet}\n"
                        "===== END ====="
                    ),
                }
            ]
        }

    @tool(
        "save_contract",
        "Lưu hợp đồng đã trích xuất, rồi TỰ ĐỘNG xử lý tiếp (sinh lịch thanh "
        "toán/mốc theo dõi/cảnh báo) — không cần gọi tool nào khác để hợp "
        "đồng thoát khỏi trạng thái 'mới nhận'. fields là JSON object theo "
        "schema đầy đủ của skill contract-extractor: title, party_a, "
        "party_b, signed_date, effective_date, end_date, total_value, "
        "currency, clauses, tags, và (nếu trích xuất được) document_type, "
        "contract_number, subtotal, vat_rate, retention_percent, "
        "penalty_percent, parties[], payment_terms[], milestones[].",
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

        # Strict structural check on the model's raw extraction before any
        # of it reaches /process — never blocks the save (Gateway's own
        # deterministic rules remain the final authority), just surfaces
        # shape problems the accountant should see rather than a silent
        # best-effort repair with no visibility at all.
        validation_issues = validate_extracted_payload(fields)

        # Same "prefer the tool's own raw_text arg, fall back to fields"
        # resolution the saved rawText field below already used — clause
        # splitting needs the identical effective text.
        effective_raw_text = raw_text or str(fields.get("raw_text") or "")

        # Clause body/heading come from raw_text deterministically whenever
        # possible — never trusted from the LLM alone. See _normalize_clauses.
        clauses_out = _normalize_clauses(fields.get("clauses"), effective_raw_text)

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
            "rawText": effective_raw_text or None,
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
        contract_id = contract.get("id")
        save_summary = (
            f"✓ Đã lưu hợp đồng id={contract_id} "
            f"title={contract.get('title')!r} với "
            f"{len(clauses_out)} clauses."
        )

        # Chain straight into processing (classify/extract/validate/
        # schedule/milestones/alerts) so the workflow never silently stalls
        # at RECEIVED after a save — the accountant should see WAITING_REVIEW
        # (or NEEDS_REVIEW/NEEDS_DOCUMENT if the extraction was incomplete),
        # never a contract that looks "saved but untouched".
        process_body = {
            "documentType": fields.get("document_type"),
            "contractNumber": fields.get("contract_number"),
            "subtotal": coerce_number(fields.get("subtotal")),
            "vatRate": normalize_vat_rate(fields.get("vat_rate")),
            "totalValue": coerce_number(fields.get("total_value")),
            "retentionPercent": fields.get("retention_percent"),
            "penaltyPercent": fields.get("penalty_percent"),
            "warrantyMonths": coerce_positive_int(fields.get("warranty_months")),
            "parties": _derive_process_parties(fields),
            "paymentTerms": _normalize_payment_terms(fields.get("payment_terms")),
            "milestones": _normalize_milestones(fields.get("milestones")),
            "referencedDocuments": [
                {"documentType": str(d.get("document_type")), "referencedCode": d.get("referenced_code"), "required": bool(d.get("required", True))}
                for d in (fields.get("referenced_documents") or [])
                if isinstance(d, dict) and d.get("document_type")
            ],
        }
        async with httpx.AsyncClient(timeout=20) as client:
            process_res = await client.post(
                f"{gateway_base}/contracts/{contract_id}/process",
                headers=auth_headers,
                json=process_body,
            )
        validation_note = (
            "\n⚠️ Vấn đề về dữ liệu trích xuất (đã tự sửa nơi có thể):\n"
            + "\n".join(f"- {issue}" for issue in validation_issues)
            if validation_issues
            else ""
        )
        if process_res.status_code != 200:
            return {
                "content": [
                    {
                        "type": "text",
                        "text": (
                            f"{save_summary}\n"
                            f"⚠️ Xử lý tiếp (process) thất bại ({process_res.status_code}): "
                            f"{process_res.text[:200]} — hợp đồng vẫn ở trạng thái đã lưu, "
                            "cần gọi lại process thủ công."
                            f"{validation_note}"
                        ),
                    }
                ]
            }
        processed = process_res.json()
        return {
            "content": [
                {
                    "type": "text",
                    "text": (
                        f"{save_summary}\n"
                        f"✓ Đã xử lý: trạng thái={processed.get('workflowState')}, "
                        f"{processed.get('paymentCount', 0)} đợt thanh toán, "
                        f"{processed.get('milestoneCount', 0)} mốc theo dõi, "
                        f"{processed.get('alertCount', 0)} cảnh báo."
                        f"{validation_note}"
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
