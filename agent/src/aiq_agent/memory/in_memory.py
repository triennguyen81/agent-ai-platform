"""In-process MemoryStore — useful for tests and single-replica dev runs."""

from __future__ import annotations

import asyncio
import time
from collections import defaultdict
from collections.abc import Sequence

from aiq_agent.llm.base import ChatMessage
from aiq_agent.memory.base import DEFAULT_THREAD_MODE, MemoryStore, ThreadSummary


class InMemoryMemoryStore(MemoryStore):
    def __init__(self) -> None:
        self._threads: dict[str, list[ChatMessage]] = defaultdict(list)
        self._updated: dict[str, float] = {}
        self._modes: dict[str, str] = {}
        self._agent_ids: dict[str, str | None] = {}
        self._lock = asyncio.Lock()

    async def get(self, thread_id: str, *, limit: int | None = None) -> list[ChatMessage]:
        async with self._lock:
            history = list(self._threads.get(thread_id, ()))
        if limit is None or limit >= len(history):
            return history
        return history[-limit:]

    async def append(
        self,
        thread_id: str,
        messages: Sequence[ChatMessage],
        *,
        mode: str = DEFAULT_THREAD_MODE,
        agent_id: str | None = None,
    ) -> None:
        if not messages:
            return
        async with self._lock:
            self._threads[thread_id].extend(messages)
            self._updated[thread_id] = time.time()
            self._modes.setdefault(thread_id, mode)
            if thread_id not in self._agent_ids:
                self._agent_ids[thread_id] = agent_id

    async def clear(self, thread_id: str) -> None:
        async with self._lock:
            self._threads.pop(thread_id, None)
            self._updated.pop(thread_id, None)
            self._modes.pop(thread_id, None)
            self._agent_ids.pop(thread_id, None)

    async def get_mode(self, thread_id: str) -> str:
        async with self._lock:
            return self._modes.get(thread_id, DEFAULT_THREAD_MODE)

    async def get_agent_id(self, thread_id: str) -> str | None:
        async with self._lock:
            return self._agent_ids.get(thread_id)

    async def list_threads(
        self,
        *,
        prefix: str | None = None,
        limit: int = 50,
        mode: str | None = None,
        agent_id: str | None = None,
    ) -> list[ThreadSummary]:
        async with self._lock:
            items: list[ThreadSummary] = []
            for tid, history in self._threads.items():
                if prefix and not tid.startswith(prefix):
                    continue
                thread_mode = self._modes.get(tid, DEFAULT_THREAD_MODE)
                if mode is not None and thread_mode != mode:
                    continue
                thread_agent_id = self._agent_ids.get(tid)
                if agent_id is not None and thread_agent_id != agent_id:
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
                        mode=thread_mode,
                        agent_id=thread_agent_id,
                    )
                )
            items.sort(key=lambda s: s.updated_at, reverse=True)
            return items[:limit]
