"""In-process MemoryStore — useful for tests and single-replica dev runs."""

from __future__ import annotations

import asyncio
import time
from collections import defaultdict
from collections.abc import Sequence

from aiq_agent.llm.base import ChatMessage
from aiq_agent.memory.base import MemoryStore, ThreadSummary


class InMemoryMemoryStore(MemoryStore):
    def __init__(self) -> None:
        self._threads: dict[str, list[ChatMessage]] = defaultdict(list)
        self._updated: dict[str, float] = {}
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
            self._updated[thread_id] = time.time()

    async def clear(self, thread_id: str) -> None:
        async with self._lock:
            self._threads.pop(thread_id, None)
            self._updated.pop(thread_id, None)

    async def list_threads(
        self, *, prefix: str | None = None, limit: int = 50
    ) -> list[ThreadSummary]:
        async with self._lock:
            items: list[ThreadSummary] = []
            for tid, history in self._threads.items():
                if prefix and not tid.startswith(prefix):
                    continue
                first_user = next(
                    (m.content for m in history if m.role == "user"),
                    "Cuộc trò chuyện",
                )
                items.append(
                    ThreadSummary(
                        thread_id=tid,
                        title=first_user[:80],
                        message_count=len(history),
                        updated_at=self._updated.get(tid, 0.0),
                    )
                )
            items.sort(key=lambda s: s.updated_at, reverse=True)
            return items[:limit]
