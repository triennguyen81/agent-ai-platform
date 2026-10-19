"""FastAPI application factory."""

from __future__ import annotations

import os
import asyncio
import logging
from contextlib import suppress
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI

from aiq_agent.api.document_routes import router as document_router
from aiq_agent.api.ocr_routes import router as ocr_router
from aiq_agent.api.routes import router
from aiq_agent.api.knowledge_routes import router as knowledge_router
from aiq_agent.audit import build_audit_store
from aiq_agent.config import get_settings
from aiq_agent.embeddings import build_embedding_service
from aiq_agent.llm import build_llm_service
from aiq_agent.llm.claude_cli import ClaudeCliLlmService
from aiq_agent.memory import build_memory_store
from aiq_agent.observability import RecordedLlmService, configure_tracing, instrument_fastapi
from aiq_agent.ocr import (
    DiacriticCorrector,
    OcrPipeline,
    PassthroughValidator,
    PaddleOcrExtractor,
    RapidOcrExtractor,
)
from aiq_agent.ocr.concurrency import get_ocr_gate
from aiq_agent.skills import build_registry as build_skill_registry
from aiq_agent.tools import default_registry


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    settings = get_settings()
    configure_tracing(settings)
    llm = RecordedLlmService(build_llm_service(settings))
    memory = build_memory_store(settings)

    # OCR backend selection — env AIQ_OCR_BACKEND overrides the default.
    #   rapid   → RapidOCR (ONNX runtime — recommended, no SIGSEGV)
    #   paddle  → PaddleOCR (legacy — segfaults on some VN images)
    # Defaults to rapid because it survived the demo-eve stability test.
    ocr_backend = os.environ.get("AIQ_OCR_BACKEND", "rapid").lower()
    if ocr_backend == "paddle":
        extractor = PaddleOcrExtractor(lang="vi")
    else:
        extractor = RapidOcrExtractor()
    # OCR correction gets its own LLM client so it can run without extended
    # thinking (chat keeps the provider default).
    correction_llm = llm
    if settings.llm_provider == "claude_cli" and settings.ocr_correction_disable_thinking:
        correction_llm = RecordedLlmService(ClaudeCliLlmService(
            model=settings.anthropic_model, max_turns=1, disable_thinking=True))
    corrector = DiacriticCorrector(correction_llm)
    validator = PassthroughValidator()
    pipeline = OcrPipeline(extractor=extractor, corrector=corrector, validator=validator,
                           correction_batch_chars=settings.ocr_correction_batch_chars,
                           text_layer_first=settings.ocr_pdf_text_layer_first)
    # Native OCR admission (AG-02) — built at startup so a bad OCR_* setting
    # fails the boot instead of the first upload.
    ocr_gate = get_ocr_gate()

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
    # keeps container startup fast. KB retrieval fails closed if the index is unavailable.
    embeddings = None
    if settings.embedding_enabled:
        embeddings = build_embedding_service(
            model_name=settings.embedding_model,
            embedding_dim=settings.embedding_dim,
            qdrant_url=settings.qdrant_url,
            qdrant_api_key=settings.qdrant_api_key,
            collection=settings.qdrant_collection,
        )

    app.state.llm = llm
    app.state.tools = default_registry
    app.state.memory = memory
    app.state.ocr_pipeline = pipeline
    app.state.ocr_extractor = extractor
    app.state.skills = skill_registry
    app.state.audit = audit_store
    app.state.embeddings = embeddings

    from aiq_agent.knowledge.service import KnowledgeService, set_service
    kb_pool = None
    kb_worker = None
    kb_dsn = settings.knowledge_postgres_dsn or settings.memory_postgres_dsn
    if kb_dsn and embeddings:
        import asyncpg
        kb_pool = await asyncpg.create_pool(kb_dsn, min_size=1, max_size=5)
        knowledge = KnowledgeService(kb_pool, embeddings, settings)
        set_service(knowledge)
        kb_worker = asyncio.create_task(knowledge.run())
    try:
        yield
    finally:
        set_service(None)
        if kb_worker:
            kb_worker.cancel()
            with suppress(asyncio.CancelledError):
                await kb_worker
        if kb_pool:
            await kb_pool.close()
        ocr_gate.shutdown()
        await memory.aclose()
        await validator.aclose()
        await llm.aclose()


def _configure_app_logging() -> None:
    """Emit aiq_agent.* logs (runtime provenance: agent/version/skill
    sources/workflow) at the configured LOG_LEVEL. Without a handler only
    WARNING+ reached stderr via Python's last-resort handler. Idempotent."""
    app_logger = logging.getLogger("aiq_agent")
    if any(getattr(h, "_aiq_handler", False) for h in app_logger.handlers):
        return
    handler = logging.StreamHandler()
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s %(message)s"))
    handler._aiq_handler = True  # type: ignore[attr-defined]
    app_logger.addHandler(handler)
    app_logger.setLevel(get_settings().log_level.upper())
    app_logger.propagate = False


_configure_app_logging()

app = FastAPI(
    title="AIQ Agent",
    version="0.1.0",
    description="Planner-Executor-Critic agent runtime (Qwen3 via Ollama / Claude).",
    lifespan=lifespan,
)

app.include_router(router)
app.include_router(ocr_router)
app.include_router(knowledge_router)
app.include_router(document_router)


# ------------------------------------------------------------------
# Cheap liveness probe — used by docker-compose healthcheck to detect
# a hung uvicorn worker (e.g. after PaddleOCR SIGSEGV corrupted the
# process). Returns instantly without touching Claude CLI or PaddleOCR
# so slow LLM calls never affect health.
# ------------------------------------------------------------------
@app.get("/healthz/live", include_in_schema=False)
async def healthz_live() -> dict[str, str]:
    return {"status": "ok"}

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
