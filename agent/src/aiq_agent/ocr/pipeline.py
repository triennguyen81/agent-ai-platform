"""End-to-end OCR pipeline orchestrator.

Stages:
    1. PDF → PNG pages (pypdfium2)
    2. Extraction (PaddleOCR or stub) → raw text per page
    3. Correction (LLM-backed) → corrected text per page
    4. Validation (pgvector or passthrough) → similarity reports

The orchestrator is decoupled from PDF rendering so callers that already have
PNG bytes (e.g. unit tests, retry flows) can call `run_pages` directly.
"""

from __future__ import annotations

import time
from collections.abc import Sequence
from dataclasses import dataclass, field
from enum import StrEnum

from aiq_agent.llm.base import LlmUsage
from aiq_agent.observability.llm_usage import merge_usage
from aiq_agent.ocr.corrector import TextCorrector
from aiq_agent.ocr.extractor import OcrExtractor, OcrPage
from aiq_agent.ocr.validator import OcrValidator, ValidationReport


class PipelineStage(StrEnum):
    TEXT_LAYER = "text_layer"
    RENDER = "render"
    EXTRACT = "extract"
    CORRECT = "correct"
    VALIDATE = "validate"


@dataclass(frozen=True, slots=True)
class PipelineResult:
    pages: list[OcrPage]
    corrected_text_by_page: list[str]
    validation: list[ValidationReport]
    stage_durations_ms: dict[PipelineStage, int] = field(default_factory=dict)
    # LLM usage of the correction stage (None when no LLM call was made).
    llm_usage: LlmUsage | None = None

    @property
    def page_count(self) -> int:
        return len(self.pages)

    @property
    def average_confidence(self) -> float:
        if not self.pages:
            return 0.0
        return sum(p.confidence for p in self.pages) / len(self.pages)

    def joined_text(self, separator: str = "\n\n") -> str:
        return separator.join(self.corrected_text_by_page)


class OcrPipeline:
    def __init__(
        self,
        *,
        extractor: OcrExtractor,
        corrector: TextCorrector,
        validator: OcrValidator,
        correction_batch_chars: int = 0,
        text_layer_first: bool = False,
    ) -> None:
        self._extractor = extractor
        self._corrector = corrector
        self._validator = validator
        # 0 = one correction call per page (default). >0 = consecutive pages
        # are corrected together while their text fits this many characters.
        self._correction_batch_chars = correction_batch_chars
        # PDFs: use a page's own text layer when it is usable and the page is
        # not a scan; only the other pages are rendered, OCR'd and corrected.
        self._text_layer_first = text_layer_first

    async def run_pdf(self, pdf_bytes: bytes, *, dpi: int = 200) -> PipelineResult:
        if self._text_layer_first:
            return await self._run_pdf_text_layer_first(pdf_bytes, dpi=dpi)

        from aiq_agent.ocr.pdf import render_pdf_pages_async

        start = time.perf_counter()
        pages = await render_pdf_pages_async(pdf_bytes, dpi=dpi)
        render_ms = _elapsed_ms(start)

        result = await self.run_pages(pages)
        return PipelineResult(
            pages=result.pages,
            corrected_text_by_page=result.corrected_text_by_page,
            validation=result.validation,
            stage_durations_ms={PipelineStage.RENDER: render_ms, **result.stage_durations_ms},
            llm_usage=result.llm_usage,
        )

    async def _run_pdf_text_layer_first(self, pdf_bytes: bytes, *, dpi: int) -> PipelineResult:
        from aiq_agent.ocr.pdf import analyse_pdf_pages_async

        start = time.perf_counter()
        analysed = await analyse_pdf_pages_async(pdf_bytes, dpi=dpi)
        analyse_ms = _elapsed_ms(start)
        scanned = [page["png"] for page in analysed if not page["use_text_layer"]]
        ocr_result = await self.run_pages(scanned) if scanned else None

        pages: list[OcrPage] = []
        corrected: list[str] = []
        ocr_iter = (
            iter(zip(ocr_result.pages, ocr_result.corrected_text_by_page, strict=True))
            if ocr_result
            else iter(())
        )
        for number, page in enumerate(analysed, 1):
            if page["use_text_layer"]:
                text = page["text"].replace("\r\n", "\n").strip()
                pages.append(
                    OcrPage(page_number=number, text=text, confidence=1.0, source="text_layer")
                )
                corrected.append(text)
            else:
                ocr_page, fixed = next(ocr_iter)
                pages.append(
                    OcrPage(page_number=number, text=ocr_page.text, confidence=ocr_page.confidence)
                )
                corrected.append(fixed)
        # Validate the assembled document (page numbers of the OCR subset are
        # not the document's page numbers in a mixed PDF).
        validation = await self._validator.validate(corrected)
        durations = {PipelineStage.TEXT_LAYER: analyse_ms}
        if ocr_result:
            durations.update(ocr_result.stage_durations_ms)
        return PipelineResult(
            pages=pages,
            corrected_text_by_page=corrected,
            validation=validation,
            stage_durations_ms=durations,
            llm_usage=ocr_result.llm_usage if ocr_result else None,
        )

    async def run_pages(self, page_images: Sequence[bytes]) -> PipelineResult:
        durations: dict[PipelineStage, int] = {}

        start = time.perf_counter()
        pages = await self._extractor.extract(page_images)
        durations[PipelineStage.EXTRACT] = _elapsed_ms(start)

        start = time.perf_counter()
        corrected: list[str] = []
        usages: list[LlmUsage | None] = []
        if self._correction_batch_chars > 0:
            for batch in _batches([p.text for p in pages], self._correction_batch_chars):
                texts, usage = await self._corrector.correct_many_with_usage(batch)
                corrected.extend(texts)
                usages.append(usage)
        else:
            for page in pages:
                text, usage = await self._corrector.correct_with_usage(page.text)
                corrected.append(text)
                usages.append(usage)
        durations[PipelineStage.CORRECT] = _elapsed_ms(start)

        start = time.perf_counter()
        validation = await self._validator.validate(corrected)
        durations[PipelineStage.VALIDATE] = _elapsed_ms(start)

        return PipelineResult(
            pages=pages,
            corrected_text_by_page=corrected,
            validation=validation,
            stage_durations_ms=durations,
            llm_usage=merge_usage(usages),
        )


def _batches(texts: list[str], limit: int) -> list[list[str]]:
    """Consecutive groups whose total length stays within ``limit``; a single
    page longer than the limit forms its own group."""
    groups: list[list[str]] = []
    current: list[str] = []
    size = 0
    for text in texts:
        if current and size + len(text) > limit:
            groups.append(current)
            current, size = [], 0
        current.append(text)
        size += len(text)
    if current:
        groups.append(current)
    return groups


def _elapsed_ms(start: float) -> int:
    return int((time.perf_counter() - start) * 1000)
