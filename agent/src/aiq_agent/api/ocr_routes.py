"""OCR pipeline HTTP endpoints."""

from __future__ import annotations

from fastapi import APIRouter, Depends, File, HTTPException, Request, UploadFile, status
from pydantic import BaseModel, Field

from aiq_agent.llm.base import LlmUsage
from aiq_agent.ocr import OcrPipeline, PipelineResult, PipelineStage
from aiq_agent.ocr.concurrency import OcrBusyError, OcrTimeoutError

router = APIRouter(prefix="/ocr", tags=["ocr"])


class PageReport(BaseModel):
    page_number: int
    raw_text: str
    corrected_text: str
    confidence: float
    similarity: float | None = None
    notes: str | None = None
    # "ocr" or "text_layer" (the PDF's own embedded text, used verbatim).
    source: str = "ocr"


class LlmUsageReport(BaseModel):
    """Numbers only — never prompt or document content."""

    llm_call_count: int
    input_tokens: int
    output_tokens: int
    cache_creation_input_tokens: int
    cache_read_input_tokens: int
    cost_usd: float | None = None
    provider: str | None = None
    model: str | None = None

    @classmethod
    def from_usage(
        cls, usage: LlmUsage | None, *, provider: str | None, model: str | None
    ) -> LlmUsageReport | None:
        if usage is None:
            return None
        return cls(
            llm_call_count=usage.llm_call_count,
            input_tokens=usage.input_tokens,
            output_tokens=usage.output_tokens,
            cache_creation_input_tokens=usage.cache_creation_input_tokens,
            cache_read_input_tokens=usage.cache_read_input_tokens,
            cost_usd=usage.cost_usd,
            provider=provider,
            model=model,
        )


class PipelineResponse(BaseModel):
    page_count: int = Field(ge=0)
    average_confidence: float
    stage_durations_ms: dict[str, int]
    pages: list[PageReport]
    joined_text: str
    # OCR-correction LLM usage, for the Gateway to record against the
    # uploading tenant (the Agent has no tenant context on this route).
    llm_usage: LlmUsageReport | None = None


def _pipeline(request: Request) -> OcrPipeline:
    pipeline = getattr(request.app.state, "ocr_pipeline", None)
    if pipeline is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="OCR pipeline not configured.",
        )
    return pipeline


def _capacity_error(exc: OcrBusyError | OcrTimeoutError) -> HTTPException:
    headers = {"Retry-After": "10"} if isinstance(exc, OcrBusyError) else None
    return HTTPException(status_code=exc.status_code, detail=str(exc), headers=headers)


def _corrector_model(request: Request) -> tuple[str | None, str | None]:
    llm = getattr(request.app.state, "llm", None)
    return getattr(llm, "provider", None), getattr(llm, "model", None)


def _to_response(result: PipelineResult, request: Request | None = None) -> PipelineResponse:
    provider, model = _corrector_model(request) if request is not None else (None, None)
    validation_by_page = {v.page_number: v for v in result.validation}
    pages: list[PageReport] = []
    for page, corrected in zip(result.pages, result.corrected_text_by_page, strict=False):
        report = validation_by_page.get(page.page_number)
        pages.append(
            PageReport(
                page_number=page.page_number,
                raw_text=page.text,
                corrected_text=corrected,
                confidence=page.confidence,
                similarity=report.similarity if report else None,
                notes=report.notes if report else None,
                source=page.source,
            )
        )

    return PipelineResponse(
        page_count=result.page_count,
        average_confidence=result.average_confidence,
        stage_durations_ms={stage.value: ms for stage, ms in result.stage_durations_ms.items()},
        pages=pages,
        joined_text=result.joined_text(),
        llm_usage=LlmUsageReport.from_usage(result.llm_usage, provider=provider, model=model),
    )


_IMAGE_SUFFIXES = (".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff", ".heic")


@router.post("/pdf", response_model=PipelineResponse)
async def run_pdf(
    request: Request,
    file: UploadFile = File(...),
    pipeline: OcrPipeline = Depends(_pipeline),
) -> PipelineResponse:
    if not file.filename or not file.filename.lower().endswith(".pdf"):
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Only .pdf uploads are accepted.",
        )

    content = await file.read()
    if not content:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Empty PDF upload.",
        )

    try:
        result = await pipeline.run_pdf(content)
    except (OcrBusyError, OcrTimeoutError) as exc:
        raise _capacity_error(exc) from exc
    except RuntimeError as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail=str(exc),
        ) from exc

    return _to_response(result, request)


@router.post("/image", response_model=PipelineResponse)
async def run_image(
    request: Request,
    file: UploadFile = File(...),
    pipeline: OcrPipeline = Depends(_pipeline),
) -> PipelineResponse:
    """Accept a single image (JPG/PNG/WebP/HEIC/BMP/TIFF) → OCR as 1 page.

    Voucher photos come from phone cameras or scanners as standalone images.
    The endpoint normalizes them to PNG (with EXIF rotation applied) and feeds
    the pipeline's page-based run_pages() with a single-element list.
    """

    filename = (file.filename or "").lower()
    if not filename.endswith(_IMAGE_SUFFIXES):
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Only image uploads accepted: {', '.join(_IMAGE_SUFFIXES)}",
        )

    content = await file.read()
    if not content:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Empty image upload.",
        )

    from aiq_agent.ocr.image import normalize_image_to_png_async

    try:
        png_bytes = await normalize_image_to_png_async(content)
    except (OcrBusyError, OcrTimeoutError) as exc:
        raise _capacity_error(exc) from exc
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=str(exc),
        ) from exc
    except RuntimeError as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail=str(exc),
        ) from exc

    try:
        result = await pipeline.run_pages([png_bytes])
    except (OcrBusyError, OcrTimeoutError) as exc:
        raise _capacity_error(exc) from exc
    except RuntimeError as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail=str(exc),
        ) from exc

    return _to_response(result, request)


__all__ = ["router", "LlmUsageReport", "PipelineResponse", "PageReport", "PipelineStage"]
