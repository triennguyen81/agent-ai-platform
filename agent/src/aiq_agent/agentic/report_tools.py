"""Real-data query/report MCP tools for /agent/run.

7 read-only tools, all thin wrappers over already-existing, already-tested
Gateway endpoints (Customers/Suppliers/Vouchers/Reports modules) — no new
Gateway endpoint, no aggregation logic that isn't already computed
server-side except summing a caller-bounded voucher list (revenue/expense/
VAT-this-month have no dedicated Gateway report endpoint yet; the tool sums
the real `totalAmount`/`vatAmount` fields of the vouchers the Gateway itself
returns, never a value invented by the model):

- list_customers        -> GET /customers
- get_customer_receivables -> GET /customers/{id}/receivables
- list_suppliers        -> GET /suppliers
- get_supplier_payables -> GET /suppliers/{id}/payables
- get_ap_summary        -> GET /suppliers/ap-summary
- list_vouchers         -> GET /vouchers (type/date/text filters)
- get_weekly_summary_report -> GET /tenant/reports/weekly-summary (tenant_owner only)

Every call carries the caller's own JWT (or the manifest's short-lived
tool_execution token) — the same tenant/department scoping every other
Gateway endpoint already enforces applies here unchanged. These tools never
write anything.
"""

from __future__ import annotations

import logging
from typing import Any

import httpx
from claude_agent_sdk import tool

logger = logging.getLogger(__name__)


def _format_vnd(amount: float) -> str:
    return f"{amount:,.0f}".replace(",", ".") + " ₫"


def build_report_tools(*, token: str, gateway_base: str) -> list[Any]:
    """Per-request factory bound to caller's JWT."""

    auth_headers = {"Authorization": f"Bearer {token}"}
    base = gateway_base.rstrip("/")

    async def _get(path: str, params: dict[str, Any] | None = None) -> tuple[int, Any]:
        async with httpx.AsyncClient(timeout=15) as client:
            res = await client.get(f"{base}{path}", headers=auth_headers, params=params)
        try:
            body = res.json()
        except ValueError:
            body = {"raw": res.text}
        return res.status_code, body

    @tool(
        "list_customers",
        "Tìm khách hàng theo tên/MST/mã KH (q) — trả về id, mã KH, tên, MST, "
        "công nợ hiện tại (currentAr), hạn mức tín dụng. Dùng để tìm customer_id "
        "trước khi gọi get_customer_receivables.",
        {"q": str, "includeInactive": bool},
    )
    async def list_customers(args: dict[str, Any]) -> dict[str, Any]:
        q = str(args.get("q", "")).strip() or None
        include_inactive = bool(args.get("includeInactive", False))
        status, body = await _get(
            "/customers", {"q": q, "includeInactive": str(include_inactive).lower()}
        )
        if status != 200:
            return {"content": [{"type": "text", "text": f"HTTP {status}: {body}"}]}
        items = body.get("items", [])
        if not items:
            return {
                "content": [
                    {"type": "text", "text": f"Không tìm thấy khách hàng nào khớp '{q or ''}'."}
                ]
            }
        lines = [f"**{len(items)} khách hàng khớp '{q or ''}':**", ""]
        for c in items:
            lines.append(
                f"- [{c['customerCode']}] **{c['name']}** "
                f"(id={c['id']}, MST={c.get('mst') or '—'}) "
                f"— công nợ hiện tại: {_format_vnd(c['currentAr'])}"
            )
        return {
            "content": [
                {"type": "text", "text": "\n".join(lines)},
                {"type": "text", "text": str({"items": items, "total": body.get("total")})},
            ]
        }

    @tool(
        "get_customer_receivables",
        "Lấy chi tiết công nợ phải thu (AR) của 1 khách hàng theo customer_id: "
        "hoá đơn mở, tuổi nợ, quá hạn, hạn mức tín dụng. customer_id lấy từ list_customers.",
        {"customer_id": str},
    )
    async def get_customer_receivables(args: dict[str, Any]) -> dict[str, Any]:
        customer_id = str(args.get("customer_id", "")).strip()
        if not customer_id:
            return {"content": [{"type": "text", "text": "customer_id là bắt buộc"}]}
        status, body = await _get(f"/customers/{customer_id}/receivables")
        if status != 200:
            return {"content": [{"type": "text", "text": f"HTTP {status}: {body}"}]}
        cust = body["customer"]
        open_invoices = body.get("openInvoices", [])
        lines = [
            f"**Công nợ phải thu — {cust['name']}** (MST {cust.get('mst') or '—'})",
            "",
            f"- Công nợ hiện tại (131): **{_format_vnd(body['currentBalance131'])}**",
            f"- Hạn mức tín dụng: {_format_vnd(body['creditLimit'])} "
            f"(đã dùng {body['creditUsedPercent']}%)",
            f"- Quá hạn: **{_format_vnd(body['overdueAmount'])}**",
        ]
        if open_invoices:
            lines.append("")
            lines.append("**Hoá đơn mở:**")
            for inv in open_invoices:
                remaining = inv["debit"] - inv.get("paidAmount", 0)
                overdue_tag = (
                    f" — QUÁ HẠN {inv['daysOverdue']} ngày" if inv.get("daysOverdue", 0) > 0 else ""
                )
                lines.append(
                    f"- {inv['documentNumber']} ngày {inv['documentDate']}, "
                    f"hạn {inv.get('dueDate') or '—'}: "
                    f"còn {_format_vnd(remaining)} ({inv.get('status', 'open')}){overdue_tag}"
                )
        else:
            lines.append("")
            lines.append("Không có hoá đơn mở nào.")
        return {
            "content": [
                {"type": "text", "text": "\n".join(lines)},
                {"type": "text", "text": str(body)},
            ]
        }

    @tool(
        "list_suppliers",
        "Tìm nhà cung cấp theo tên/MST/mã NCC (q) — trả về id, mã NCC, tên, MST, "
        "công nợ phải trả hiện tại (currentAp). Dùng để tìm supplier_id trước "
        "khi gọi get_supplier_payables.",
        {"q": str, "includeInactive": bool},
    )
    async def list_suppliers(args: dict[str, Any]) -> dict[str, Any]:
        q = str(args.get("q", "")).strip() or None
        include_inactive = bool(args.get("includeInactive", False))
        status, body = await _get(
            "/suppliers", {"q": q, "includeInactive": str(include_inactive).lower()}
        )
        if status != 200:
            return {"content": [{"type": "text", "text": f"HTTP {status}: {body}"}]}
        items = body.get("items", [])
        if not items:
            if not q:
                return {
                    "content": [{"type": "text", "text": "Không có nhà cung cấp nào có công nợ."}]
                }
            return {
                "content": [
                    {"type": "text", "text": f"Không tìm thấy nhà cung cấp nào khớp '{q}'."}
                ]
            }
        lines = [f"**{len(items)} nhà cung cấp" + (f" khớp '{q}'" if q else "") + ":**", ""]
        for s in items:
            lines.append(
                f"- [{s['supplierCode']}] **{s['name']}** "
                f"(id={s['id']}, MST={s.get('mst') or '—'}) "
                f"— công nợ phải trả: {_format_vnd(s['currentAp'])}"
            )
        return {
            "content": [
                {"type": "text", "text": "\n".join(lines)},
                {"type": "text", "text": str({"items": items, "total": body.get("total")})},
            ]
        }

    @tool(
        "get_supplier_payables",
        "Lấy chi tiết công nợ phải trả (AP) của 1 nhà cung cấp theo supplier_id: "
        "hoá đơn mở, tuổi nợ, quá hạn. supplier_id lấy từ list_suppliers.",
        {"supplier_id": str},
    )
    async def get_supplier_payables(args: dict[str, Any]) -> dict[str, Any]:
        supplier_id = str(args.get("supplier_id", "")).strip()
        if not supplier_id:
            return {"content": [{"type": "text", "text": "supplier_id là bắt buộc"}]}
        status, body = await _get(f"/suppliers/{supplier_id}/payables")
        if status != 200:
            return {"content": [{"type": "text", "text": f"HTTP {status}: {body}"}]}
        sup = body["supplier"]
        open_invoices = body.get("openInvoices", [])
        lines = [
            f"**Công nợ phải trả — {sup['name']}** (MST {sup.get('mst') or '—'})",
            "",
            f"- Công nợ hiện tại (331): **{_format_vnd(body['currentBalance331'])}**",
            f"- Quá hạn: **{_format_vnd(body['overdueAmount'])}**",
        ]
        if open_invoices:
            lines.append("")
            lines.append("**Hoá đơn mở:**")
            for inv in open_invoices:
                remaining = inv["credit"] - inv.get("paidAmount", 0)
                overdue_tag = (
                    f" — QUÁ HẠN {inv['daysOverdue']} ngày" if inv.get("daysOverdue", 0) > 0 else ""
                )
                lines.append(
                    f"- {inv['documentNumber']} ngày {inv['documentDate']}, "
                    f"hạn {inv.get('dueDate') or '—'}: "
                    f"còn {_format_vnd(remaining)} ({inv.get('status', 'open')}){overdue_tag}"
                )
        else:
            lines.append("")
            lines.append("Không có hoá đơn mở nào.")
        return {
            "content": [
                {"type": "text", "text": "\n".join(lines)},
                {"type": "text", "text": str(body)},
            ]
        }

    @tool(
        "get_ap_summary",
        "Tổng hợp công nợ phải trả (AP) toàn phòng ban: tổng phải trả, tuổi nợ, "
        "quá hạn, sắp đến hạn 7/30 ngày. Không cần tham số.",
        {},
    )
    async def get_ap_summary(_args: dict[str, Any]) -> dict[str, Any]:
        status, body = await _get("/suppliers/ap-summary")
        if status != 200:
            return {"content": [{"type": "text", "text": f"HTTP {status}: {body}"}]}
        lines = [
            "**Tổng hợp công nợ phải trả (AP):**",
            "",
            f"- Số nhà cung cấp có công nợ: {body['supplierCount']}",
            f"- Tổng phải trả: **{_format_vnd(body['totalPayable'])}**",
            f"- Quá hạn: **{_format_vnd(body['overdueAmount'])}**",
            f"- Sắp đến hạn trong 7 ngày: {_format_vnd(body['dueWithin7Days'])}",
            f"- Sắp đến hạn trong 30 ngày: {_format_vnd(body['dueWithin30Days'])}",
        ]
        return {
            "content": [
                {"type": "text", "text": "\n".join(lines)},
                {"type": "text", "text": str(body)},
            ]
        }

    @tool(
        "list_vouchers",
        "Tìm chứng từ (hoá đơn/phiếu thu/phiếu chi) theo loại và khoảng ngày. "
        "voucherType ∈ {invoice_vat_out (hoá đơn bán), invoice_vat_in (hoá đơn "
        "mua), receipt (phiếu thu), payment (phiếu chi), bank_transfer, "
        "expense_claim}. documentDateFrom/documentDateTo dạng yyyy-MM-dd. "
        "q tìm theo số hoá đơn/tên-MST người bán. Dùng để tính doanh thu/chi "
        "phí/VAT tháng (cộng dồn totalAmount/vatAmount của kết quả trả về) "
        "hoặc để tìm 1 hoá đơn cụ thể theo số trước khi đề xuất định khoản.",
        {
            "voucherType": str,
            "documentDateFrom": str,
            "documentDateTo": str,
            "q": str,
            "limit": int,
        },
    )
    async def list_vouchers(args: dict[str, Any]) -> dict[str, Any]:
        params = {
            "voucherType": str(args.get("voucherType", "")).strip() or None,
            "documentDateFrom": str(args.get("documentDateFrom", "")).strip() or None,
            "documentDateTo": str(args.get("documentDateTo", "")).strip() or None,
            "q": str(args.get("q", "")).strip() or None,
            "limit": int(args.get("limit", 0)) or 200,
        }
        status, body = await _get("/vouchers", params)
        if status != 200:
            return {"content": [{"type": "text", "text": f"HTTP {status}: {body}"}]}
        items = body.get("items", [])
        total_amount = sum((v.get("totalAmount") or 0) for v in items)
        total_vat = sum((v.get("vatAmount") or 0) for v in items)
        lines = [
            f"**{len(items)} chứng từ khớp bộ lọc** (tenant/department hiện tại, tối đa "
            f"{params['limit']} dòng — nếu total ở dưới lớn hơn số dòng trả về thì cần thu hẹp "
            f"filter):",
            "",
            f"- Tổng totalAmount cộng dồn: **{_format_vnd(total_amount)}**",
            f"- Tổng vatAmount cộng dồn: **{_format_vnd(total_vat)}**",
            "",
        ]
        for v in items[:30]:
            lines.append(
                f"- [{v['voucherType']}] {v.get('documentNumber') or '(chưa có số)'} ngày "
                f"{v.get('documentDate') or '—'}: {_format_vnd(v.get('totalAmount') or 0)} (VAT "
                f"{_format_vnd(v.get('vatAmount') or 0)}) — status={v['status']} — id={v['id']}"
            )
        return {
            "content": [
                {"type": "text", "text": "\n".join(lines)},
                {
                    "type": "text",
                    "text": str(
                        {
                            "items_returned": len(items),
                            "total_reported_by_gateway": body.get("total"),
                            "sum_total_amount": total_amount,
                            "sum_vat_amount": total_vat,
                            "items": items,
                        }
                    ),
                },
            ]
        }

    @tool(
        "get_weekly_summary_report",
        "Báo cáo tuần cho ban giám đốc (chỉ TENANT_OWNER xem được): dòng tiền "
        "thu trong tuần, tổng công nợ phải thu mở, chi phí theo bộ phận. "
        "Không cần tham số.",
        {},
    )
    async def get_weekly_summary_report(_args: dict[str, Any]) -> dict[str, Any]:
        status, body = await _get("/tenant/reports/weekly-summary")
        if status == 403:
            return {
                "content": [
                    {
                        "type": "text",
                        "text": "Chỉ TENANT_OWNER mới xem được báo cáo tuần này (HTTP 403) — "
                        "tài khoản hiện tại không có quyền.",
                    }
                ]
            }
        if status != 200:
            return {"content": [{"type": "text", "text": f"HTTP {status}: {body}"}]}
        return {
            "content": [
                {"type": "text", "text": f"**Báo cáo tuần (ban giám đốc):**\n\n{body}"},
                {"type": "text", "text": str(body)},
            ]
        }

    return [
        list_customers,
        get_customer_receivables,
        list_suppliers,
        get_supplier_payables,
        get_ap_summary,
        list_vouchers,
        get_weekly_summary_report,
    ]
