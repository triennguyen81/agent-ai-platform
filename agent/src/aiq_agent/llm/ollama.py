"""Ollama HTTP client — runs Qwen3 (or any local model) via /api/chat."""

from __future__ import annotations

from collections.abc import Sequence

import httpx

from aiq_agent.llm.base import ChatMessage, ChatResult, LlmService, LlmUsage


class OllamaLlmService(LlmService):
    provider = "ollama"

    def __init__(
        self,
        *,
        base_url: str,
        model: str,
        timeout_seconds: float,
    ) -> None:
        self.model = model
        self._client = httpx.AsyncClient(
            base_url=base_url.rstrip("/"),
            timeout=httpx.Timeout(timeout_seconds),
        )

    async def chat(
        self,
        messages: Sequence[ChatMessage],
        *,
        temperature: float = 0.2,
        max_tokens: int | None = None,
    ) -> ChatResult:
        options: dict[str, object] = {"temperature": temperature}
        if max_tokens is not None:
            options["num_predict"] = max_tokens

        payload = {
            "model": self.model,
            "stream": False,
            "options": options,
            "messages": [{"role": m.role, "content": m.content} for m in messages],
        }

        response = await self._client.post("/api/chat", json=payload)
        response.raise_for_status()
        data = response.json()

        message = data.get("message", {})
        content = message.get("content", "")

        usage = LlmUsage(
            input_tokens=int(data.get("prompt_eval_count", 0)),
            output_tokens=int(data.get("eval_count", 0)),
        )

        return ChatResult(
            content=content,
            model=self.model,
            provider=self.provider,
            usage=usage,
        )

    async def aclose(self) -> None:
        await self._client.aclose()
