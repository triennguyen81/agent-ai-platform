"""FastAPI application factory."""

from __future__ import annotations

import os
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI

from aiq_agent.api.ocr_routes import router as ocr_router
from aiq_agent.api.routes import router
from aiq_agent.audit import build_audit_store
from aiq_agent.config import get_settings
from aiq_agent.embeddings import build_embedding_service
from aiq_agent.llm import build_llm_service
from aiq_agent.memory import build_memory_store
from aiq_agent.observability import RecordedLlmService, configure_tracing, instrument_fastapi
from aiq_agent.ocr import (
    DiacriticCorrector,
    OcrPipeline,
    PassthroughValidator,
    PaddleOcrExtractor,
)
from aiq_agent.skills import build_registry as build_skill_registry
from aiq_agent.tools import default_registry


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    settings = get_settings()
    configure_tracing(settings)
    llm = RecordedLlmService(build_llm_service(settings))
    memory = build_memory_store(settings)

    extractor = PaddleOcrExtractor(lang="vi")
    corrector = DiacriticCorrector(llm)
    validator = PassthroughValidator()
    pipeline = OcrPipeline(extractor=extractor, corrector=corrector, validator=validator)

    # SKILL.md packages live at:
    # - Dev:    <repo>/agent/skills/<skill>/SKILL.md
    # - Docker: /app/skills/<skill>/SKILL.md (Dockerfile copies it there)
    # Env var override AIQ_SKILLS_DIR wins if set.
    skills_root_env = os.environ.get("AIQ_SKILLS_DIR")
    if skills_root_env:
        skills_root = Path(skills_root_env)
    else:
        repo_candidate = Path(__file__).resolve().parent.parent.parent / "skills"
        container_candidate = Path("/app/skills")
        skills_root = repo_candidate if repo_candidate.exists() else container_candidate
    skill_registry = build_skill_registry(skills_root)

    # Audit store reuses the memory store's underlying Redis client when
    # the redis backend is selected; otherwise audit is disabled (None).
    audit_store = None
    if settings.memory_backend == "redis" and hasattr(memory, "_redis"):
        audit_store = build_audit_store(redis=memory._redis)  # noqa: SLF001 — intentional

    # Embedding service is lazy: fastembed model is downloaded the first
    # time anyone calls .sync_notes() or .search(), not at startup. That
    # keeps container startup fast and lets the system fall back to
    # full-context RAG silently if fastembed init fails later.
    embeddings = None
    if settings.embedding_enabled:
        embeddings = build_embedding_service(
            model_name=settings.embedding_model,
            embedding_dim=settings.embedding_dim,
            qdrant_url=settings.qdrant_url,
            collection=settings.qdrant_collection,
        )

    app.state.llm = llm
    app.state.tools = default_registry
    app.state.memory = memory
    app.state.ocr_pipeline = pipeline
    app.state.skills = skill_registry
    app.state.audit = audit_store
    app.state.embeddings = embeddings

    try:
        yield
    finally:
        await memory.aclose()
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

instrument_fastapi(app)


def _install_prometheus() -> None:
    """Expose /metrics with request latency, count, in-progress histograms.

    TEMPORARILY DISABLED: prometheus-fastapi-instrumentator 8.0.0 + FastAPI
    0.137 hit `_IncludedRouter has no attribute path` because newer FastAPI
    wraps included routers. Crashes every request middleware. Re-enable
    after pinning the dep (see pyproject.toml) or after upstream fix.
    """
    from prometheus_fastapi_instrumentator import Instrumentator

    Instrumentator(
        excluded_handlers=["/healthz/live", "/healthz/ready", "/metrics"],
    ).instrument(app).expose(app, endpoint="/metrics", include_in_schema=False)


# _install_prometheus()  # noqa: ERA001 — see docstring above
