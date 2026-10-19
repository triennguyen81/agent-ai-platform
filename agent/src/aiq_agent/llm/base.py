"""LLM provider contract — vendor-agnostic chat interface."""

from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Literal

Role = Literal["system", "user", "assistant", "tool"]


@dataclass(frozen=True, slots=True)
class ChatMessage:
    role: Role
    content: str
    name: str | None = None


@dataclass(frozen=True, slots=True)
class LlmUsage:
    """Token usage of one chat() — possibly several API calls.

    Anthropic semantics: ``input_tokens`` is the UNCACHED prompt part only;
    the full prompt is input + cache_creation + cache_read. The three are
    disjoint, so summing them never double counts. ``cost_usd`` is the
    provider-reported cost (already priced per token class) — never derive
    a second cost from the token counts.
    """

    input_tokens: int
    output_tokens: int
    cache_creation_input_tokens: int = 0
    cache_read_input_tokens: int = 0
    llm_call_count: int = 1
    cost_usd: float | None = None

    @property
    def prompt_tokens(self) -> int:
        return self.input_tokens + self.cache_creation_input_tokens + self.cache_read_input_tokens

    @property
    def total_tokens(self) -> int:
        return self.prompt_tokens + self.output_tokens


@dataclass(frozen=True, slots=True)
class ChatResult:
    content: str
    model: str
    provider: str
    usage: LlmUsage | None = None


class LlmService(ABC):
    """Vendor-agnostic LLM client. Implementations must be async-safe."""

    provider: str
    model: str

    @abstractmethod
    async def chat(
        self,
        messages: Sequence[ChatMessage],
        *,
        temperature: float = 0.2,
        max_tokens: int | None = None,
    ) -> ChatResult:
        """Send a chat request and return the assistant reply."""

    @abstractmethod
    async def aclose(self) -> None:
        """Release underlying network resources."""
