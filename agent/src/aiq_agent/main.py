"""FastAPI application factory."""

from __future__ import annotations

from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import FastAPI

from aiq_agent.api.ocr_routes import router as ocr_router
from aiq_agent.api.routes import router
from aiq_agent.config import get_settings
from aiq_agent.llm import build_llm_service
from aiq_agent.ocr import (
    DiacriticCorrector,
    OcrPipeline,
    PassthroughValidator,
    PaddleOcrExtractor,
)
from aiq_agent.tools import default_registry


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    settings = get_settings()
    llm = build_llm_service(settings)

    extractor = PaddleOcrExtractor(lang="vi")
    corrector = DiacriticCorrector(llm)
    validator = PassthroughValidator()
    pipeline = OcrPipeline(extractor=extractor, corrector=corrector, validator=validator)

    app.state.llm = llm
    app.state.tools = default_registry
    app.state.ocr_pipeline = pipeline

    try:
        yield
    finally:
        await validator.aclose()
        await llm.aclose()


app = FastAPI(
    title="AIQ Agent",
    version="0.1.0",
    description="Planner-Executor-Critic agent runtime (Qwen3 via Ollama / Claude).",
    lifespan=lifespan,
)

app.include_router(router)
app.include_router(ocr_router)
