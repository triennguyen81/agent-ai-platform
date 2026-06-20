"""Runtime configuration loaded from environment variables."""

from functools import lru_cache
from typing import Literal

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict

LlmProvider = Literal["ollama", "anthropic"]
MemoryBackend = Literal["in_memory", "redis"]


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

    # ---- Gateway (for agentic tools that proxy through Gateway) ----
    gateway_base_url: str = Field(default="http://aiq-gateway:8080")

    # ---- M6: Embeddings + Qdrant top-K RAG ----
    # Self-hosted via fastembed (ONNX) — $0 marginal cost. Falls back to
    # full-context RAG if Qdrant or model init fails.
    # multilingual-e5-small: 384 dim, ~120MB, decent Vietnamese support.
    embedding_model: str = Field(default="intfloat/multilingual-e5-small")
    embedding_dim: int = Field(default=384, gt=0)
    embedding_enabled: bool = Field(default=True)
    qdrant_url: str = Field(default="http://aiq-qdrant:6333")
    qdrant_collection: str = Field(default="aiq_notes")
    qdrant_top_k: int = Field(default=10, gt=0, le=100)

    # ---- Memory ----
    memory_backend: MemoryBackend = Field(default="in_memory")
    memory_ttl_seconds: int = Field(default=86_400, gt=0)
    memory_max_messages: int = Field(default=200, gt=0)
    redis_url: str | None = Field(default=None)
    redis_password: str | None = Field(default=None)

    # ---- Observability ----
    otel_service_name: str = Field(default="aiq-agent")
    otel_exporter_otlp_endpoint: str | None = Field(default=None)
    otel_deployment_environment: str = Field(default="production")


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    return Settings()
