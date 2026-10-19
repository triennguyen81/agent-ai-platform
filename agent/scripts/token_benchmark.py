"""Offline token benchmark (no LLM calls, no network, no database).

Builds the EXACT system prompt / user message / tool list the runtime would
send for representative AIQ requests — using the real composition code
(runtime.executors) and the real skill files — under the baseline and each
optimisation phase, and reports their sizes.

MEASURED here: characters of everything the runtime composes, tool counts,
LLM calls per OCR document. NOT measured here: tokens. Token figures are
ESTIMATES bounded by two assumptions (see `estimate`) and calibrated on the
production medians of docs/audits/TOKEN_USAGE_AUDIT.md; the live benchmark
(token_benchmark_live.py, paid, needs approval) is what verifies them.

Usage (inside the agent image, from agent/):
    PYTHONPATH=src python scripts/token_benchmark.py [--json out.json]
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass
from pathlib import Path
from uuid import uuid4

from aiq_agent.runtime.executors import (
    _installed_tools,
    _plan_skills,
    _resolve_skills,
    _system_parts,
    extraction_manifest,
)
from aiq_agent.runtime.manifest import ExecutionManifest
from aiq_agent.skills.loader import build_registry

ROOT = Path(__file__).resolve().parents[1]

# Production configuration of "Trợ lý kế toán" (read-only audit 2026-10-11):
# 9 image skills, 23 granted tools, maxTurns 8, no KB, ~1.3k-char prompt.
LIVE_SKILLS = [
    "accounting-vietnam", "accounting-vn-style", "compliance-citation", "contract-checklist-builder",
    "contract-extractor", "tax-calculation", "url-ingest", "vietnamese-style", "voucher-invoice-vat",
]
LIVE_TOOLS = [
    "search_notes", "get_note", "calculate_vat", "calculate_pit", "calculate_cit", "lookup_account",
    "check_filing_deadline", "journal_entry_validator", "extract_contract_fields", "save_contract", "get_contract",
    "save_checklist_items", "extract_voucher_fields", "verify_einvoice_tct", "suggest_journal_entry",
    "list_customers", "get_customer_receivables", "list_suppliers", "get_supplier_payables", "get_ap_summary",
    "list_vouchers", "get_weekly_summary_report", "propose_tax_verification",
]
AGENT_SYSTEM_PROMPT = (
    "Bạn là Trợ lý Kế toán của AIQ. Hỗ trợ người dùng tra cứu, kiểm tra, phân tích và đề xuất xử lý các nghiệp "
    "vụ kế toán trong phạm vi các Nghiệp vụ và Tools được cấp cho Agent. " * 4
)[:1271]

# Production medians, first LLM call of a request (TOKEN_USAGE_AUDIT.md §1, MEASURED).
MEASURED_FIRST_CALL = {"extraction": 32_031, "chat": 31_009}
# Smallest measured agent prompt (B p10) — used as the "fixed overhead never
# shrinks" bound.
MEASURED_FIXED_FLOOR = 6_888

OCR_SAMPLE = (
    "HOA DON GIA TRI GIA TANG\nKy hieu: 1C26TAA So: 0001234\nNgay 20 thang 09 nam 2026\n"
    "Don vi ban hang: CONG TY TNHH THUONG MAI MAU\nMa so thue: 0312345678\n"
    "Ten hang hoa: Giay in A4 - SL 50 - Don gia 65.000 - Thanh tien 3.250.000\n"
    "Cong tien hang: 3.250.000\nThue suat GTGT: 10%  Tien thue: 325.000\nTong cong thanh toan: 3.575.000\n"
)  # synthetic; production OCR text median is 646 chars (measured)


def gateway_like_extraction_prompt(raw: str, legacy: bool) -> str:
    """Structure + skill references of DocumentExtraction.BuildManifestPrompt
    (manifest) / VoucherExtractJobRunner.BuildExtractionPrompt (legacy)."""
    fields = "\n".join(f"- field_{i} (string): Trường {i}" for i in range(14))
    block1 = ("**BLOCK 1** — Áp dụng skill `voucher-invoice-vat`, đúng phần mô tả cho voucher_type đã xác định."
              if legacy else
              "**BLOCK 1** — Trích xuất fields theo đúng SCHEMA của voucher_type đã xác định.\n"
              "### invoice_vat_in — Hoá đơn GTGT đầu vào\n" + fields)
    return (
        "Bạn là chuyên viên kế toán VN. Xử lý raw OCR text của 1 chứng từ bên dưới.\n"
        "TỰ XÁC ĐỊNH voucher_type từ nội dung trong: invoice_vat_out, invoice_vat_in, receipt, payment, "
        "bank_transfer, bank_statement, expense_claim, contract.\n\n" + block1 + "\n\n"
        "**BLOCK 2** — Áp dụng skill `journal-entry-formatter` + `accounting-vietnam` để đề xuất bút toán N/C theo TT200\n"
        "Trả về JSON array các entries.\n\n**QUAN TRỌNG**: chỉ trả 2 code block JSON.\n\n"
        "===== RAW OCR TEXT =====\n" + raw + "\n===== END =====\n"
    )


CHAT_SCENARIOS = {
    "chat · công nợ": "Công nợ phải thu của khách hàng Minh An hiện là bao nhiêu?",
    "chat · thuế GTGT": "Tính thuế GTGT phải nộp tháng 9 khi đầu ra 500 triệu, đầu vào 320 triệu.",
    "chat · hợp đồng": "Tạo checklist theo dõi các mốc thanh toán của hợp đồng HD-2026-12.",
    "chat · định khoản": "Định khoản nghiệp vụ mua hàng nhập kho chưa thanh toán người bán.",
    "chat · chào hỏi": "Xin chào, bạn giúp được gì?",
}


def manifest(**overrides) -> ExecutionManifest:
    data = {
        "userId": str(uuid4()), "tenantId": str(uuid4()),
        "agent": {"id": str(uuid4()), "slug": "tro-ly-ke-toan", "name": "Trợ lý kế toán", "description": None,
                  "iconMetadata": {}},
        "publishedVersionId": str(uuid4()), "version": 4, "provider": "claude_sdk", "model": "sonnet",
        "systemPrompt": AGENT_SYSTEM_PROMPT, "temperature": 0.2, "maxTurns": 8, "timeoutSeconds": 120,
        "maxTokens": 2048, "memoryConfig": {"mode": "session"}, "skills": list(LIVE_SKILLS), "knowledge": [],
        "tools": [{"id": str(uuid4()), "code": f"mcp__aiq__{name}", "name": name, "description": None,
                   "category": None, "riskTier": "read"} for name in LIVE_TOOLS],
        "companyProfile": {"legalName": "CÔNG TY CỔ PHẦN MẪU", "taxCode": "0312345678", "shortName": "MẪU",
                           "aliases": [], "registeredAddress": "TP.HCM", "legalRepresentative": None,
                           "representativeTitle": None, "phone": None, "email": None, "website": None},
    }
    data.update(overrides)
    return ExecutionManifest.model_validate(data)


@dataclass
class Composition:
    system_chars: int
    user_chars: int
    tool_count: int
    tool_def_chars: int
    skills_sent: int

    @property
    def total_chars(self) -> int:
        return self.system_chars + self.user_chars + self.tool_def_chars


def _tool_def_chars(codes: list[str], extra: int = 0) -> int:
    _, installed = _installed_tools("t", "http://gateway")
    total = extra
    for code in codes:
        t = installed.get(code)
        if t is not None:
            total += len(t.description or "") + len(json.dumps({k: str(v) for k, v in dict(t.input_schema).items()}))
    return total


def compose_extraction(task: str, *, lean: bool, registry) -> Composition:
    m = manifest()
    ex = extraction_manifest(m, task, lean=lean)
    skills = _resolve_skills(ex, registry)
    system = "\n\n".join(_system_parts(ex, skills))
    return Composition(len(system), len(f"USER REQUEST:\n{task}"), 0, 0, len(skills.names))


def compose_chat(prompt: str, *, dynamic: bool, registry) -> Composition:
    m = manifest()
    allowed = [t.code for t in m.tools]
    if dynamic:
        plan = _plan_skills(m, registry, prompt)
        parts = _system_parts(m, plan.inline)
        if plan.catalog:
            parts.insert(4 if plan.inline.block else 3, plan.catalog)
        system = "\n\n".join(parts)
        user = "\n\n".join(p for p in (plan.preselected_block, f"USER REQUEST:\n{prompt}") if p)
        load_skill_def = 220 if plan.loadable else 0
        sent = len(plan.inline.names) + len(plan.preselected_names)
        return Composition(len(system), len(user), len(allowed) + (1 if plan.loadable else 0),
                           _tool_def_chars(allowed, load_skill_def), sent)
    skills = _resolve_skills(m, registry)
    system = "\n\n".join(_system_parts(m, skills))
    return Composition(len(system), len(f"USER REQUEST:\n{prompt}"), len(allowed), _tool_def_chars(allowed),
                       len(skills.names))


def estimate(measured_baseline: int, base: Composition, opt: Composition) -> tuple[int, int]:
    """ESTIMATED first-call prompt tokens after optimisation, as a range.

    low  = everything scales with composed characters;
    high = the smallest measured agent prompt (MEASURED_FIXED_FLOOR) is a
           fixed CLI/tool overhead that does not shrink, only the rest scales.
    """
    ratio = opt.total_chars / base.total_chars
    low = round(measured_baseline * ratio)
    high = round(MEASURED_FIXED_FLOOR + (measured_baseline - MEASURED_FIXED_FLOOR) * ratio)
    return low, high


def ocr_calls(pages: list[str], batch_chars: int) -> int:
    from aiq_agent.ocr.pipeline import _batches

    if batch_chars <= 0:
        return sum(1 for p in pages if p.strip())
    return sum(1 for group in _batches(pages, batch_chars) if any(p.strip() for p in group))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()
    registry = build_registry(ROOT / "skills")
    rows = []

    for name, legacy in (("trích xuất · cấu hình (manifest)", False), ("trích xuất · prompt legacy", True)):
        task = gateway_like_extraction_prompt(OCR_SAMPLE, legacy)
        base = compose_extraction(task, lean=False, registry=registry)
        opt = compose_extraction(task, lean=True, registry=registry)
        rows.append((name, "Phase 2", base, opt, estimate(MEASURED_FIRST_CALL["extraction"], base, opt)))

    for name, prompt in CHAT_SCENARIOS.items():
        base = compose_chat(prompt, dynamic=False, registry=registry)
        opt = compose_chat(prompt, dynamic=True, registry=registry)
        rows.append((name, "Phase 4", base, opt, estimate(MEASURED_FIRST_CALL["chat"], base, opt)))

    print("| Kịch bản | Phase | Skill gửi (trước→sau) | Ký tự prompt+tool (ĐO, trước→sau) | Giảm | "
          "Token call đầu ƯỚC LƯỢNG (trước ĐO → sau, khoảng) |")
    print("|---|---|---|---|---|---|")
    out = []
    for name, phase, base, opt, (low, high) in rows:
        cut = 1 - opt.total_chars / base.total_chars
        measured = MEASURED_FIRST_CALL["extraction" if "trích" in name else "chat"]
        print(f"| {name} | {phase} | {base.skills_sent}→{opt.skills_sent} | {base.total_chars:,}→{opt.total_chars:,} | "
              f"{cut:.0%} | {measured:,} → {low:,}–{high:,} |")
        out.append({"scenario": name, "phase": phase, "baseline": base.__dict__, "optimized": opt.__dict__,
                    "char_reduction": round(cut, 4), "est_tokens_low": low, "est_tokens_high": high,
                    "measured_baseline_tokens": measured})

    pages = [OCR_SAMPLE] * 5
    ocr = {mode: ocr_calls(pages, chars) for mode, chars in (("per_page", 0), ("batch_8000", 8000))}
    print(f"\nOCR 5 trang × {len(OCR_SAMPLE)} ký tự: LLM call per-page={ocr['per_page']}, batch 8000={ocr['batch_8000']} (ĐO)")
    if args.json:
        args.json.write_text(json.dumps({"rows": out, "ocr_calls_5_pages": ocr}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
