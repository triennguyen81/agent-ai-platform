"""End-to-end OCR pipeline test using stub extractor + fake LLM corrector."""

from __future__ import annotations

import pytest

from aiq_agent.ocr import (
    DiacriticCorrector,
    OcrPipeline,
    PassthroughValidator,
    PipelineStage,
    StubOcrExtractor,
)

from .fake_llm import FakeLlmService


@pytest.mark.asyncio
async def test_pipeline_runs_three_stages_and_returns_corrected_text() -> None:
    raw_pages = [
        "Cha cong cha nhu nui Thai Son",
        "Nghia me nhu nuoc trong nguon chay ra",
    ]
    corrected_pages = [
        "Công cha như núi Thái Sơn",
        "Nghĩa mẹ như nước trong nguồn chảy ra",
    ]

    extractor = StubOcrExtractor(texts=raw_pages, confidence=0.9)
    corrector = DiacriticCorrector(FakeLlmService(replies=corrected_pages))
    validator = PassthroughValidator()
    pipeline = OcrPipeline(extractor=extractor, corrector=corrector, validator=validator)

    # Two opaque "PNG" payloads — content does not matter for the stub extractor.
    result = await pipeline.run_pages([b"page-1", b"page-2"])

    assert result.page_count == 2
    assert [p.text for p in result.pages] == raw_pages
    assert result.corrected_text_by_page == corrected_pages
    assert result.average_confidence == pytest.approx(0.9)
    assert {PipelineStage.EXTRACT, PipelineStage.CORRECT, PipelineStage.VALIDATE} <= set(
        result.stage_durations_ms.keys()
    )
    assert all(report.notes == "passthrough" for report in result.validation)
    assert result.joined_text() == "\n\n".join(corrected_pages)


@pytest.mark.asyncio
async def test_pipeline_skips_correction_for_empty_pages() -> None:
    extractor = StubOcrExtractor(texts=["   ", "Xin chao the gioi"])
    fake_llm = FakeLlmService(replies=["Xin chào thế giới"])  # only one call expected
    corrector = DiacriticCorrector(fake_llm)
    pipeline = OcrPipeline(
        extractor=extractor,
        corrector=corrector,
        validator=PassthroughValidator(),
    )

    result = await pipeline.run_pages([b"page-1", b"page-2"])

    assert result.corrected_text_by_page[0].strip() == ""
    assert result.corrected_text_by_page[1] == "Xin chào thế giới"
    # Empty page must NOT consume an LLM call — token budget protection.
    assert len(fake_llm.calls) == 1


@pytest.mark.asyncio
async def test_pipeline_records_stage_durations() -> None:
    pipeline = OcrPipeline(
        extractor=StubOcrExtractor(texts=["abc"]),
        corrector=DiacriticCorrector(FakeLlmService(replies=["ABC"])),
        validator=PassthroughValidator(),
    )

    result = await pipeline.run_pages([b"page-1"])

    for stage in (PipelineStage.EXTRACT, PipelineStage.CORRECT, PipelineStage.VALIDATE):
        assert stage in result.stage_durations_ms
        assert result.stage_durations_ms[stage] >= 0
