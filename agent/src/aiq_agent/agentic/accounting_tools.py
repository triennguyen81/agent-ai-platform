"""Vietnamese accounting domain tools for /agent/run.

7 MCP tools that the model can invoke:
- calculate_vat
- calculate_pit
- calculate_cit
- lookup_account
- check_filing_deadline
- journal_entry_validator
- import_excel_ledger

Data tables (TT200 chart of accounts, filing deadlines) are embedded
inline so the tools work offline without round-tripping to the KB.
"""

from __future__ import annotations

import io
import logging
from typing import Any

import httpx
from claude_agent_sdk import tool

logger = logging.getLogger(__name__)

# --------------------------------------------------------------------------
# Data tables — TT200 chart of accounts (most common 60 codes)
# --------------------------------------------------------------------------

# Each entry: code -> (name, parent_or_none, notes)
TT200_ACCOUNTS: dict[str, tuple[str, str | None, str]] = {
    # Loại 1 — Tài sản ngắn hạn
    "111": ("Tiền mặt", None, "1111 VND · 1112 ngoại tệ · 1113 vàng bạc đá quý"),
    "1111": ("Tiền mặt Việt Nam đồng", "111", ""),
    "1112": ("Tiền mặt ngoại tệ", "111", ""),
    "112": ("Tiền gửi ngân hàng", None, "1121 VND · 1122 ngoại tệ — chi tiết từng NH"),
    "1121": ("Tiền gửi NH bằng VND", "112", ""),
    "1122": ("Tiền gửi NH bằng ngoại tệ", "112", ""),
    "113": ("Tiền đang chuyển", None, ""),
    "121": ("Đầu tư chứng khoán ngắn hạn", None, ""),
    "128": ("Đầu tư khác ngắn hạn", None, "Tiền gửi có kỳ hạn ngắn"),
    "131": ("Phải thu khách hàng", None, "Chi tiết theo từng KH; có thể có dư Nợ và Có"),
    "133": ("Thuế GTGT được khấu trừ", None, "1331 hàng hoá-dịch vụ · 1332 TSCĐ"),
    "1331": ("VAT đầu vào được khấu trừ — hàng hoá dịch vụ", "133", ""),
    "1332": ("VAT đầu vào được khấu trừ — TSCĐ", "133", ""),
    "138": ("Phải thu khác", None, "1381 thiếu chờ xử lý · 1388 khác"),
    "1381": ("Tài sản thiếu chờ xử lý", "138", ""),
    "1388": ("Phải thu khác", "138", ""),
    "141": ("Tạm ứng", None, "Chi tiết từng người tạm ứng"),
    "142": ("Chi phí trả trước ngắn hạn", None, ""),
    "152": ("Nguyên liệu, vật liệu", None, ""),
    "153": ("Công cụ, dụng cụ", None, ""),
    "154": ("Chi phí SXKD dở dang", None, ""),
    "155": ("Thành phẩm", None, ""),
    "156": ("Hàng hoá", None, "1561 hàng tồn · 1562 chi phí thu mua · 1567 BĐS để bán"),
    "1561": ("Hàng hoá tồn kho", "156", ""),
    "1562": ("Chi phí thu mua hàng hoá", "156", ""),
    "157": ("Hàng gửi đi bán", None, ""),
    "158": ("Hàng hoá kho bảo thuế", None, ""),

    # Loại 2 — Tài sản dài hạn
    "211": ("TSCĐ hữu hình", None, "2111 nhà cửa · 2112 máy móc · 2113 phương tiện · 2114 thiết bị QL · 2118 khác"),
    "213": ("TSCĐ vô hình", None, "2131 quyền SD đất · 2135 phần mềm · 2138 khác"),
    "214": ("Hao mòn TSCĐ", None, "Đối ứng với 211/213 — bên Có giảm giá trị TSCĐ"),
    "217": ("Bất động sản đầu tư", None, ""),
    "221": ("Đầu tư vào công ty con", None, ""),
    "222": ("Đầu tư vào công ty liên kết", None, ""),
    "228": ("Đầu tư khác dài hạn", None, ""),
    "242": ("Chi phí trả trước dài hạn", None, ""),

    # Loại 3 — Nợ phải trả
    "331": ("Phải trả người bán", None, "Chi tiết từng NCC; có thể có dư Nợ (ứng trước)"),
    "333": ("Thuế và các khoản phải nộp NN", None, "3331 GTGT · 3334 TNDN · 3335 TNCN · 3338 phí lệ phí"),
    "3331": ("Thuế GTGT phải nộp", "333", "33311 GTGT đầu ra · 33312 GTGT hàng nhập khẩu"),
    "33311": ("Thuế GTGT đầu ra", "3331", "Dùng khi hạch toán VAT đầu ra của hoá đơn bán ra (invoice_vat_out) — Có 33311 khi ghi nhận doanh thu"),
    "33312": ("Thuế GTGT hàng nhập khẩu", "3331", "VAT hàng nhập khẩu phải nộp ở khâu nhập khẩu, khác với 1331/1332 (VAT được khấu trừ)"),
    "3332": ("Thuế tiêu thụ đặc biệt", "333", ""),
    "3333": ("Thuế xuất nhập khẩu", "333", ""),
    "3334": ("Thuế thu nhập doanh nghiệp", "333", ""),
    "3335": ("Thuế thu nhập cá nhân", "333", ""),
    "3336": ("Thuế tài nguyên", "333", ""),
    "3338": ("Phí lệ phí và các khoản phải nộp khác", "333", ""),
    "334": ("Phải trả người lao động", None, "3341 lương · 3348 khác"),
    "335": ("Chi phí phải trả", None, "Trích trước"),
    "336": ("Phải trả nội bộ", None, ""),
    "338": ("Phải trả phải nộp khác", None, "3382 KPCĐ · 3383 BHXH · 3384 BHYT · 3389 BHTN"),
    "3382": ("Kinh phí công đoàn", "338", ""),
    "3383": ("Bảo hiểm xã hội", "338", ""),
    "3384": ("Bảo hiểm y tế", "338", ""),
    "3389": ("Bảo hiểm thất nghiệp", "338", ""),
    "341": ("Vay và nợ thuê tài chính", None, "Sau TT200 — gộp 311 cũ"),

    # Loại 4 — Vốn chủ sở hữu
    "411": ("Vốn đầu tư của chủ sở hữu", None, "4111 vốn góp · 4112 thặng dư · 4118 khác"),
    "412": ("Chênh lệch đánh giá lại tài sản", None, ""),
    "413": ("Chênh lệch tỷ giá hối đoái", None, ""),
    "414": ("Quỹ đầu tư phát triển", None, ""),
    "421": ("Lợi nhuận sau thuế chưa phân phối", None, "4211 năm trước · 4212 năm nay"),

    # Loại 5 — Doanh thu
    "511": ("Doanh thu bán hàng và cung cấp dịch vụ", None, ""),
    "515": ("Doanh thu hoạt động tài chính", None, ""),
    "5211": ("Chiết khấu thương mại", None, ""),
    "5212": ("Hàng bán bị trả lại", None, ""),
    "5213": ("Giảm giá hàng bán", None, ""),

    # Loại 6 — Chi phí
    "621": ("Chi phí nguyên vật liệu trực tiếp", None, ""),
    "622": ("Chi phí nhân công trực tiếp", None, ""),
    "627": ("Chi phí sản xuất chung", None, "6271 NV · 6272 NVL · 6274 KH · 6277 DV mua ngoài"),
    "632": ("Giá vốn hàng bán", None, ""),
    "635": ("Chi phí tài chính", None, "Lãi vay, chiết khấu thanh toán cho KH"),
    "641": ("Chi phí bán hàng", None, ""),
    "642": ("Chi phí quản lý doanh nghiệp", None, ""),

    # Loại 7-8
    "711": ("Thu nhập khác", None, "Thanh lý TSCĐ, hoàn nhập DP, được giảm phạt"),
    "811": ("Chi phí khác", None, ""),
    "821": ("Chi phí thuế TNDN", None, "8211 hiện hành · 8212 hoãn lại"),

    # Loại 9
    "911": ("Xác định kết quả kinh doanh", None, "Cuối kỳ kết chuyển doanh thu, chi phí"),
}


# --------------------------------------------------------------------------
# Data tables — Filing deadlines
# --------------------------------------------------------------------------

FILING_DEADLINES: dict[str, dict[str, Any]] = {
    "01/GTGT": {
        "name": "Tờ khai thuế GTGT theo phương pháp khấu trừ",
        "monthly_deadline_day": 20,
        "quarterly_deadline_day": 30,
        "quarterly_deadline_month_offset": 1,
        "frequency_note": "Mặc định theo tháng; DN doanh thu năm trước <50 tỷ được khai quý.",
    },
    "02/GTGT": {
        "name": "Tờ khai thuế GTGT phương pháp trực tiếp",
        "monthly_deadline_day": 20,
        "quarterly_deadline_day": 30,
        "quarterly_deadline_month_offset": 1,
        "frequency_note": "Áp dụng cho DN doanh thu thấp / hộ kinh doanh.",
    },
    "02/TNDN": {
        "name": "Tờ khai tạm tính thuế TNDN",
        "quarterly_deadline_day": 30,
        "quarterly_deadline_month_offset": 1,
        "frequency_note": "Khai theo quý; tổng tạm nộp 4 quý ≥80% số quyết toán năm.",
    },
    "03/TNDN": {
        "name": "Tờ khai quyết toán thuế TNDN",
        "annual_deadline_day": 31,
        "annual_deadline_month": 3,
        "frequency_note": "Khai theo năm; hạn 90 ngày sau kết thúc năm tài chính.",
    },
    "05/QTT-TNCN": {
        "name": "Tờ khai quyết toán TNCN (DN khấu trừ thay)",
        "annual_deadline_day": 31,
        "annual_deadline_month": 3,
        "frequency_note": "Khai theo năm.",
    },
    "02/QTT-TNCN": {
        "name": "Tờ khai quyết toán TNCN (cá nhân tự QT)",
        "annual_deadline_day": 31,
        "annual_deadline_month": 3,
        "frequency_note": "Khai theo năm; cá nhân tự nộp.",
    },
    "05/KK-TNCN": {
        "name": "Tờ khai thuế TNCN khấu trừ",
        "monthly_deadline_day": 20,
        "frequency_note": "Khai theo tháng nếu trong tháng phát sinh khấu trừ ≥100 triệu.",
    },
    "BCTC": {
        "name": "Báo cáo tài chính năm",
        "annual_deadline_day": 31,
        "annual_deadline_month": 3,
        "frequency_note": "Cùng hạn với QT TNDN — 90 ngày sau kết thúc năm tài chính.",
    },
}


# --------------------------------------------------------------------------
# PIT constants — 2026
# --------------------------------------------------------------------------

PIT_BRACKETS = [
    (5_000_000, 0.05),
    (10_000_000, 0.10),
    (18_000_000, 0.15),
    (32_000_000, 0.20),
    (52_000_000, 0.25),
    (80_000_000, 0.30),
    (float("inf"), 0.35),
]

PIT_PERSONAL_DEDUCTION = 11_000_000
PIT_DEPENDENT_DEDUCTION = 4_400_000

# Social insurance employee rates
BHXH_EMPLOYEE_RATE = 0.08
BHYT_EMPLOYEE_RATE = 0.015
BHTN_EMPLOYEE_RATE = 0.01
SI_EMPLOYEE_TOTAL_RATE = BHXH_EMPLOYEE_RATE + BHYT_EMPLOYEE_RATE + BHTN_EMPLOYEE_RATE  # 10.5%

# Cap for BHXH/BHYT base salary (20× minimum wage = 2,340,000 × 20)
SI_BHXH_BHYT_CAP = 46_800_000


def _compute_pit(taxable_income: float) -> tuple[float, list[dict[str, Any]]]:
    """Apply 7-bracket progressive scale. Returns (tax, breakdown)."""
    if taxable_income <= 0:
        return 0.0, []
    tax = 0.0
    breakdown: list[dict[str, Any]] = []
    prev_cap = 0.0
    for cap, rate in PIT_BRACKETS:
        if taxable_income <= prev_cap:
            break
        portion = min(taxable_income, cap) - prev_cap
        if portion <= 0:
            continue
        bracket_tax = portion * rate
        breakdown.append(
            {
                "range": f"{prev_cap / 1_000_000:.0f}tr → "
                + ("∞" if cap == float("inf") else f"{cap / 1_000_000:.0f}tr"),
                "rate": f"{int(rate * 100)}%",
                "amount_taxed": portion,
                "tax": bracket_tax,
            }
        )
        tax += bracket_tax
        prev_cap = cap
    return tax, breakdown


def _format_vnd(amount: float) -> str:
    return f"{amount:,.0f}".replace(",", ".") + " ₫"


# --------------------------------------------------------------------------
# Tool factories
# --------------------------------------------------------------------------


def build_accounting_tools(*, token: str, gateway_base: str) -> list[Any]:
    """Return the 7 accounting tools for inclusion in /agent/run MCP server."""

    auth_headers = {"Authorization": f"Bearer {token}"}

    @tool(
        "calculate_vat",
        "Tách giá / VAT / tổng tiền hoá đơn theo thuế suất GTGT. "
        "rate ∈ {0, 5, 8, 10}. inclusive=true nếu amount đã gồm VAT.",
        {"amount": float, "rate": int, "inclusive": bool},
    )
    async def calculate_vat(args: dict[str, Any]) -> dict[str, Any]:
        amount = float(args.get("amount", 0))
        rate_pct = int(args.get("rate", 10))
        inclusive = bool(args.get("inclusive", True))
        if amount < 0:
            return {"content": [{"type": "text", "text": "amount phải > 0"}]}
        if rate_pct not in (0, 5, 8, 10):
            return {"content": [{"type": "text", "text": "rate chỉ chấp nhận 0, 5, 8, 10 (%)"}]}

        rate = rate_pct / 100.0
        if inclusive:
            net = amount / (1 + rate) if rate > 0 else amount
            vat = amount - net
            gross = amount
        else:
            net = amount
            vat = amount * rate
            gross = amount + vat

        text = (
            f"VAT {rate_pct}% — input "
            f"{'đã gồm VAT' if inclusive else 'chưa gồm VAT'}: {_format_vnd(amount)}\n\n"
            f"- Giá chưa VAT: **{_format_vnd(net)}**\n"
            f"- VAT: **{_format_vnd(vat)}**\n"
            f"- Tổng thanh toán: **{_format_vnd(gross)}**"
        )
        return {
            "content": [
                {"type": "text", "text": text},
                {
                    "type": "text",
                    "text": str(
                        {
                            "net": round(net, 2),
                            "vat": round(vat, 2),
                            "gross": round(gross, 2),
                            "rate_pct": rate_pct,
                            "inclusive": inclusive,
                        }
                    ),
                },
            ]
        }

    @tool(
        "calculate_pit",
        "Tính thuế TNCN tháng theo biểu lũy tiến 7 bậc + giảm trừ + BH bắt buộc. "
        "Input: gross_monthly (lương gross VNĐ/tháng), dependents (số NPT), "
        "include_si (bool, mặc định True: trừ BHXH/BHYT/BHTN 10.5%).",
        {"gross_monthly": float, "dependents": int, "include_si": bool},
    )
    async def calculate_pit(args: dict[str, Any]) -> dict[str, Any]:
        gross = float(args.get("gross_monthly", 0))
        deps = int(args.get("dependents", 0))
        include_si = bool(args.get("include_si", True))

        if gross < 0 or deps < 0:
            return {"content": [{"type": "text", "text": "Tham số phải không âm"}]}

        if include_si:
            si_base = min(gross, SI_BHXH_BHYT_CAP)
            si_employee = si_base * SI_EMPLOYEE_TOTAL_RATE
        else:
            si_employee = 0.0

        personal_ded = PIT_PERSONAL_DEDUCTION
        dependent_ded = PIT_DEPENDENT_DEDUCTION * deps
        taxable = gross - si_employee - personal_ded - dependent_ded
        if taxable < 0:
            taxable = 0.0

        tax, breakdown = _compute_pit(taxable)
        net = gross - si_employee - tax

        lines = [
            f"**TNCN tháng** (gross {_format_vnd(gross)}, {deps} NPT, "
            f"{'có' if include_si else 'không'} đóng BH):",
            "",
            f"- BHXH/BHYT/BHTN NLĐ (10,5%): **{_format_vnd(si_employee)}**",
            f"- Giảm trừ bản thân: **{_format_vnd(personal_ded)}**",
            f"- Giảm trừ NPT ({deps} × 4,4tr): **{_format_vnd(dependent_ded)}**",
            f"- Thu nhập tính thuế: **{_format_vnd(taxable)}**",
            "",
            "**Breakdown theo bậc:**",
        ]
        for bk in breakdown:
            lines.append(
                f"- Bậc {bk['range']}  ({bk['rate']}): "
                f"{_format_vnd(bk['amount_taxed'])} → {_format_vnd(bk['tax'])}"
            )
        lines.extend(
            [
                "",
                f"- **Tổng TNCN tháng: {_format_vnd(tax)}**",
                f"- **Lương net: {_format_vnd(net)}**",
            ]
        )
        return {
            "content": [
                {"type": "text", "text": "\n".join(lines)},
                {
                    "type": "text",
                    "text": str(
                        {
                            "gross": gross,
                            "si_employee": round(si_employee, 2),
                            "personal_ded": personal_ded,
                            "dependent_ded": dependent_ded,
                            "taxable": round(taxable, 2),
                            "tax": round(tax, 2),
                            "net": round(net, 2),
                        }
                    ),
                },
            ]
        }

    @tool(
        "calculate_cit",
        "Tính thuế TNDN năm. Input: revenue (doanh thu), expense (chi phí được trừ), "
        "loss_carryforward (lỗ chuyển từ năm trước), rate_pct (thuế suất 20/17/15/10, "
        "mặc định 20).",
        {"revenue": float, "expense": float, "loss_carryforward": float, "rate_pct": int},
    )
    async def calculate_cit(args: dict[str, Any]) -> dict[str, Any]:
        revenue = float(args.get("revenue", 0))
        expense = float(args.get("expense", 0))
        loss = float(args.get("loss_carryforward", 0))
        rate_pct = int(args.get("rate_pct", 20))

        if rate_pct not in (5, 10, 15, 17, 20):
            return {
                "content": [
                    {"type": "text", "text": "rate_pct chỉ chấp nhận 5/10/15/17/20"}
                ]
            }

        taxable = revenue - expense - max(loss, 0)
        if taxable < 0:
            tax = 0.0
        else:
            tax = taxable * (rate_pct / 100)

        lines = [
            "**Thuế TNDN năm:**",
            "",
            f"- Doanh thu: {_format_vnd(revenue)}",
            f"- Chi phí được trừ: {_format_vnd(expense)}",
            f"- Lỗ kết chuyển: {_format_vnd(loss)}",
            f"- Thu nhập tính thuế: **{_format_vnd(max(taxable, 0))}**"
            + (f" (đang lỗ {_format_vnd(-taxable)} — không nộp thuế)" if taxable < 0 else ""),
            f"- Thuế suất: **{rate_pct}%**",
            "",
            f"- **Thuế TNDN phải nộp: {_format_vnd(tax)}**",
        ]
        return {
            "content": [
                {"type": "text", "text": "\n".join(lines)},
                {
                    "type": "text",
                    "text": str(
                        {
                            "revenue": revenue,
                            "expense": expense,
                            "loss_carryforward": loss,
                            "taxable_income": round(taxable, 2),
                            "rate_pct": rate_pct,
                            "tax": round(tax, 2),
                        }
                    ),
                },
            ]
        }

    @tool(
        "lookup_account",
        "Tra tài khoản theo mã số TT200. Trả về tên TK + TK con + ghi chú. "
        "code có thể là 3-4 chữ số (vd '156' hoặc '1561').",
        {"code": str},
    )
    async def lookup_account(args: dict[str, Any]) -> dict[str, Any]:
        code = str(args.get("code", "")).strip().replace("TK", "").replace(" ", "")
        if not code:
            return {"content": [{"type": "text", "text": "code là bắt buộc"}]}

        entry = TT200_ACCOUNTS.get(code)
        if not entry:
            # Try matching as prefix
            matches = [(k, v) for k, v in TT200_ACCOUNTS.items() if k.startswith(code)]
            if not matches:
                return {
                    "content": [
                        {
                            "type": "text",
                            "text": f"TK {code} không có trong bảng tra TT200 phổ biến. "
                            "Hãy kiểm tra TT200/2014/TT-BTC để tra chính xác.",
                        }
                    ]
                }
            text = f"Không thấy TK {code} chính xác — các TK có prefix tương tự:\n"
            for k, (name, _parent, _note) in matches[:10]:
                text += f"- TK {k} — {name}\n"
            return {"content": [{"type": "text", "text": text}]}

        name, parent, note = entry
        children = [
            (k, v[0]) for k, v in TT200_ACCOUNTS.items() if v[1] == code
        ]
        lines = [f"**TK {code} — {name}**"]
        if parent:
            parent_name = TT200_ACCOUNTS.get(parent, (parent,))[0]
            lines.append(f"- TK cấp trên: TK {parent} — {parent_name}")
        if note:
            lines.append(f"- Ghi chú: {note}")
        if children:
            lines.append(f"- TK con ({len(children)}):")
            for child_code, child_name in children:
                lines.append(f"  · TK {child_code} — {child_name}")
        lines.append("")
        lines.append("_Tham chiếu: TT200/2014/TT-BTC_")
        return {
            "content": [
                {"type": "text", "text": "\n".join(lines)},
                {"type": "text", "text": str({"code": code, "name": name, "parent": parent})},
            ]
        }

    @tool(
        "check_filing_deadline",
        "Tra deadline tờ khai thuế. form_code ∈ {01/GTGT, 02/GTGT, 02/TNDN, "
        "03/TNDN, 05/QTT-TNCN, 02/QTT-TNCN, 05/KK-TNCN, BCTC}. "
        "period là kỳ kê khai ('Q3/2026', 'T10/2026', '2025').",
        {"form_code": str, "period": str},
    )
    async def check_filing_deadline(args: dict[str, Any]) -> dict[str, Any]:
        form_code = str(args.get("form_code", "")).strip().upper().replace(" ", "")
        period = str(args.get("period", "")).strip()
        if not form_code or not period:
            return {
                "content": [
                    {"type": "text", "text": "form_code và period đều bắt buộc"}
                ]
            }

        meta = FILING_DEADLINES.get(form_code)
        if not meta:
            valid = ", ".join(FILING_DEADLINES.keys())
            return {
                "content": [
                    {
                        "type": "text",
                        "text": f"form_code không hợp lệ. Chọn 1 trong: {valid}",
                    }
                ]
            }

        # Parse period
        period_upper = period.upper().replace(" ", "")
        deadline = "?"
        try:
            if period_upper.startswith("Q") and "/" in period_upper:
                q_num = int(period_upper[1:].split("/")[0])
                year = int(period_upper.split("/")[1])
                if "quarterly_deadline_day" in meta:
                    end_month = q_num * 3
                    next_month = end_month + (meta.get("quarterly_deadline_month_offset", 1))
                    target_year = year + (1 if next_month > 12 else 0)
                    target_month = ((next_month - 1) % 12) + 1
                    deadline = f"{meta['quarterly_deadline_day']:02d}/{target_month:02d}/{target_year}"
            elif period_upper.startswith("T") and "/" in period_upper:
                month_num = int(period_upper[1:].split("/")[0])
                year = int(period_upper.split("/")[1])
                if "monthly_deadline_day" in meta:
                    target_month = month_num + 1
                    target_year = year + (1 if target_month > 12 else 0)
                    target_month = ((target_month - 1) % 12) + 1
                    deadline = f"{meta['monthly_deadline_day']:02d}/{target_month:02d}/{target_year}"
            else:
                # Annual
                year = int(period_upper)
                if "annual_deadline_day" in meta:
                    deadline = (
                        f"{meta['annual_deadline_day']:02d}/"
                        f"{meta['annual_deadline_month']:02d}/{year + 1}"
                    )
        except (ValueError, IndexError):
            deadline = "?"

        lines = [
            f"**{form_code} — {meta['name']}**",
            "",
            f"- Kỳ: {period}",
            f"- Deadline: **{deadline}**",
            f"- Ghi chú: {meta['frequency_note']}",
            "",
            "_Phạt chậm nộp: 2-25 triệu ₫ + 0,03%/ngày trên số tiền thuế chậm nộp (NĐ125/2020/NĐ-CP)._",
        ]
        return {
            "content": [
                {"type": "text", "text": "\n".join(lines)},
                {
                    "type": "text",
                    "text": str(
                        {"form_code": form_code, "period": period, "deadline": deadline}
                    ),
                },
            ]
        }

    @tool(
        "journal_entry_validator",
        "Validate 1 bút toán: kiểm tra tổng Nợ = tổng Có + TK có trong TT200 + "
        "có ít nhất 1 vế Nợ và 1 vế Có. lines là list dict "
        "{side: 'Nợ'|'Có', account: '156', amount: 100000000, description?: '...'}.",
        {"lines": list},
    )
    async def journal_entry_validator(args: dict[str, Any]) -> dict[str, Any]:
        lines_in = args.get("lines") or []
        if not isinstance(lines_in, list) or not lines_in:
            return {
                "content": [
                    {"type": "text", "text": "lines phải là list không rỗng"}
                ]
            }

        errors: list[str] = []
        warnings: list[str] = []
        total_debit = 0.0
        total_credit = 0.0
        has_debit = False
        has_credit = False
        normalized: list[dict[str, Any]] = []

        for idx, raw in enumerate(lines_in, start=1):
            if not isinstance(raw, dict):
                errors.append(f"Dòng {idx}: phải là dict, không phải {type(raw).__name__}")
                continue
            side_raw = str(raw.get("side", "")).strip().lower()
            side_is_debit = side_raw in ("nợ", "no", "debit", "dr", "d")
            side_is_credit = side_raw in ("có", "co", "credit", "cr", "c")
            if not (side_is_debit or side_is_credit):
                errors.append(f"Dòng {idx}: side phải là 'Nợ' hoặc 'Có' (nhận '{raw.get('side')}')")
                continue
            account = str(raw.get("account", "")).strip().replace("TK", "").replace(" ", "")
            if not account:
                errors.append(f"Dòng {idx}: thiếu account")
                continue
            if account not in TT200_ACCOUNTS:
                warnings.append(
                    f"Dòng {idx}: TK {account} không có trong bảng tra TT200 phổ biến — "
                    "kiểm tra lại"
                )
            try:
                amount = float(raw.get("amount", 0))
            except (TypeError, ValueError):
                errors.append(f"Dòng {idx}: amount phải là số")
                continue
            if amount <= 0:
                errors.append(f"Dòng {idx}: amount phải > 0")
                continue

            if side_is_debit:
                total_debit += amount
                has_debit = True
            else:
                total_credit += amount
                has_credit = True
            normalized.append(
                {
                    "side": "Nợ" if side_is_debit else "Có",
                    "account": account,
                    "amount": amount,
                    "description": raw.get("description", ""),
                }
            )

        if not has_debit:
            errors.append("Thiếu ít nhất 1 vế Nợ")
        if not has_credit:
            errors.append("Thiếu ít nhất 1 vế Có")
        if abs(total_debit - total_credit) > 1:  # cho phép sai số 1đ do làm tròn
            errors.append(
                f"Tổng Nợ ({_format_vnd(total_debit)}) ≠ Tổng Có ({_format_vnd(total_credit)}) — "
                f"lệch {_format_vnd(abs(total_debit - total_credit))}"
            )

        ok = len(errors) == 0
        lines_out = [
            f"**Kết quả validate: {'✅ HỢP LỆ' if ok else '❌ LỖI'}**",
            "",
            "**Các dòng:**",
        ]
        for n in normalized:
            tk = n["account"]
            tk_name = TT200_ACCOUNTS.get(tk, ("?", None, ""))[0]
            lines_out.append(
                f"- {n['side']} TK {tk}  — {tk_name}  | {_format_vnd(n['amount'])}"
                + (f"  ({n['description']})" if n["description"] else "")
            )
        lines_out.extend(
            [
                "",
                f"- Tổng Nợ: **{_format_vnd(total_debit)}**",
                f"- Tổng Có: **{_format_vnd(total_credit)}**",
            ]
        )
        if errors:
            lines_out.append("")
            lines_out.append("**Lỗi:**")
            for e in errors:
                lines_out.append(f"- ❌ {e}")
        if warnings:
            lines_out.append("")
            lines_out.append("**Cảnh báo:**")
            for w in warnings:
                lines_out.append(f"- ⚠️ {w}")

        return {
            "content": [
                {"type": "text", "text": "\n".join(lines_out)},
                {
                    "type": "text",
                    "text": str(
                        {
                            "ok": ok,
                            "total_debit": round(total_debit, 2),
                            "total_credit": round(total_credit, 2),
                            "errors": errors,
                            "warnings": warnings,
                        }
                    ),
                },
            ]
        }

    @tool(
        "import_excel_ledger",
        "Tải file CSV/XLSX từ URL công khai và parse thành rows. Hỗ trợ CSV "
        "(UTF-8, dấu phẩy hoặc tab) và XLSX (nếu openpyxl khả dụng). "
        "Trả về tối đa 200 hàng đầu. Cap 1 MB.",
        {"file_url": str, "sheet_name": str},
    )
    async def import_excel_ledger(args: dict[str, Any]) -> dict[str, Any]:
        url = str(args.get("file_url", "")).strip()
        sheet = str(args.get("sheet_name", "")).strip() or None
        if not url.startswith(("http://", "https://")):
            return {
                "content": [
                    {"type": "text", "text": "file_url phải bắt đầu bằng http:// hoặc https://"}
                ]
            }

        try:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers={"User-Agent": "AIQ-Agent/0.1 (+import_excel_ledger)"},
            ) as client:
                response = await client.get(url)
                if response.status_code >= 400:
                    return {
                        "content": [
                            {
                                "type": "text",
                                "text": f"HTTP {response.status_code} fetching {url}",
                            }
                        ]
                    }
                raw_bytes = response.content
                if len(raw_bytes) > 1_048_576:
                    return {
                        "content": [
                            {
                                "type": "text",
                                "text": f"File quá lớn ({len(raw_bytes) // 1024} KB > 1 MB cap)",
                            }
                        ]
                    }
                content_type = response.headers.get("content-type", "").lower()
        except httpx.HTTPError as exc:
            return {
                "content": [
                    {"type": "text", "text": f"Fetch error: {exc.__class__.__name__}: {exc}"}
                ]
            }

        rows: list[list[Any]] = []
        source_format = "unknown"

        # Try XLSX first if extension matches
        if url.lower().endswith((".xlsx", ".xls")) or "spreadsheet" in content_type:
            try:
                import openpyxl  # type: ignore[import-not-found]

                wb = openpyxl.load_workbook(io.BytesIO(raw_bytes), data_only=True)
                ws = wb[sheet] if sheet and sheet in wb.sheetnames else wb.active
                for row in ws.iter_rows(values_only=True):
                    rows.append(list(row))
                    if len(rows) >= 200:
                        break
                source_format = f"xlsx (sheet={ws.title})"
            except ImportError:
                return {
                    "content": [
                        {
                            "type": "text",
                            "text": "openpyxl chưa được cài. Convert sang CSV rồi upload lại.",
                        }
                    ]
                }
            except Exception as exc:  # noqa: BLE001
                return {
                    "content": [
                        {
                            "type": "text",
                            "text": f"Lỗi parse XLSX: {exc.__class__.__name__}: {exc}",
                        }
                    ]
                }
        else:
            # Treat as CSV/TSV
            text = raw_bytes.decode("utf-8", errors="replace")
            import csv as csv_module

            sniffer = csv_module.Sniffer()
            try:
                dialect = sniffer.sniff(text[:4096], delimiters=",\t;|")
            except csv_module.Error:
                dialect = csv_module.excel
            reader = csv_module.reader(io.StringIO(text), dialect=dialect)
            for row in reader:
                rows.append(list(row))
                if len(rows) >= 200:
                    break
            source_format = f"csv (delimiter={dialect.delimiter!r})"

        if not rows:
            return {"content": [{"type": "text", "text": "Không parse được dòng nào"}]}

        preview = "\n".join(
            "| " + " | ".join(str(c) if c is not None else "" for c in row[:8]) + " |"
            for row in rows[:20]
        )

        text = (
            f"**Đã parse {len(rows)} hàng từ {source_format}**\n"
            f"_Source: {url}_\n\n"
            "**Preview 20 hàng đầu (max 8 cột):**\n\n"
            f"{preview}\n\n"
            "Dùng `save_note` để lưu phần tổng hợp thành Note nếu cần."
        )
        return {
            "content": [
                {"type": "text", "text": text},
                {
                    "type": "text",
                    "text": str(
                        {
                            "rows_count": len(rows),
                            "format": source_format,
                            "sample_rows": rows[:5],
                        }
                    ),
                },
            ]
        }

    return [
        calculate_vat,
        calculate_pit,
        calculate_cit,
        lookup_account,
        check_filing_deadline,
        journal_entry_validator,
        import_excel_ledger,
    ]
