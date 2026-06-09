"""Provider selection based on Settings."""

from __future__ import annotations

from aiq_agent.config import Settings
from aiq_agent.llm.anthropic_provider import AnthropicLlmService
from aiq_agent.llm.base import LlmService
from aiq_agent.llm.ollama import OllamaLlmService


def build_llm_service(settings: Settings) -> LlmService:
    if settings.llm_provider == "ollama":
        return OllamaLlmService(
            base_url=settings.ollama_base_url,
            model=settings.ollama_model,
            timeout_seconds=settings.ollama_timeout_seconds,
        )

    if settings.llm_provider == "anthropic":
        if not settings.anthropic_api_key:
            raise RuntimeError(
                "llm_provider=anthropic but ANTHROPIC_API_KEY is not set."
            )
        return AnthropicLlmService(
            api_key=settings.anthropic_api_key,
            model=settings.anthropic_model,
            max_tokens=settings.anthropic_max_tokens,
        )

    raise ValueError(f"Unknown llm_provider: {settings.llm_provider!r}")
