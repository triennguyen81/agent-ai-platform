"""MemoryStore contract — append-only conversation history keyed by thread id."""

from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import Sequence

from aiq_agent.llm.base import ChatMessage


class MemoryStore(ABC):
    """Store and retrieve chat history for a given thread.

    Implementations must be async-safe and tolerate concurrent appends to the
    same thread. Order of messages within a thread MUST match insertion order.
    """

    @abstractmethod
    async def get(self, thread_id: str, *, limit: int | None = None) -> list[ChatMessage]:
        """Return the message history. Newest message last."""

    @abstractmethod
    async def append(self, thread_id: str, messages: Sequence[ChatMessage]) -> None:
        """Append messages to the thread."""

    @abstractmethod
    async def clear(self, thread_id: str) -> None:
        """Drop all messages for a thread."""

    async def aclose(self) -> None:
        """Release any underlying resources."""
        return None
