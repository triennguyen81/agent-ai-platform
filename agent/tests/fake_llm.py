"""Deterministic LLM double used by pipeline + orchestrator tests."""

from __future__ import annotations

from collections.abc import Sequence

from aiq_agent.llm.base import ChatMessage, ChatResult, LlmService, LlmUsage


class FakeLlmService(LlmService):
    """Returns canned replies + records each call for assertion."""

    provider = "fake"
    model = "fake-model"

    def __init__(self, *, replies: Sequence[str]) -> None:
        self._replies = list(replies)
        self.calls: list[list[ChatMessage]] = []

    async def chat(
        self,
        messages: Sequence[ChatMessage],
        *,
        temperature: float = 0.2,
        max_tokens: int | None = None,
    ) -> ChatResult:
        self.calls.append(list(messages))
        if not self._replies:
            raise AssertionError("FakeLlmService ran out of canned replies")
        content = self._replies.pop(0)
        return ChatResult(
            content=content,
            model=self.model,
            provider=self.provider,
            usage=LlmUsage(input_tokens=len(messages), output_tokens=len(content)),
        )

    async def aclose(self) -> None:
        return None
