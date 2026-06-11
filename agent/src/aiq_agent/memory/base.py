"""MemoryStore contract — append-only conversation history keyed by thread id."""

from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import Sequence
from dataclasses import dataclass

from aiq_agent.llm.base import ChatMessage


@dataclass(frozen=True, slots=True)
class ThreadSummary:
    """Light metadata for the thread-history sidebar."""

    thread_id: str
    title: str
    message_count: int
    updated_at: float  # unix seconds; 0 if unknown


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

    @abstractmethod
    async def list_threads(self, *, prefix: str | None = None, limit: int = 50) -> list[ThreadSummary]:
        """List threads, optionally filtered by an id prefix (e.g. per-user)."""

    async def aclose(self) -> None:
        """Release any underlying resources."""
        return None
