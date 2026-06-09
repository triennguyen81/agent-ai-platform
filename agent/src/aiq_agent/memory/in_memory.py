"""In-process MemoryStore — useful for tests and single-replica dev runs."""

from __future__ import annotations

import asyncio
from collections import defaultdict
from collections.abc import Sequence

from aiq_agent.llm.base import ChatMessage
from aiq_agent.memory.base import MemoryStore


class InMemoryMemoryStore(MemoryStore):
    def __init__(self) -> None:
        self._threads: dict[str, list[ChatMessage]] = defaultdict(list)
        self._lock = asyncio.Lock()

    async def get(self, thread_id: str, *, limit: int | None = None) -> list[ChatMessage]:
        async with self._lock:
            history = list(self._threads.get(thread_id, ()))
        if limit is None or limit >= len(history):
            return history
        return history[-limit:]

    async def append(self, thread_id: str, messages: Sequence[ChatMessage]) -> None:
        if not messages:
            return
        async with self._lock:
            self._threads[thread_id].extend(messages)

    async def clear(self, thread_id: str) -> None:
        async with self._lock:
            self._threads.pop(thread_id, None)
