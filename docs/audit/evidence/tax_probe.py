"""Read-only golden-case probe of aiq_agent.agentic.accounting_tools.

Calls the real tool handlers (no LLM, no network, no DB) and compares with
expected values derived from the law in force for the tested period.
"""

import ast
import asyncio

from aiq_agent.agentic.accounting_tools import build_accounting_tools

tools = {t.name: t for t in build_accounting_tools(token="x", gateway_base="http://invalid")}


async def call(name, args):
    res = await tools[name].handler(args)
    blocks = res["content"]
    if len(blocks) < 2:
        return {"error": blocks[0]["text"]}
    return ast.literal_eval(blocks[1]["text"])


def pit_2026(taxable):
    # Luật 109/2025/QH15 Điều 9 — 5 bậc (monthly)
    brackets = [(10e6, .05), (30e6, .10), (60e6, .20), (100e6, .30), (float("inf"), .35)]
    tax, prev = 0.0, 0.0
    for cap, r in brackets:
        if taxable <= prev:
            break
        tax += (min(taxable, cap) - prev) * r
        prev = cap
    return tax


rows = []


def check(case_id, desc, actual, expected, tol=0.5):
    if isinstance(expected, str):
        ok = actual == expected
    else:
        ok = actual is not None and abs(actual - expected) <= tol
    rows.append((case_id, "PASS" if ok else "FAIL", desc, actual, expected))


async def main():
    # ---------------- VAT ----------------
    r = await call("calculate_vat", {"amount": 110_000_000, "rate": 10, "inclusive": True})
    check("VAT-01", "110tr gồm VAT 10% → VAT", r["vat"], 10_000_000)
    r = await call("calculate_vat", {"amount": 108_000_000, "rate": 8, "inclusive": True})
    check("VAT-02", "108tr gồm VAT 8% → VAT", r["vat"], 8_000_000)
    r = await call("calculate_vat", {"amount": 100_000_000, "rate": 5, "inclusive": False})
    check("VAT-03", "100tr chưa VAT 5% → VAT", r["vat"], 5_000_000)
    r = await call("calculate_vat", {"amount": 1_234_567, "rate": 10, "inclusive": True})
    check("VAT-04", "1.234.567 gồm VAT 10% → VAT làm tròn đồng (112.233)", r["vat"], 112_233, tol=0)
    r = await call("calculate_vat", {"amount": 100, "rate": 7, "inclusive": False})
    check("VAT-05", "thuế suất 7% bị từ chối", "error" in r, True, tol=0)
    r = await call("calculate_vat", {"amount": 50_000_000, "rate": 0, "inclusive": False})
    check("VAT-06", "0% → VAT 0", r["vat"], 0)

    # ---------------- PIT (kỳ 2026) ----------------
    # 30tr gross, 0 NPT, đóng BH: BH 3.15tr; TNTT = 30 - 3.15 - 15.5 = 11.35tr
    r = await call("calculate_pit", {"gross_monthly": 30_000_000, "dependents": 0, "include_si": True})
    check("PIT-01", "30tr, 0 NPT — giảm trừ bản thân 2026 (15.5tr)", r["personal_ded"], 15_500_000)
    check("PIT-02", "30tr, 0 NPT — thuế TNCN tháng kỳ 2026", r["tax"], pit_2026(30e6 - 3.15e6 - 15.5e6))
    # 50tr gross, 2 NPT: BHXH+BHYT 9.5% × 46.8tr = 4.446tr; BHTN 1% × 50tr = 0.5tr
    r = await call("calculate_pit", {"gross_monthly": 50_000_000, "dependents": 2, "include_si": True})
    check("PIT-03", "50tr, 2 NPT — giảm trừ NPT 2026 (2 × 6.2tr)", r["dependent_ded"], 12_400_000)
    check("PIT-04", "50tr — BH NLĐ (BHTN trần 20×LTTV vùng I = 106.2tr)", r["si_employee"], 4_946_000)
    si = 4_946_000
    check("PIT-05", "50tr, 2 NPT — thuế TNCN tháng kỳ 2026", r["tax"], pit_2026(50e6 - si - 15.5e6 - 12.4e6))
    # Regression of the code's own (2025) model: 25tr TNTT → 3.35tr (skill example)
    r = await call("calculate_pit", {"gross_monthly": 36_000_000, "dependents": 0, "include_si": False})
    check("PIT-06", "Biểu 7 bậc cũ: TNTT 25tr → 3.35tr (đúng cho kỳ 2025)", r["tax"], 3_350_000)
    # Edge: TNTT exactly at bracket boundary 10tr (2026) → 500k
    r = await call("calculate_pit", {"gross_monthly": 25_500_000, "dependents": 0, "include_si": False})
    check("PIT-07", "TNTT = 10tr (ranh giới bậc 2026) → 500.000", r["tax"], 500_000)

    # ---------------- CIT ----------------
    r = await call("calculate_cit", {"revenue": 10e9, "expense": 8e9, "loss_carryforward": 0})
    check("CIT-01", "DT 10 tỷ (3–50 tỷ) — mặc định không có rate → 17% = 340tr", r["tax"], 340e6)
    r = await call("calculate_cit", {"revenue": 2e9, "expense": 1.5e9, "loss_carryforward": 0})
    check("CIT-02", "DT 2 tỷ (≤3 tỷ) — mặc định → 15% = 75tr", r["tax"], 75e6)
    r = await call("calculate_cit", {"revenue": 200e9, "expense": 150e9, "loss_carryforward": 0, "rate_pct": 17})
    check("CIT-03", "DT 200 tỷ + rate 17% phải bị từ chối", "error" in r, True, tol=0)
    r = await call("calculate_cit", {"revenue": 100e9, "expense": 80e9, "loss_carryforward": 5e9, "rate_pct": 20})
    check("CIT-04", "DT 100 tỷ, lỗ 5 tỷ, 20% → 3 tỷ", r["tax"], 3e9)
    r = await call("calculate_cit", {"revenue": 100e9, "expense": 80e9, "loss_carryforward": -5e9, "rate_pct": 20})
    check("CIT-05", "lỗ âm bị bỏ qua (max(loss,0)) → 4 tỷ", r["tax"], 4e9)

    # ---------------- Deadlines ----------------
    async def dl(form, period):
        return (await call("check_filing_deadline", {"form_code": form, "period": period}))["deadline"]

    check("DL-01", "01/GTGT T12/2026 → 20/01/2027", await dl("01/GTGT", "T12/2026"), "20/01/2027")
    check("DL-02", "01/GTGT Q1/2026 → 30/04/2026 (trước dời ngày nghỉ)", await dl("01/GTGT", "Q1/2026"), "30/04/2026")
    check("DL-03", "01/GTGT Q2/2026 → 31/07/2026", await dl("01/GTGT", "Q2/2026"), "31/07/2026")
    check("DL-04", "01/GTGT Q3/2026 → 31/10/2026", await dl("01/GTGT", "Q3/2026"), "31/10/2026")
    check("DL-05", "01/GTGT Q4/2026 → 31/01/2027", await dl("01/GTGT", "Q4/2026"), "31/01/2027")
    check("DL-06", "02/QTT-TNCN 2025 (cá nhân tự QT) → 30/04/2026", await dl("02/QTT-TNCN", "2025"), "30/04/2026")
    check("DL-07", "05/QTT-TNCN 2025 → 31/03/2026", await dl("05/QTT-TNCN", "2025"), "31/03/2026")
    check("DL-08", "03/TNDN 2025 (năm dương lịch) → 31/03/2026", await dl("03/TNDN", "2025"), "31/03/2026")

    for row in rows:
        print(" | ".join(str(c) for c in row))
    print(f"TOTAL {len(rows)} PASS {sum(r[1] == 'PASS' for r in rows)} FAIL {sum(r[1] == 'FAIL' for r in rows)}")


asyncio.run(main())
