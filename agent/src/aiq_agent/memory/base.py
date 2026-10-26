"""MemoryStore contract — append-only conversation history keyed by thread id."""

from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import Sequence
from dataclasses import dataclass

from aiq_agent.llm.base import ChatMessage

#: The only two valid values for a thread's persisted `mode`. Explicit and
#: set once at creation (first `append()` call) — never inferred later from
#: title text or message content. A thread with no stored mode (created
#: before this field existed) is treated as "chat" for backward
#: compatibility, everywhere a mode is read.
DEFAULT_THREAD_MODE = "chat"


@dataclass(frozen=True, slots=True)
class ThreadSummary:
    """Light metadata for the thread-history sidebar."""

    thread_id: str
    title: str
    message_count: int
    updated_at: float  # unix seconds; 0 if unknown
    mode: str = DEFAULT_THREAD_MODE
    # Which agent this thread belongs to, or None for a plain/no-agent chat
    # (e.g. an admin/super_admin using generic chat with no agent selected).
    # Set once at creation, same as `mode` and `title` — never changes on a
    # later append. A thread with no stored agent_id NEVER matches a
    # `list_threads(agent_id=...)` filter for a specific agent, even if that
    # agent happens to be the caller's currently selected one — see
    # list_threads docstring below.
    agent_id: str | None = None


class MemoryStore(ABC):
    """Store and retrieve chat history for a given thread.

    Implementations must be async-safe and tolerate concurrent appends to the
    same thread. Order of messages within a thread MUST match insertion order.
    """

    @abstractmethod
    async def get(self, thread_id: str, *, limit: int | None = None) -> list[ChatMessage]:
        """Return the message history. Newest message last."""

    @abstractmethod
    async def append(
        self,
        thread_id: str,
        messages: Sequence[ChatMessage],
        *,
        mode: str = DEFAULT_THREAD_MODE,
        agent_id: str | None = None,
    ) -> None:
        """Append messages to the thread.

        `mode` and `agent_id` are persisted only on the THREAD's first append
        (like the title, derived from the first user message) and never
        overwritten on later appends to the same thread_id — a thread cannot
        silently change mode or switch to a different agent mid-conversation.
        """

    @abstractmethod
    async def clear(self, thread_id: str) -> None:
        """Drop all messages for a thread."""

    @abstractmethod
    async def get_mode(self, thread_id: str) -> str:
        """Return the thread's persisted mode, or DEFAULT_THREAD_MODE if the
        thread doesn't exist or predates this field."""

    @abstractmethod
    async def get_agent_id(self, thread_id: str) -> str | None:
        """Return the thread's persisted agent_id, or None if the thread
        doesn't exist, predates this field, or was created without an
        agent (plain chat)."""

    @abstractmethod
    async def list_threads(
        self,
        *,
        prefix: str | None = None,
        limit: int = 50,
        mode: str | None = None,
        agent_id: str | None = None,
    ) -> list[ThreadSummary]:
        """List threads, optionally filtered by an id prefix (e.g. per-user),
        an exact `mode` match, and/or an exact `agent_id` match.

        `mode=None` / `agent_id=None` return every mode / every agent
        respectively — passing an explicit `agent_id` never matches a
        thread that has no stored agent_id (legacy threads must not
        silently attach to whichever agent the caller happens to filter
        by)."""

    async def aclose(self) -> None:
        """Release any underlying resources."""
        return None


def trim_history(
    messages: list[ChatMessage], *, max_messages: int, max_chars: int
) -> list[ChatMessage]:
    """Newest whole messages within both budgets (token Phase 6).

    A leading assistant message is dropped so the window never starts with
    an answer whose question was cut off.
    """
    kept: list[ChatMessage] = []
    used = 0
    for message in reversed(messages):
        if len(kept) >= max_messages or used + len(message.content) > max_chars:
            break
        kept.append(message)
        used += len(message.content)
    kept.reverse()
    while kept and kept[0].role == "assistant":
        kept.pop(0)
    return kept
