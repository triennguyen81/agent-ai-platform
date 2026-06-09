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
from enum import Enum

from aiq_agent.ocr.corrector import TextCorrector
from aiq_agent.ocr.extractor import OcrExtractor, OcrPage
from aiq_agent.ocr.validator import OcrValidator, ValidationReport


class PipelineStage(str, Enum):
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
    ) -> None:
        self._extractor = extractor
        self._corrector = corrector
        self._validator = validator

    async def run_pdf(self, pdf_bytes: bytes, *, dpi: int = 200) -> PipelineResult:
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
        )

    async def run_pages(self, page_images: Sequence[bytes]) -> PipelineResult:
        durations: dict[PipelineStage, int] = {}

        start = time.perf_counter()
        pages = await self._extractor.extract(page_images)
        durations[PipelineStage.EXTRACT] = _elapsed_ms(start)

        start = time.perf_counter()
        corrected: list[str] = []
        for page in pages:
            corrected.append(await self._corrector.correct(page.text))
        durations[PipelineStage.CORRECT] = _elapsed_ms(start)

        start = time.perf_counter()
        validation = await self._validator.validate(corrected)
        durations[PipelineStage.VALIDATE] = _elapsed_ms(start)

        return PipelineResult(
            pages=pages,
            corrected_text_by_page=corrected,
            validation=validation,
            stage_durations_ms=durations,
        )


def _elapsed_ms(start: float) -> int:
    return int((time.perf_counter() - start) * 1000)
