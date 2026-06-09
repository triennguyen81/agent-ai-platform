"""Anthropic Claude provider — separates system prompt per Messages API rules."""

from __future__ import annotations

from collections.abc import Sequence

from anthropic import AsyncAnthropic

from aiq_agent.llm.base import ChatMessage, ChatResult, LlmService, LlmUsage


class AnthropicLlmService(LlmService):
    provider = "anthropic"

    def __init__(self, *, api_key: str, model: str, max_tokens: int) -> None:
        self.model = model
        self._default_max_tokens = max_tokens
        self._client = AsyncAnthropic(api_key=api_key)

    async def chat(
        self,
        messages: Sequence[ChatMessage],
        *,
        temperature: float = 0.2,
        max_tokens: int | None = None,
    ) -> ChatResult:
        system_parts = [m.content for m in messages if m.role == "system"]
        conversation = [
            {"role": m.role, "content": m.content}
            for m in messages
            if m.role in ("user", "assistant")
        ]

        response = await self._client.messages.create(
            model=self.model,
            max_tokens=max_tokens or self._default_max_tokens,
            temperature=temperature,
            system="\n\n".join(system_parts) if system_parts else None,
            messages=conversation,
        )

        text_blocks = [block.text for block in response.content if block.type == "text"]
        content = "".join(text_blocks)

        usage = LlmUsage(
            input_tokens=response.usage.input_tokens,
            output_tokens=response.usage.output_tokens,
        )

        return ChatResult(
            content=content,
            model=response.model,
            provider=self.provider,
            usage=usage,
        )

    async def aclose(self) -> None:
        await self._client.close()
