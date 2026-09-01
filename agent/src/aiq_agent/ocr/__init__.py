"""OCR pipeline: PaddleOCR extraction → Qwen3 correction → pgvector validation."""

from aiq_agent.ocr.corrector import DiacriticCorrector, NoopCorrector
from aiq_agent.ocr.extractor import (
    OcrExtractor,
    OcrPage,
    PaddleOcrExtractor,
    RapidOcrExtractor,
    StubOcrExtractor,
)
from aiq_agent.ocr.pipeline import (
    OcrPipeline,
    PipelineResult,
    PipelineStage,
)
from aiq_agent.ocr.validator import OcrValidator, PassthroughValidator, ValidationReport

__all__ = [
    "DiacriticCorrector",
    "NoopCorrector",
    "OcrExtractor",
    "OcrPage",
    "OcrPipeline",
    "OcrValidator",
    "PaddleOcrExtractor",
    "PassthroughValidator",
    "RapidOcrExtractor",
    "PipelineResult",
    "PipelineStage",
    "StubOcrExtractor",
    "ValidationReport",
]
