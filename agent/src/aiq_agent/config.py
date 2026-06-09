"""Runtime configuration loaded from environment variables."""

from functools import lru_cache
from typing import Literal

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict

LlmProvider = Literal["ollama", "anthropic"]


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


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    return Settings()
