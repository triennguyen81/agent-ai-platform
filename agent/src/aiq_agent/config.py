"""Runtime configuration loaded from environment variables."""

from functools import lru_cache
from typing import Literal

from pydantic import Field, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

LlmProvider = Literal["ollama", "anthropic", "claude_cli"]
MemoryBackend = Literal["in_memory", "redis", "postgres"]


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
        env_nested_delimiter="__",
    )

    # ---- General ----
    environment: str = Field(default="development")
    log_level: str = Field(default="INFO")

    # ---- LLM routing ----
    llm_provider: LlmProvider = Field(default="ollama")

    # ---- Ollama ----
    ollama_base_url: str = Field(default="http://host.docker.internal:11434")
    ollama_model: str = Field(default="qwen3:7b")
    ollama_timeout_seconds: float = Field(default=120.0, gt=0)

    # ---- Anthropic ----
    anthropic_api_key: str | None = Field(default=None)
    anthropic_model: str = Field(default="claude-sonnet-4-6")
    anthropic_max_tokens: int = Field(default=2048, gt=0)

    # ---- Claude CLI (subscription auth via `claude login`) ----
    # max_turns caps how many assistant/tool round-trips a single /chat
    # call can spend before returning. Keep it small — /chat is meant to
    # be single-turn; multi-turn agentic work goes through /agent/run.
    claude_cli_max_turns: int = Field(default=4, gt=0, le=20)

    # ---- Gateway (for agentic tools that proxy through Gateway) ----
    gateway_base_url: str = Field(default="http://aiq-gateway:8080")

    # ---- M6: Embeddings + Qdrant top-K RAG ----
    # Self-hosted via fastembed (ONNX). Versioned retrieval fails closed on index errors.
    # paraphrase-multilingual-MiniLM-L12-v2: 384 dim, ~220MB, supports
    # Vietnamese + 50 languages. Smallest fastembed multilingual model.
    embedding_model: str = Field(
        default="sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2"
    )
    embedding_dim: int = Field(default=384, gt=0)
    embedding_enabled: bool = Field(default=True)
    qdrant_url: str = Field(default="http://aiq-qdrant:6333")
    qdrant_api_key: str | None = Field(default=None)
    qdrant_collection: str = Field(default="aiq_notes")
    qdrant_top_k: int = Field(default=10, gt=0, le=100)

    knowledge_postgres_dsn: str | None = None
    knowledge_chunk_size: int = Field(default=800, ge=64, le=8000)
    knowledge_chunk_overlap: int = Field(default=120, ge=0)
    knowledge_score_threshold: float = Field(default=0.25, ge=-1, le=1)
    knowledge_context_chars: int = Field(default=12000, ge=1000, le=64000)

    @model_validator(mode="after")
    def validate_knowledge_chunking(self):
        if self.knowledge_chunk_overlap >= self.knowledge_chunk_size:
            raise ValueError("knowledge_chunk_overlap must be less than knowledge_chunk_size")
        return self

    # ---- OCR native work (AG-02, see aiq_agent.ocr.concurrency) ----
    # Concurrent native OCR tasks (PDFium is additionally always serialised).
    ocr_max_concurrent_jobs: int = Field(default=1, ge=1, le=8)
    # Tasks allowed to wait for a slot; beyond this the request gets HTTP 429.
    ocr_queue_capacity: int = Field(default=32, ge=0, le=1000)
    # Max time a caller waits for one native task (queue wait + run) → 504.
    ocr_job_timeout_seconds: float = Field(default=300.0, gt=0)

    # ---- Token optimisation (docs/audits/TOKEN_OPTIMIZATION_PLAN.md) ----
    # Phase 2: document extraction keeps only the skills its prompt names.
    extraction_lean_prompt: bool = Field(default=True)
    # Phase 3: >0 corrects consecutive OCR pages in one LLM call while their
    # text fits this many characters (page markers validated, per-page
    # fallback). 0 = one call per page, the pre-optimisation behaviour.
    ocr_correction_batch_chars: int = Field(default=0, ge=0, le=60_000)
    # OCR performance audit: PDF pages with a usable embedded text layer (and
    # not a scan) are used verbatim — no render, OCR or LLM correction.
    ocr_pdf_text_layer_first: bool = Field(default=True)
    # The diacritic-correction call needs no reasoning; adaptive thinking
    # made single pages take 50-86 s (~7k thinking tokens, measured).
    ocr_correction_disable_thinking: bool = Field(default=True)
    # Phase 4: agent chat/run sends only always-needed skills (declarative,
    # always_on) + keyword-matched ones; the rest are listed and loadable
    # on demand via the load_skill tool. False = every assigned skill inline.
    dynamic_skill_loading: bool = Field(default=False)
    # Phase 5: /agent/run without an agent (legacy run_task: every image
    # skill, CLI built-in tools, no manifest/RBAC agent scoping) is refused
    # unless explicitly re-enabled. The code path itself is kept.
    legacy_agent_run_enabled: bool = Field(default=False)
    # Phase 6: conversation history sent to the model — newest messages,
    # whole messages only, within both caps (chars approximate tokens; the
    # stored thread itself is not trimmed).
    history_max_messages: int = Field(default=20, ge=0, le=200)
    history_max_chars: int = Field(default=24_000, ge=0, le=400_000)
    # Hard ceiling on agent tool rounds, whatever a version configures
    # (12 = highest value published today, so no current agent changes).
    agent_max_turns_cap: int = Field(default=12, ge=1, le=20)

    # ---- Memory ----
    memory_backend: MemoryBackend = Field(default="in_memory")
    memory_ttl_seconds: int = Field(default=86_400, gt=0)
    memory_max_messages: int = Field(default=200, gt=0)
    redis_url: str | None = Field(default=None)
    redis_password: str | None = Field(default=None)
    # Postgres DSN for memory_backend=postgres. Format:
    #   postgresql://user:pass@host:5432/dbname
    memory_postgres_dsn: str | None = Field(default=None)

    # ---- Observability ----
    otel_service_name: str = Field(default="aiq-agent")
    otel_exporter_otlp_endpoint: str | None = Field(default=None)
    otel_deployment_environment: str = Field(default="production")


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    return Settings()
