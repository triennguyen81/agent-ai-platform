"""OCR / extraction performance benchmark (docs/OCR_PERFORMANCE_AUDIT.md).

Runs the real pipeline code on demo-output/samples — no Gateway, no database.
`--variant before` must run against the DEPLOYED package (the image's
site-packages); `--variant after` against the working tree (PYTHONPATH=src).

Per document and run it records wall time, CPU time (process + child CLI
processes) and peak RSS for: OCR-engine init, text-layer read, render,
OCR recognition, LLM correction, LLM extraction — plus LLM calls / tokens
(from the Claude CLI's own session logs, numbers only), text accuracy
(character error rate vs the PDF's exact text layer) and extraction accuracy
(golden values recalled in the extracted JSON).

`--llm` makes REAL Claude calls (needs CLAUDE_CODE_OAUTH_TOKEN; approved
for this audit). Without it only the non-LLM stages run.

    python scripts/ocr_benchmark.py --variant after --runs 3 --llm --out after.json
"""

from __future__ import annotations

import argparse
import asyncio
import glob
import io
import json
import os
import re
import resource
import sys
import time
import unicodedata
from pathlib import Path
from uuid import uuid4

SAMPLES = Path(os.environ.get("AIQ_SAMPLES", "/samples"))
CORRECTION_MODEL = os.environ.get("AIQ_CORRECTION_MODEL", "claude-sonnet-4-6")  # production anthropic_model
EXTRACTION_MODEL = os.environ.get("AIQ_EXTRACTION_MODEL", "sonnet")  # production manifest model alias

DOCS = {
    "invoice": {"file": "02_Hoa_Don_GTGT.pdf", "type": "invoice_vat_in",
                "golden": ["0001026", "2026-10-05", 33000000, 3000000, 30000000, "0200000001", "0123456789"]},
    "bank_statement": {"file": "03_Sao_Ke_Ngan_Hang.pdf", "type": "bank_statement",
                       "golden": [22000, 45000000, 33000000, 11000, 4350000, 250000000, 257617000]},
    "payment_request": {"file": "04_De_Nghi_Thanh_Toan.pdf", "type": "payment",
                        "golden": ["DNTT-2026-1001", "2026-10-06", 33000000, "0200000001"]},
    "expense_bill": {"file": "07_Hoa_Don_Ve_May_Bay.pdf", "type": "invoice_vat_in",
                     "golden": ["0000456", "2026-10-06", 2600000, 236364, "0200000003"]},
    "contract": {"file": "01_Hop_Dong_Dich_Vu.pdf", "type": None, "golden": []},  # stages only
}
LLM_DOCS = ["invoice", "bank_statement", "payment_request", "expense_bill"]
SCANNED_DOCS = ["invoice", "bank_statement", "payment_request"]


# ------------------------------------------------------------------ metrics
def _rss_mb() -> tuple[float, float]:
    return (resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024,
            resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss / 1024)


def _cpu() -> float:
    s, c = resource.getrusage(resource.RUSAGE_SELF), resource.getrusage(resource.RUSAGE_CHILDREN)
    return s.ru_utime + s.ru_stime + c.ru_utime + c.ru_stime


class Stage:
    def __init__(self, name: str, row: dict) -> None:
        self.name, self.row = name, row

    def __enter__(self):
        self.t, self.c, self.since = time.perf_counter(), _cpu(), time.time()
        return self

    def __exit__(self, *exc):
        self.row[f"{self.name}_s"] = round(time.perf_counter() - self.t, 3)
        self.row[f"{self.name}_cpu_s"] = round(_cpu() - self.c, 3)
        return False


def cli_usage_since(since: float) -> dict:
    """LLM calls / tokens / API time from CLI session logs written since `since`."""
    calls, agg = {}, {"calls": 0, "input": 0, "cache_write": 0, "cache_read": 0, "output": 0, "thinking_blocks": 0}
    for path in glob.glob(os.path.expanduser("~/.claude/projects/*/*.jsonl")):
        if os.path.getmtime(path) < since:
            continue
        for line in open(path, encoding="utf-8", errors="replace"):
            try:
                event = json.loads(line)
            except json.JSONDecodeError:
                continue
            message = event.get("message") or {}
            if event.get("type") == "assistant" and message.get("usage"):
                calls[(path, message.get("id"))] = message
    for message in calls.values():
        usage = message["usage"]
        agg["calls"] += 1
        agg["input"] += usage.get("input_tokens") or 0
        agg["cache_write"] += usage.get("cache_creation_input_tokens") or 0
        agg["cache_read"] += usage.get("cache_read_input_tokens") or 0
        agg["output"] += usage.get("output_tokens") or 0
        agg["thinking_blocks"] += sum(1 for b in message.get("content") or [] if isinstance(b, dict) and b.get("type") == "thinking")
    return agg


def _norm_text(text: str) -> str:
    return re.sub(r"\s+", " ", unicodedata.normalize("NFC", text)).strip()


def cer(hypothesis: str, reference: str) -> float:
    """Character error rate (Levenshtein / reference length) on normalised text."""
    a, b = _norm_text(hypothesis), _norm_text(reference)
    if not b:
        return 0.0 if not a else 1.0
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return round(prev[-1] / len(b), 4)


def word_recall(hypothesis: str, reference: str) -> float:
    """Share of the reference's words (lower-cased, NFC) found in the
    hypothesis, as a multiset — insensitive to reading order, unlike CER."""
    from collections import Counter

    ref = Counter(_norm_text(reference).lower().split())
    hyp = Counter(_norm_text(hypothesis).lower().split())
    total = sum(ref.values())
    return round(sum(min(n, hyp[w]) for w, n in ref.items()) / total, 4) if total else 1.0


def golden_recall(fields_json: str, golden: list) -> float:
    if not golden:
        return 1.0
    flat = re.sub(r"[\s.,]", "", fields_json)
    hits = 0
    for value in golden:
        needle = str(value).replace(".", "").replace(",", "") if isinstance(value, int) else str(value)
        hits += needle in (flat if isinstance(value, int) else fields_json)
    return round(hits / len(golden), 3)


def first_json(text: str) -> tuple[dict, str]:
    for block in re.findall(r"```(?:json)?\s*([\s\S]*?)```", text):
        try:
            value = json.loads(block)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict):
            return value, block
    return {}, ""


# ------------------------------------------------------------------ pipeline
def reference_text(pdf_bytes: bytes) -> str:
    from aiq_agent.ocr.pdf import extract_pdf_text_layer

    return "\n".join(extract_pdf_text_layer(pdf_bytes))


def build_pipeline(variant: str, llm: bool, dpi: int):
    from aiq_agent.ocr import OcrPipeline, PassthroughValidator, RapidOcrExtractor
    from aiq_agent.ocr.corrector import DiacriticCorrector, NoopCorrector

    extractor = RapidOcrExtractor()
    corrector = NoopCorrector()
    if llm:
        from aiq_agent.llm.claude_cli import ClaudeCliLlmService

        service = (ClaudeCliLlmService(model=CORRECTION_MODEL) if variant == "before"
                   else ClaudeCliLlmService(model=CORRECTION_MODEL, max_turns=1, disable_thinking=True))
        corrector = DiacriticCorrector(service)
    kwargs = {"text_layer_first": True} if variant == "after" else {}
    return extractor, OcrPipeline(extractor=extractor, corrector=corrector, validator=PassthroughValidator(), **kwargs)


def extraction_prompt(raw_text: str) -> str:
    """Same structure + skill references as the Gateway manifest prompt."""
    fields = "\n".join(f"- {name} (string)" for name in (
        "voucher_type", "document_number", "document_series", "document_date", "seller_name", "seller_mst",
        "buyer_name", "buyer_mst", "subtotal", "vat_rate", "vat_amount", "total_amount", "transactions"))
    return (
        "Bạn là chuyên viên kế toán VN. Xử lý raw OCR text của 1 chứng từ bên dưới.\n"
        "TỰ XÁC ĐỊNH voucher_type từ nội dung trong: invoice_vat_out, invoice_vat_in, receipt, payment, "
        "bank_transfer, bank_statement, expense_claim, contract.\n\n"
        "**BLOCK 1** — Trích xuất fields theo đúng SCHEMA. Trả về MỘT JSON object PHẲNG. date = YYYY-MM-DD; "
        "số tiền = số. Với sao kê: transactions = mảng {date, description, money_in, money_out, balance}.\n" + fields +
        "\n\n**BLOCK 2** — Áp dụng skill `journal-entry-formatter` + `accounting-vietnam` để đề xuất bút toán N/C theo TT200.\n"
        "Trả về JSON array các entries.\n\n**QUAN TRỌNG**: chỉ trả 2 code block JSON, KHÔNG kèm markdown khác, KHÔNG gọi tool.\n\n"
        "===== RAW OCR TEXT =====\n" + raw_text[:20000] + "\n===== END =====\n"
    )


def live_manifest():
    """Production 'Trợ lý kế toán' shape: 9 image skills, document processing on."""
    from aiq_agent.runtime.manifest import ExecutionManifest

    skills = ["accounting-vietnam", "accounting-vn-style", "compliance-citation", "contract-checklist-builder",
              "contract-extractor", "tax-calculation", "url-ingest", "vietnamese-style", "voucher-invoice-vat"]
    data = {
        "userId": str(uuid4()), "tenantId": str(uuid4()),
        "agent": {"id": str(uuid4()), "slug": "tro-ly-ke-toan", "name": "Trợ lý kế toán", "description": None, "iconMetadata": {}},
        "publishedVersionId": str(uuid4()), "version": 4, "provider": "claude_sdk", "model": EXTRACTION_MODEL,
        "systemPrompt": "Bạn là Trợ lý Kế toán của AIQ. Xác định rõ công ty mình là bên nào; không bịa số liệu.",
        "temperature": 0.2, "maxTurns": 8, "timeoutSeconds": 180, "maxTokens": 2048,
        "memoryConfig": {"mode": "session"}, "tools": [], "skills": skills, "knowledge": [],
        "companyProfile": {"legalName": "Cong Ty TNHH Sáu Trực", "taxCode": "0123456789", "shortName": "Sáu Trực",
                           "aliases": [], "registeredAddress": None, "legalRepresentative": None,
                           "representativeTitle": None, "phone": None, "email": None, "website": None},
    }
    return ExecutionManifest.model_validate(data)


async def run_extraction(variant: str, text: str):
    from aiq_agent.config import Settings
    from aiq_agent.runtime.executors import build_agent_executor
    from aiq_agent.skills.loader import build_registry

    registry = build_registry(Path(os.environ.get("AIQ_SKILLS_DIR", "/app/skills")))
    manifest = live_manifest()
    task = extraction_prompt(text)
    if variant == "before":  # the deployed /agent/document-extract behaviour
        run_manifest = manifest.model_copy(update={"tools": [], "delegates": [], "workflow": None,
                                                   "maxTurns": min(manifest.maxTurns, 2)})
        executor = build_agent_executor(run_manifest, Settings(embedding_enabled=False), registry)
    else:
        from aiq_agent.runtime.executors import extraction_manifest

        executor = build_agent_executor(extraction_manifest(manifest, task, lean=True),
                                        Settings(embedding_enabled=False), registry)
        executor.cli_skill_discovery = False
        executor.dynamic_skills = False
    return await executor.execute(prompt=task, token="benchmark")


async def bench_document(name: str, spec: dict, variant: str, llm: bool, pipeline, scanned: bool, dpi: int) -> dict:
    data = (SAMPLES / spec["file"]).read_bytes()
    reference = reference_text(data)
    row = {"doc": name, "scanned": scanned, "variant": variant, "bytes": len(data)}
    if scanned:  # the same page as a phone/scanner image (JPEG), no text layer
        from aiq_agent.ocr.image import normalize_image_to_png
        from aiq_agent.ocr.pdf import render_pdf_pages

        pages = render_pdf_pages(data, dpi=dpi)
        jpeg = io.BytesIO()
        from PIL import Image

        Image.open(io.BytesIO(pages[0])).convert("RGB").save(jpeg, "JPEG", quality=85)
        row["bytes"] = len(jpeg.getvalue())
        since = time.time()
        with Stage("total", row):
            png = normalize_image_to_png(jpeg.getvalue())
            result = await pipeline.run_pages([png])
    else:
        since = time.time()
        with Stage("total", row):
            result = await pipeline.run_pdf(data, dpi=dpi)
    row["pages"] = result.page_count
    row["page_sources"] = [getattr(p, "source", "ocr") for p in result.pages]
    for stage, ms in result.stage_durations_ms.items():
        row[f"{stage.value}_stage_s"] = round(ms / 1000, 3)
    row["cer_raw"] = cer("\n".join(p.text for p in result.pages), reference)
    row["cer_final"] = cer(result.joined_text("\n"), reference)
    row["word_recall_raw"] = word_recall("\n".join(p.text for p in result.pages), reference)
    row["word_recall_final"] = word_recall(result.joined_text("\n"), reference)
    if llm:
        row["correction_llm"] = cli_usage_since(since)
    text = result.joined_text()
    if llm and spec["type"] and not scanned:
        since = time.time()
        with Stage("extraction", row):
            executed = await run_extraction(variant, text)
        row["extraction_llm"] = cli_usage_since(since)
        fields, raw = first_json(executed.final_answer)
        row["extracted_type"] = fields.get("voucher_type")
        row["type_ok"] = fields.get("voucher_type") == spec["type"]
        row["golden_recall"] = golden_recall(raw, spec["golden"])
    row["rss_self_mb"], row["rss_children_mb"] = _rss_mb()
    return row


async def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--variant", choices=("before", "after"), required=True)
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--llm", action="store_true")
    parser.add_argument("--dpi", type=int, default=200)
    parser.add_argument("--docs", default=",".join(DOCS))
    parser.add_argument("--scanned", action="store_true", help="also run image (scan) versions")
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()

    rows: list[dict] = []
    init = {}
    t, c = time.perf_counter(), _cpu()
    extractor, pipeline = build_pipeline(args.variant, args.llm, args.dpi)
    extractor._ensure_engine()  # cold: model load
    init = {"ocr_engine_init_s": round(time.perf_counter() - t, 3), "ocr_engine_init_cpu_s": round(_cpu() - c, 3),
            "rss_after_init_mb": _rss_mb()[0]}
    print(json.dumps({"init": init}), flush=True)
    names = [n for n in args.docs.split(",") if n]
    for run in range(1, args.runs + 1):
        for name in names:
            spec = DOCS[name]
            llm = args.llm and name in LLM_DOCS
            row = await bench_document(name, spec, args.variant, llm, pipeline, False, args.dpi)
            row["run"], row["start"] = run, "cold" if run == 1 else "warm"
            rows.append(row)
            print(json.dumps(row, ensure_ascii=False), flush=True)
            if args.scanned and name in SCANNED_DOCS:
                row = await bench_document(name, spec, args.variant, llm, pipeline, True, args.dpi)
                row["run"], row["start"] = run, "cold" if run == 1 else "warm"
                rows.append(row)
                print(json.dumps(row, ensure_ascii=False), flush=True)
    if args.out:
        args.out.write_text(json.dumps({"variant": args.variant, "dpi": args.dpi, "init": init, "rows": rows},
                                       ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
