"""OCR extractor contract + PaddleOCR adapter + deterministic stub for tests."""

from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import Sequence
from dataclasses import dataclass

from aiq_agent.ocr.concurrency import run_ocr_native


@dataclass(frozen=True, slots=True)
class OcrPage:
    page_number: int
    text: str
    confidence: float
    # "ocr" (image recognised by the OCR engine) or "text_layer" (the PDF's
    # own embedded text, used verbatim — exact, so confidence 1.0).
    source: str = "ocr"


class OcrExtractor(ABC):
    """Extract text from image bytes (one entry per page)."""

    @abstractmethod
    async def extract(self, pages: Sequence[bytes]) -> list[OcrPage]:
        ...


class StubOcrExtractor(OcrExtractor):
    """Deterministic extractor for unit tests and smoke runs without paddleocr."""

    def __init__(self, texts: Sequence[str], confidence: float = 0.95) -> None:
        self._texts = list(texts)
        self._confidence = confidence

    async def extract(self, pages: Sequence[bytes]) -> list[OcrPage]:
        return [
            OcrPage(
                page_number=index + 1,
                text=self._texts[index] if index < len(self._texts) else "",
                confidence=self._confidence,
            )
            for index in range(len(pages))
        ]


class PaddleOcrExtractor(OcrExtractor):
    """PaddleOCR 2.9.x adapter (see agent/Dockerfile for the pin rationale).

    Install the optional dependency group first:
        pip install '.[ocr]'
    """

    def __init__(self, *, lang: str = "vi", use_gpu: bool = False) -> None:
        self._lang = lang
        self._use_gpu = use_gpu
        self._engine = None

    def _ensure_engine(self) -> object:
        if self._engine is not None:
            return self._engine
        try:
            from paddleocr import PaddleOCR  # type: ignore[import-not-found]
        except ImportError as exc:
            raise RuntimeError(
                "paddleocr is not installed. Install the ocr extras: "
                "`pip install '.[ocr]'`"
            ) from exc

        # PaddleOCR 2.9.x API — stable + working CPU path with oneDNN.
        # (3.x drops these args and its CPU runtime throws
        # NotImplementedError under mkldnn — see Dockerfile comment.)
        self._engine = PaddleOCR(use_angle_cls=True, lang=self._lang, use_gpu=self._use_gpu)
        return self._engine

    async def extract(self, pages: Sequence[bytes]) -> list[OcrPage]:
        engine = self._ensure_engine()
        return await run_ocr_native(_run_paddle, engine, list(pages))


def _run_paddle(engine: object, pages: list[bytes]) -> list[OcrPage]:
    import io

    from PIL import Image  # type: ignore[import-not-found]

    results: list[OcrPage] = []
    for index, png_bytes in enumerate(pages):
        image = Image.open(io.BytesIO(png_bytes)).convert("RGB")
        raw = engine.ocr(_pil_to_array(image), cls=True)  # type: ignore[attr-defined]

        lines: list[str] = []
        confidences: list[float] = []
        for page_result in raw or []:
            for line in page_result or []:
                if not line or len(line) < 2:
                    continue
                text, score = line[1][0], float(line[1][1])
                lines.append(text)
                confidences.append(score)

        avg_conf = sum(confidences) / len(confidences) if confidences else 0.0
        results.append(
            OcrPage(
                page_number=index + 1,
                text="\n".join(lines),
                confidence=avg_conf,
            )
        )
    return results


def _pil_to_array(image: object) -> object:
    import numpy as np  # type: ignore[import-not-found]

    return np.array(image)


class RapidOcrExtractor(OcrExtractor):
    """rapidocr-onnxruntime adapter — ONNX runtime avoids PaddlePaddle segfaults.

    Same PP-OCRv4 models as PaddleOCR, packaged as ONNX so we don't have
    to depend on the paddle framework (which SIGSEGVs on some VN invoice
    images and takes the whole uvicorn process down).
    """

    def __init__(self) -> None:
        self._engine = None

    def _ensure_engine(self) -> object:
        if self._engine is not None:
            return self._engine
        try:
            from rapidocr_onnxruntime import RapidOCR  # type: ignore[import-not-found]
        except ImportError as exc:
            raise RuntimeError(
                "rapidocr-onnxruntime is not installed. Add to Dockerfile."
            ) from exc
        # Default constructor — pulls the PP-OCRv4 models on first use +
        # caches them under ~/.cache/rapidocr. Set det/rec/cls thresholds
        # to defaults; VN diacritic correction happens downstream in
        # DiacriticCorrector so we don't tune rec_score_thresh here.
        self._engine = RapidOCR()
        return self._engine

    async def extract(self, pages: Sequence[bytes]) -> list[OcrPage]:
        engine = self._ensure_engine()
        return await run_ocr_native(_run_rapid, engine, list(pages))


def _run_rapid(engine: object, pages: list[bytes]) -> list[OcrPage]:
    """RapidOCR call — takes image bytes/np.array directly, returns
    [(bbox, text, confidence), ...]."""
    import io

    import numpy as np  # type: ignore[import-not-found]
    from PIL import Image  # type: ignore[import-not-found]

    results: list[OcrPage] = []
    for index, png_bytes in enumerate(pages):
        image = Image.open(io.BytesIO(png_bytes)).convert("RGB")
        arr = np.array(image)
        # RapidOCR returns tuple: (results, elapsed_dict). results is
        # None if nothing detected, otherwise list of [box, text, score].
        raw, _elapsed = engine(arr)  # type: ignore[operator]
        lines: list[str] = []
        confidences: list[float] = []
        for row in raw or []:
            if not row or len(row) < 3:
                continue
            _bbox, text, score = row[0], str(row[1]), float(row[2])
            lines.append(text)
            confidences.append(score)
        avg_conf = sum(confidences) / len(confidences) if confidences else 0.0
        results.append(
            OcrPage(
                page_number=index + 1,
                text="\n".join(lines),
                confidence=avg_conf,
            )
        )
    return results
