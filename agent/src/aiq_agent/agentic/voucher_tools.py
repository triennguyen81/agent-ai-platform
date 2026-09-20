"""Voucher domain MCP tools for /agent/run.

Seven tools:
- extract_voucher_fields(raw_text, voucher_type) — LLM-driven extract, pattern
  identical to contract_tools.extract_contract_fields (echo raw + prompt the
  model to apply the right skill).
- save_voucher(fields, raw_text?, source_path?) — POST to Gateway.
- verify_einvoice_tct(invoice_no, seller_mst, ...) — call hoadondientu.gdt.gov.vn
  lookup. Falls back to a local heuristic check if TCT unreachable.
- confirm_tax_verified(voucher_id) — persist a genuine positive TCT result
  onto the voucher row (vouchers.tct_verified). Only ever a one-way flip to
  true; never called for an ambiguous/negative/unavailable result.
- reject_tax_verified(voucher_id, reason, provider_unavailable) — persist a
  genuine NEGATIVE or unavailable TCT result. This is what lets the Gateway's
  Luồng 03 approval gate (IExpenseClaimRepository.ConfirmAsync) tell "never
  checked" apart from "checked and blocked" — never silently left unverified.
- suggest_journal_entry(voucher_id) — read the voucher and ask the LLM to
  output N/C entries per TT200 accounting rules.
- save_journal_suggestions(voucher_id, entries) — persist proposed entries;
  for receipt/expense_claim vouchers the Gateway validates every (debit,
  credit) pair against the business process document's closed allow-list
  before saving (see JournalRoutingRules.cs).

The extraction and journal-suggest tools intentionally return structured-
text wrappers rather than doing a second LLM round-trip themselves: the
voucher-universal-extractor (or, for the legacy invoice_vat type only,
voucher-invoice-vat) and accounting-vietnam / journal-entry-formatter
skills teach the model to produce the right JSON.
"""

from __future__ import annotations

import logging
from typing import Any

import httpx
from claude_agent_sdk import tool

logger = logging.getLogger(__name__)

_TCT_LOOKUP_URL = "https://hoadondientu.gdt.gov.vn/restapi/invoices/lookup"

_VOUCHER_TYPES = {
    "invoice_vat",
    "invoice_vat_out",
    "invoice_vat_in",
    "receipt",
    "payment",
    "bank_transfer",
    "bank_statement",
    "expense_claim",
    "contract",
}

# R17/R20 fix — voucher-receipt/voucher-bank-transfer/voucher-bank-statement
# never existed as skill files (grep of agent/skills/ confirms zero matches);
# every real type except the legacy bare "invoice_vat" is documented by the
# single comprehensive voucher-universal-extractor skill (see its own
# numbered sections for invoice_vat_out/in, receipt, payment, bank_transfer,
# bank_statement, expense_claim, contract). Route everything there instead
# of at three skills that don't exist.
_SKILL_BY_TYPE = {
    "invoice_vat": "voucher-invoice-vat",
}
_DEFAULT_SKILL = "voucher-universal-extractor"


def build_voucher_tools(*, token: str, gateway_base: str) -> list[Any]:
    """Per-request factory bound to caller's JWT."""

    auth_headers = {"Authorization": f"Bearer {token}"}

    @tool(
        "extract_voucher_fields",
        "Trích xuất trường từ raw OCR text của chứng từ. voucher_type là 1 trong: "
        f"{', '.join(sorted(_VOUCHER_TYPES))}. Trả về JSON theo đúng schema của "
        "voucher-universal-extractor cho loại đó (voucher-invoice-vat chỉ cho "
        "loại legacy 'invoice_vat').",
        {"raw_text": str, "voucher_type": str},
    )
    async def extract_voucher_fields(args: dict[str, Any]) -> dict[str, Any]:
        raw = str(args.get("raw_text", "")).strip()
        vtype = str(args.get("voucher_type", "invoice_vat")).strip().lower()

        if not raw:
            return {"content": [{"type": "text", "text": "raw_text trống — không có gì để extract"}]}
        if vtype not in _VOUCHER_TYPES:
            return {
                "content": [
                    {
                        "type": "text",
                        "text": f"voucher_type '{vtype}' không hợp lệ. Chọn 1 trong: {', '.join(sorted(_VOUCHER_TYPES))}",
                    }
                ]
            }

        skill_name = _SKILL_BY_TYPE.get(vtype, _DEFAULT_SKILL)
        snippet = raw[:50_000]
        # R17/R20 fix — the field list below used to be hardcoded to an
        # invoice's own fields (seller/buyer/line_items) regardless of
        # vtype, which is wrong for receipt/expense_claim/payment/
        # bank_transfer/bank_statement/contract (they don't have a
        # seller/buyer pair and DO have type-specific fields the skill
        # documents, e.g. expense_claim's employee_code/items[]). The skill
        # itself (voucher-universal-extractor, section per voucher_type)
        # is now the sole source of the field schema — this instruction
        # only states the one field every type shares (voucher_type) and
        # defers everything else to the skill.
        return {
            "content": [
                {
                    "type": "text",
                    "text": (
                        f"Raw OCR text chứng từ loại '{vtype}' ({len(raw)} ký tự). "
                        f"Hãy áp dụng skill {skill_name}, đúng phần mô tả cho voucher_type='{vtype}', "
                        "và trả về JSON đầy đủ đúng theo schema của loại chứng từ đó (bắt buộc có "
                        "voucher_type). KHÔNG dùng schema hoá đơn (seller/buyer/line_items) cho một "
                        "loại chứng từ không phải hoá đơn.\n\n"
                        "===== RAW OCR TEXT =====\n"
                        f"{snippet}\n"
                        "===== END ====="
                    ),
                }
            ]
        }

    @tool(
        "save_voucher",
        "Lưu chứng từ đã trích xuất vào DB. fields là JSON object theo schema "
        "voucher-invoice-vat (voucher_type, document_number, document_date, "
        "seller_*, buyer_*, subtotal, vat_*, total_amount, line_items[], ...). "
        "raw_text và source_path optional để giữ audit trail. "
        "voucher_id: nếu có, UPDATE row đó (dùng khi voucher stub đã tồn tại "
        "từ upload); nếu KHÔNG có, tạo row mới.",
        {"fields": dict, "raw_text": str, "source_path": str, "voucher_id": str},
    )
    async def save_voucher(args: dict[str, Any]) -> dict[str, Any]:
        fields = args.get("fields") or {}
        raw_text = str(args.get("raw_text", ""))
        source_path = str(args.get("source_path", "")) or None
        voucher_id = str(args.get("voucher_id", "")).strip() or None

        if not isinstance(fields, dict):
            return {"content": [{"type": "text", "text": "fields phải là dict"}]}

        vtype = str(fields.get("voucher_type") or "invoice_vat").strip().lower()
        if vtype not in _VOUCHER_TYPES:
            return {
                "content": [{"type": "text", "text": f"voucher_type '{vtype}' không hợp lệ"}]
            }

        line_items_in = fields.get("line_items") or []
        line_items_out: list[dict[str, Any]] = []
        if isinstance(line_items_in, list):
            for idx, li in enumerate(line_items_in, start=1):
                if not isinstance(li, dict):
                    continue
                line_items_out.append(
                    {
                        "ordinal": int(li.get("ordinal", idx)),
                        "itemCode": li.get("item_code"),
                        "description": str(li.get("description", ""))[:500],
                        "unit": li.get("unit"),
                        "quantity": _to_number(li.get("quantity")),
                        "unitPrice": _to_number(li.get("unit_price")),
                        "subtotal": _to_number(li.get("subtotal")),
                        "vatRate": _to_number(li.get("vat_rate")),
                        "vatAmount": _to_number(li.get("vat_amount")),
                        "totalAmount": _to_number(li.get("total_amount")),
                    }
                )

        body = {
            "voucherType": vtype,
            "documentNumber": fields.get("document_number"),
            "documentSeries": fields.get("document_series"),
            "documentDate": fields.get("document_date"),
            "sellerName": fields.get("seller_name"),
            "sellerMst": fields.get("seller_mst"),
            "sellerAddress": fields.get("seller_address"),
            "buyerName": fields.get("buyer_name"),
            "buyerMst": fields.get("buyer_mst"),
            "buyerAddress": fields.get("buyer_address"),
            "paymentMethod": fields.get("payment_method"),
            "subtotal": _to_number(fields.get("subtotal")),
            "vatRate": _to_number(fields.get("vat_rate")),
            "vatAmount": _to_number(fields.get("vat_amount")),
            "discountAmount": _to_number(fields.get("discount_amount")),
            "totalAmount": _to_number(fields.get("total_amount")),
            "currency": fields.get("currency") or "VND",
            "amountInWords": fields.get("amount_in_words"),
            "sourceFilePath": source_path or fields.get("source_file_path"),
            "sourceFileName": fields.get("source_file_name"),
            "sourceMimeType": fields.get("source_mime_type"),
            "rawOcrText": raw_text or fields.get("raw_ocr_text"),
            "ocrConfidence": _to_number(fields.get("ocr_confidence")),
            "extractedFields": fields,
            "tags": [str(t) for t in (fields.get("tags") or []) if t],
            "lineItems": line_items_out,
        }

        # Route to update-existing endpoint if caller passed an id.
        # Prevents duplicate rows when the web upload flow already staged
        # an empty voucher stub and we're just filling in extracted fields.
        if voucher_id:
            url = f"{gateway_base}/vouchers/{voucher_id}/apply-extraction"
            verb = "PATCH"
        else:
            url = f"{gateway_base}/vouchers"
            verb = "POST"

        async with httpx.AsyncClient(timeout=20) as client:
            res = await client.request(verb, url, headers=auth_headers, json=body)

        if res.status_code not in (200, 201, 204):
            return {
                "content": [
                    {
                        "type": "text",
                        "text": f"Lưu chứng từ thất bại ({res.status_code}): {res.text[:200]}",
                    }
                ]
            }

        # PATCH may return empty body (204) — fall back to args.
        try:
            saved = res.json() if res.text else {}
        except Exception:
            saved = {}
        voucher = saved.get("voucher") or saved
        vid = voucher.get("id") or voucher_id
        return {
            "content": [
                {
                    "type": "text",
                    "text": (
                        f"✓ Đã {'cập nhật' if voucher_id else 'tạo'} chứng từ "
                        f"id={vid} "
                        f"số={fields.get('document_number')!r} "
                        f"tổng tiền={fields.get('total_amount')} "
                        f"với {len(line_items_out)} dòng."
                    ),
                }
            ]
        }

    @tool(
        "verify_einvoice_tct",
        "Xác thực hoá đơn VAT điện tử qua Tổng cục Thuế (hoadondientu.gdt.gov.vn). "
        "Best-effort — nếu TCT không khả dụng thì trả kết quả 'unavailable' + "
        "vẫn chạy check MST format cục bộ. Input: invoice_number, seller_mst, "
        "invoice_date (YYYY-MM-DD), total_amount (optional cho khớp).",
        {
            "invoice_number": str,
            "seller_mst": str,
            "invoice_date": str,
            "total_amount": float,
        },
    )
    async def verify_einvoice_tct(args: dict[str, Any]) -> dict[str, Any]:
        invoice_no = str(args.get("invoice_number", "")).strip()
        mst = str(args.get("seller_mst", "")).strip()
        inv_date = str(args.get("invoice_date", "")).strip()
        total = args.get("total_amount")

        checks: list[str] = []

        # Local: MST format
        if not mst:
            checks.append("❌ Thiếu MST bên bán")
        elif not (mst.isdigit() and len(mst) in (10, 13)):
            checks.append(f"⚠ MST '{mst}' không đúng format (cần 10 hoặc 13 chữ số)")
        else:
            checks.append(f"✓ MST '{mst}' đúng format")

        if not invoice_no:
            checks.append("❌ Thiếu số hoá đơn")
        if not inv_date:
            checks.append("⚠ Thiếu ngày hoá đơn")

        # TCT lookup — best effort, don't fail the whole tool
        tct_status = "not_attempted"
        tct_body: str | None = None
        if invoice_no and mst:
            try:
                async with httpx.AsyncClient(timeout=8) as client:
                    res = await client.post(
                        _TCT_LOOKUP_URL,
                        json={
                            "invoiceNumber": invoice_no,
                            "sellerTaxCode": mst,
                            "invoiceDate": inv_date,
                            "totalAmount": total,
                        },
                    )
                tct_status = f"http_{res.status_code}"
                tct_body = res.text[:500]
                if res.status_code == 200:
                    checks.append("✓ TCT trả 200 — cần review body để xác nhận exists")
                elif res.status_code in (400, 404):
                    checks.append(f"⚠ TCT trả {res.status_code} — hoá đơn có thể không tồn tại")
                else:
                    checks.append(f"⚠ TCT trả {res.status_code} — không kết luận được")
            except httpx.HTTPError as exc:
                tct_status = f"network_error: {type(exc).__name__}"
                checks.append(f"⚠ TCT không khả dụng ({type(exc).__name__}) — chỉ verify cục bộ")

        summary_lines = ["**Kết quả xác thực hoá đơn:**", ""]
        summary_lines.extend(checks)
        summary_lines.append("")
        summary_lines.append(f"TCT lookup status: `{tct_status}`")
        if tct_body:
            summary_lines.append(f"TCT response snippet: `{tct_body[:200]}`")

        return {"content": [{"type": "text", "text": "\n".join(summary_lines)}]}

    @tool(
        "confirm_tax_verified",
        "Đánh dấu 1 voucher đã có trong DB là ĐÃ xác thực hợp lệ qua TCT "
        "(hoadondientu.gdt.gov.vn). CHỈ gọi tool này sau khi verify_einvoice_tct "
        "trả HTTP 200 VÀ bạn đã tự đọc response body xác nhận hoá đơn thực sự "
        "tồn tại/còn hiệu lực — một HTTP 200 một mình KHÔNG đủ. Không có cách "
        "revert — chỉ set true, không bao giờ set false (chưa xác thực = false, "
        "không phải giá trị hợp lệ để agent tự gán).",
        {"voucher_id": str},
    )
    async def confirm_tax_verified(args: dict[str, Any]) -> dict[str, Any]:
        vid = str(args.get("voucher_id", "")).strip()
        if not vid:
            return {"content": [{"type": "text", "text": "voucher_id required"}]}
        async with httpx.AsyncClient(timeout=10) as client:
            res = await client.post(
                f"{gateway_base}/vouchers/{vid}/tax-verification/confirm",
                headers=auth_headers,
            )
        if res.status_code == 404:
            return {"content": [{"type": "text", "text": "Voucher not found"}]}
        if res.status_code != 200:
            return {"content": [{"type": "text", "text": f"Gateway returned {res.status_code}"}]}
        return {"content": [{"type": "text", "text": f"Voucher {vid} marked tax-verified."}]}

    @tool(
        "reject_tax_verified",
        "Ghi nhận 1 kết quả xác thực TIÊU CỰC hoặc KHÔNG THỂ xác thực cho voucher "
        "(P1 audit fix — không được để 'chưa xác thực' im lặng, phải ghi rõ lý do). "
        "Gọi với provider_unavailable=true nếu verify_einvoice_tct không kết nối được "
        "cổng thuế (network_error) — không bao giờ coi im lặng/lỗi mạng là đã xác "
        "thực thành công. Gọi với provider_unavailable=false nếu cổng thuế trả lời "
        "rõ ràng hoá đơn không tồn tại/đã huỷ/đã thay thế.",
        {"voucher_id": str, "reason": str, "provider_unavailable": bool},
    )
    async def reject_tax_verified(args: dict[str, Any]) -> dict[str, Any]:
        vid = str(args.get("voucher_id", "")).strip()
        reason = str(args.get("reason", "")).strip()
        provider_unavailable = bool(args.get("provider_unavailable", False))
        if not vid or not reason:
            return {"content": [{"type": "text", "text": "voucher_id and reason required"}]}
        async with httpx.AsyncClient(timeout=10) as client:
            res = await client.post(
                f"{gateway_base}/vouchers/{vid}/tax-verification/reject",
                headers=auth_headers,
                json={"reason": reason, "providerUnavailable": provider_unavailable},
            )
        if res.status_code == 404:
            return {"content": [{"type": "text", "text": "Voucher not found"}]}
        if res.status_code != 200:
            return {"content": [{"type": "text", "text": f"Gateway returned {res.status_code}"}]}
        return {"content": [{"type": "text", "text": f"Voucher {vid} tax-verification recorded as rejected/unavailable."}]}

    @tool(
        "suggest_journal_entry",
        "Đề xuất bút toán N/C theo TT200 cho 1 voucher đã có trong DB. "
        "Trả về hướng dẫn cho model dùng skill journal-entry-formatter + "
        "accounting-vietnam để sinh JSON journal entries.",
        {"voucher_id": str},
    )
    async def suggest_journal_entry(args: dict[str, Any]) -> dict[str, Any]:
        vid = str(args.get("voucher_id", "")).strip()
        if not vid:
            return {"content": [{"type": "text", "text": "voucher_id required"}]}

        async with httpx.AsyncClient(timeout=10) as client:
            res = await client.get(
                f"{gateway_base}/vouchers/{vid}",
                headers=auth_headers,
            )
        if res.status_code == 404:
            return {"content": [{"type": "text", "text": "Voucher not found"}]}
        if res.status_code != 200:
            return {
                "content": [
                    {"type": "text", "text": f"Gateway returned {res.status_code}"}
                ]
            }

        voucher_json = res.text
        return {
            "content": [
                {
                    "type": "text",
                    "text": (
                        "Dựa vào voucher dưới đây, áp dụng skill "
                        "journal-entry-formatter + accounting-vietnam để đề xuất "
                        "bút toán N/C theo TT200. Trả về JSON array với schema:\n"
                        '[{"entry_no": 1, "debit_account": "156", '
                        '"credit_account": "331", "amount": 10000000, '
                        '"description": "Mua hàng ...", "confidence": 0.9}, ...]\n\n'
                        "Quy tắc chung:\n"
                        "- Hoá đơn VAT đầu vào (mua hàng): Nợ 156/152/153/641/642/6427 + "
                        "Nợ 1331 / Có 331 (nếu công nợ) hoặc 111/112 (nếu trả ngay).\n"
                        "- Hoá đơn VAT đầu ra (bán hàng): Nợ 131/111/112 / Có 511 + Có 33311 "
                        "(TK con của 3331 — dùng đúng TK chi tiết, không dùng TK cha 3331).\n"
                        "- Phiếu thu: Nợ 111 / Có 131/511/711.\n"
                        "- Phiếu chi: Nợ 331/641/642 / Có 111.\n"
                        "- Uỷ nhiệm chi: Nợ 331/641 / Có 112.\n\n"
                        f"===== VOUCHER JSON =====\n{voucher_json}\n===== END ====="
                    ),
                }
            ]
        }

    @tool(
        "save_journal_suggestions",
        "Persist the journal N/C entries proposed by suggest_journal_entry "
        "into voucher_journal_suggestions so the web detail page shows "
        "them and the user can accept/reject. entries is a JSON array of "
        "objects: {entry_no, debit_account, credit_account, amount, "
        "description, confidence}. Set replace_existing=true (default) to "
        "wipe prior AI proposals before writing this batch.",
        {"voucher_id": str, "entries": list, "replace_existing": bool},
    )
    async def save_journal_suggestions(args: dict[str, Any]) -> dict[str, Any]:
        vid = str(args.get("voucher_id", "")).strip()
        entries_in = args.get("entries") or []
        replace = bool(args.get("replace_existing", True))

        if not vid:
            return {"content": [{"type": "text", "text": "voucher_id required"}]}
        if not isinstance(entries_in, list) or not entries_in:
            return {"content": [{"type": "text", "text": "entries must be a non-empty list"}]}

        # Normalize + drop malformed rows so the LLM's shape drift
        # doesn't fail the whole batch. Skip any entry missing the two
        # sides of the T-account or a positive amount.
        normalized: list[dict[str, Any]] = []
        for idx, raw in enumerate(entries_in, start=1):
            if not isinstance(raw, dict):
                continue
            debit = str(raw.get("debit_account") or "").strip()
            credit = str(raw.get("credit_account") or "").strip()
            amount = _to_number(raw.get("amount"))
            if not debit or not credit or not amount or amount <= 0:
                continue
            normalized.append(
                {
                    "entryNo": int(raw.get("entry_no", idx)),
                    "debitAccount": debit,
                    "creditAccount": credit,
                    "amount": amount,
                    "description": raw.get("description"),
                    "confidence": _to_number(raw.get("confidence")),
                }
            )

        if not normalized:
            return {"content": [{"type": "text", "text": "no valid entries after normalization"}]}

        body = {"entries": normalized, "replaceExisting": replace}
        async with httpx.AsyncClient(timeout=15) as client:
            res = await client.post(
                f"{gateway_base}/vouchers/{vid}/journal-suggestions",
                headers=auth_headers,
                json=body,
            )
        if res.status_code not in (200, 201):
            return {
                "content": [
                    {
                        "type": "text",
                        "text": f"Save journal failed ({res.status_code}): {res.text[:200]}",
                    }
                ]
            }

        return {
            "content": [
                {
                    "type": "text",
                    "text": (
                        f"✓ Đã lưu {len(normalized)} bút toán suggest cho voucher {vid}."
                    ),
                }
            ]
        }

    return [
        extract_voucher_fields,
        save_voucher,
        verify_einvoice_tct,
        confirm_tax_verified,
        reject_tax_verified,
        suggest_journal_entry,
        save_journal_suggestions,
    ]


def _to_number(value: Any) -> float | int | None:
    """Coerce LLM output to a number or None. LLM sometimes returns strings."""

    if value is None or value == "":
        return None
    if isinstance(value, (int, float)):
        return value
    if isinstance(value, str):
        cleaned = value.replace(",", "").replace(".", "").strip()
        if cleaned.isdigit():
            return int(cleaned)
        try:
            return float(value.replace(",", "."))
        except ValueError:
            return None
    return None
