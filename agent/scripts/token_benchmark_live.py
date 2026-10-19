"""LIVE token / cost / latency / accuracy benchmark — CALLS THE PAID LLM.

Runs synthetic documents (no customer data) through the real runtime code
with the real Claude CLI, baseline vs optimised, and records — from the
CLI's own accounting — uncached input, cache write, cache read and output
tokens, cost, LLM call count, latency, and accuracy against golden values.

Never runs by accident: requires --confirm-paid-llm, stops when the
cumulative reported cost exceeds --max-usd. Makes no Gateway / database
calls (tool-less manifests; usage is printed, not reported).

Usage (inside the agent container, logged-in CLI):
    PYTHONPATH=src python scripts/token_benchmark_live.py --confirm-paid-llm --max-usd 3 --out live.json
"""

from __future__ import annotations

import argparse
import asyncio
import difflib
import json
import re
import sys
import time
from pathlib import Path

from aiq_agent.config import Settings
from aiq_agent.llm.claude_cli import ClaudeCliLlmService
from aiq_agent.ocr.corrector import DiacriticCorrector
from aiq_agent.runtime.executors import build_agent_executor, extraction_manifest
from aiq_agent.skills.loader import build_registry

sys.path.insert(0, str(Path(__file__).resolve().parent))
from token_benchmark import CHAT_SCENARIOS, ROOT, gateway_like_extraction_prompt, manifest  # noqa: E402

# Synthetic documents with golden values (fictitious companies / numbers).
DOCUMENTS = [
    {
        "name": "hoá đơn GTGT đầu vào",
        "text": "HOA DON GIA TRI GIA TANG\nKy hieu: 1C26TAA So: 0001234\nNgay 20 thang 09 nam 2026\n"
                "Don vi ban hang: CONG TY TNHH THUONG MAI MAU\nMa so thue: 0399999991\n"
                "Don vi mua hang: CONG TY CO PHAN MAU\nMa so thue: 0312345678\n"
                "Giay in A4 SL 50 Don gia 65.000 Thanh tien 3.250.000\n"
                "Cong tien hang: 3.250.000\nThue GTGT 10%: 325.000\nTong cong thanh toan: 3.575.000\n",
        "golden": {"voucher_type": "invoice_vat_in", "document_number": "0001234", "document_date": "2026-09-20",
                   "total_amount": 3575000},
    },
    {
        "name": "phiếu thu",
        "text": "PHIEU THU\nSo: PT-0912\nNgay 15/09/2026\nHo ten nguoi nop tien: Nguyen Van Mau\n"
                "Don vi: CONG TY TNHH KHACH HANG MAU, MST 0388888882\nLy do nop: Thanh toan hoa don 0000777\n"
                "So tien: 12.500.000 dong\n",
        "golden": {"voucher_type": "receipt", "document_number": "PT-0912", "document_date": "2026-09-15",
                   "total_amount": 12500000},
    },
    {
        "name": "uỷ nhiệm chi",
        "text": "UY NHIEM CHI\nSo: UNC-0045 Ngay: 02/10/2026\nTen don vi tra tien: CONG TY CO PHAN MAU\n"
                "Ten don vi nhan tien: CONG TY TNHH NHA CUNG CAP MAU, MST 0377777773\n"
                "So tien: 48.000.000 VND\nNoi dung: Thanh toan hop dong HD-2026-12 dot 2\n",
        "golden": {"voucher_type": "bank_transfer", "document_number": "UNC-0045", "document_date": "2026-10-02",
                   "total_amount": 48000000},
    },
    {
        "name": "đề nghị hoàn ứng",
        "text": "GIAY DE NGHI THANH TOAN HOAN UNG\nSo: DN-2026-031 Ngay 28/09/2026\nNguoi de nghi: Tran Thi Mau - NV042\n"
                "Noi dung: Cong tac Da Nang 24-26/09/2026\nVe may bay 2.400.000\nKhach san 1.800.000\n"
                "Tong so tien de nghi: 4.200.000\n",
        "golden": {"voucher_type": "expense_claim", "document_number": "DN-2026-031", "document_date": "2026-09-28",
                   "total_amount": 4200000},
    },
]

OCR_PAGES = [  # (OCR-style input without diacritics, golden corrected text)
    ("Cong ty co phan mau\nDia chi: 12 duong Le Loi, quan 1", "Công ty cổ phần mẫu\nĐịa chỉ: 12 đường Lê Lợi, quận 1"),
    ("Tong cong tien thanh toan: 3.575.000 dong", "Tổng cộng tiền thanh toán: 3.575.000 đồng"),
    ("Nguoi mua hang ky ghi ro ho ten", "Người mua hàng ký ghi rõ họ tên"),
]

_FENCE = re.compile(r"```(?:json)?\s*([\s\S]*?)```")


def _first_json(text: str) -> dict:
    for block in _FENCE.findall(text):
        try:
            value = json.loads(block)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict):
            return value
    return {}


def _same(actual, expected) -> bool:
    """Golden comparison: amounts compare as numbers (\"3.575.000\" == 3575000),
    everything else as trimmed text (document numbers keep leading zeros)."""
    if isinstance(expected, (int, float)):
        if isinstance(actual, str):
            digits = actual.replace(".", "").replace(",", "").replace(" ", "").strip()
            if not digits.isdigit():
                return False
            actual = int(digits)
        return isinstance(actual, (int, float)) and float(actual) == float(expected)
    return isinstance(actual, str) and actual.strip() == str(expected)


def _accuracy(fields: dict, golden: dict) -> float:
    return sum(_same(fields.get(k), v) for k, v in golden.items()) / len(golden)


class Budget:
    def __init__(self, max_usd: float) -> None:
        self.max_usd, self.spent = max_usd, 0.0

    def add(self, cost: float | None) -> None:
        self.spent += cost or 0.0
        if self.spent > self.max_usd:
            raise SystemExit(f"budget exceeded: {self.spent:.4f} USD > {self.max_usd}")


def _usage_row(result, seconds: float) -> dict:
    return {
        "llm_calls": result.llm_call_count, "input": result.input_tokens,
        "cache_write": result.cache_creation_input_tokens, "cache_read": result.cache_read_input_tokens,
        "output": result.output_tokens, "cost_usd": result.estimated_cost_usd, "seconds": round(seconds, 2),
    }


async def run(args) -> dict:
    registry = build_registry(ROOT / "skills")
    budget = Budget(args.max_usd)
    out: dict = {"extraction": [], "chat": [], "ocr": []}

    for doc in DOCUMENTS:
        task = gateway_like_extraction_prompt(doc["text"], legacy=False)
        for mode, lean in (("baseline", False), ("optimized", True)):
            settings = Settings(embedding_enabled=False, extraction_lean_prompt=lean)
            executor = build_agent_executor(extraction_manifest(manifest(), task, lean=lean), settings, registry)
            executor.cli_skill_discovery = not lean
            executor.dynamic_skills = False
            started = time.monotonic()
            result = await executor.execute(prompt=task, token="benchmark")
            row = {"document": doc["name"], "mode": mode, **_usage_row(result, time.monotonic() - started),
                   "accuracy": _accuracy(_first_json(result.final_answer), doc["golden"])}
            out["extraction"].append(row)
            budget.add(result.estimated_cost_usd)
            print(json.dumps(row, ensure_ascii=False))

    for name, prompt in CHAT_SCENARIOS.items():
        for mode, dynamic in (("baseline", False), ("optimized", True)):
            settings = Settings(embedding_enabled=False, dynamic_skill_loading=dynamic)
            # Tool-less: the benchmark must not call the Gateway with a fake token.
            executor = build_agent_executor(manifest(tools=[]), settings, registry)
            started = time.monotonic()
            result = await executor.execute(prompt=prompt, token="benchmark")
            row = {"scenario": name, "mode": mode, **_usage_row(result, time.monotonic() - started),
                   "answer_chars": len(result.final_answer)}
            out["chat"].append(row)
            budget.add(result.estimated_cost_usd)
            print(json.dumps(row, ensure_ascii=False))

    llm = ClaudeCliLlmService(model=args.ocr_model)
    corrector = DiacriticCorrector(llm)
    inputs = [raw for raw, _ in OCR_PAGES]
    golden = [fixed for _, fixed in OCR_PAGES]
    for mode in ("per_page", "batch"):
        started = time.monotonic()
        if mode == "batch":
            texts, usage = await corrector.correct_many_with_usage(inputs)
        else:
            pairs = [await corrector.correct_with_usage(t) for t in inputs]
            texts = [t for t, _ in pairs]
            from aiq_agent.observability.llm_usage import merge_usage

            usage = merge_usage([u for _, u in pairs])
        similarity = sum(difflib.SequenceMatcher(None, a, b).ratio() for a, b in zip(texts, golden)) / len(golden)
        row = {"mode": mode, "llm_calls": usage.llm_call_count if usage else None,
               "input": usage.input_tokens if usage else None,
               "cache_write": usage.cache_creation_input_tokens if usage else None,
               "cache_read": usage.cache_read_input_tokens if usage else None,
               "output": usage.output_tokens if usage else None, "cost_usd": usage.cost_usd if usage else None,
               "seconds": round(time.monotonic() - started, 2), "similarity_to_golden": round(similarity, 4)}
        out["ocr"].append(row)
        budget.add(row["cost_usd"])
        print(json.dumps(row, ensure_ascii=False))
    out["total_cost_usd"] = round(budget.spent, 4)
    return out


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--confirm-paid-llm", action="store_true", help="required: this calls the paid LLM")
    parser.add_argument("--max-usd", type=float, default=3.0)
    parser.add_argument("--ocr-model", default=Settings(embedding_enabled=False).anthropic_model)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not args.confirm_paid_llm:
        print("Refusing to run: this benchmark calls the paid LLM. Pass --confirm-paid-llm.", file=sys.stderr)
        return 2
    result = asyncio.run(run(args))
    if args.out:
        args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
