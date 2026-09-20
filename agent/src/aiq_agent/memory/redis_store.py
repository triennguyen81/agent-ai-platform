"""Redis-backed MemoryStore. Uses LIST per thread + JSON payloads.

Layout:
    Key:   aiq:mem:{thread_id}
    Type:  LIST of JSON-encoded ChatMessage dicts
    TTL:   refreshed on every append (sliding expiration)

Lazy-imports redis-py so unit tests can run without a live Redis.
"""

from __future__ import annotations

import json
import time
from collections.abc import Sequence
from typing import Any

from aiq_agent.llm.base import ChatMessage
from aiq_agent.memory.base import DEFAULT_THREAD_MODE, MemoryStore, ThreadSummary


class RedisMemoryStore(MemoryStore):
    def __init__(
        self,
        *,
        url: str,
        password: str | None = None,
        key_prefix: str = "aiq:mem:",
        ttl_seconds: int = 60 * 60 * 24,  # 24h sliding
        max_messages: int = 200,
    ) -> None:
        try:
            import redis.asyncio as redis_async  # type: ignore[import-not-found]
        except ImportError as exc:
            raise RuntimeError(
                "redis is not installed. Add it to your environment: `pip install redis>=5.2`."
            ) from exc

        kwargs: dict[str, Any] = {"decode_responses": True}
        if password:
            kwargs["password"] = password

        self._redis = redis_async.from_url(url, **kwargs)
        self._key_prefix = key_prefix
        self._ttl_seconds = ttl_seconds
        self._max_messages = max_messages

    def _key(self, thread_id: str) -> str:
        return f"{self._key_prefix}{thread_id}"

    async def get(self, thread_id: str, *, limit: int | None = None) -> list[ChatMessage]:
        raw = await self._redis.lrange(self._key(thread_id), 0, -1)
        decoded = [ChatMessage(**json.loads(item)) for item in raw]
        if limit is None or limit >= len(decoded):
            return decoded
        return decoded[-limit:]

    def _meta_key(self, thread_id: str) -> str:
        return f"{self._key_prefix}meta:{thread_id}"

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

        key = self._key(thread_id)
        meta_key = self._meta_key(thread_id)
        payloads = [
            json.dumps({"role": m.role, "content": m.content, "name": m.name})
            for m in messages
        ]
        now = time.time()
        first_user = next(
            (m.content for m in messages if m.role == "user"),
            None,
        )

        pipe = self._redis.pipeline()
        pipe.rpush(key, *payloads)
        pipe.ltrim(key, -self._max_messages, -1)  # cap memory growth
        pipe.expire(key, self._ttl_seconds)

        meta_updates: dict[str, str] = {"updated_at": f"{now:.3f}"}
        if first_user:
            # Only set the title on the first user message of the thread.
            pipe.hsetnx(meta_key, "title", first_user[:120])
        # Same set-once semantics as title: a thread's mode/agent_id are
        # fixed at creation and never changed by a later append to the
        # same id.
        pipe.hsetnx(meta_key, "mode", mode)
        if agent_id:
            pipe.hsetnx(meta_key, "agent_id", agent_id)
        pipe.hset(meta_key, mapping=meta_updates)
        pipe.expire(meta_key, self._ttl_seconds)
        await pipe.execute()

    async def clear(self, thread_id: str) -> None:
        pipe = self._redis.pipeline()
        pipe.delete(self._key(thread_id))
        pipe.delete(self._meta_key(thread_id))
        await pipe.execute()

    async def get_mode(self, thread_id: str) -> str:
        mode = await self._redis.hget(self._meta_key(thread_id), "mode")
        return mode or DEFAULT_THREAD_MODE

    async def get_agent_id(self, thread_id: str) -> str | None:
        agent_id = await self._redis.hget(self._meta_key(thread_id), "agent_id")
        return agent_id or None

    async def list_threads(
        self,
        *,
        prefix: str | None = None,
        limit: int = 50,
        mode: str | None = None,
        agent_id: str | None = None,
    ) -> list[ThreadSummary]:
        # SCAN over message-list keys; meta entries live under a separate
        # prefix so the pattern below doesn't collect them twice.
        scan_match = f"{self._key_prefix}{prefix or ''}*"
        meta_marker = f"{self._key_prefix}meta:"

        summaries: list[ThreadSummary] = []
        cursor = 0
        while True:
            cursor, keys = await self._redis.scan(cursor=cursor, match=scan_match, count=200)
            for key in keys:
                if key.startswith(meta_marker):
                    continue
                thread_id = key[len(self._key_prefix):]
                length = await self._redis.llen(key)
                if length == 0:
                    continue
                meta = await self._redis.hgetall(self._meta_key(thread_id))
                title = (meta.get("title") or "Cuộc trò chuyện")[:120]
                thread_mode = meta.get("mode") or DEFAULT_THREAD_MODE
                if mode is not None and thread_mode != mode:
                    continue
                thread_agent_id = meta.get("agent_id") or None
                if agent_id is not None and thread_agent_id != agent_id:
                    continue
                try:
                    updated_at = float(meta.get("updated_at", "0"))
                except ValueError:
                    updated_at = 0.0
                summaries.append(
                    ThreadSummary(
                        thread_id=thread_id,
                        title=title,
                        message_count=length,
                        updated_at=updated_at,
                        mode=thread_mode,
                        agent_id=thread_agent_id,
                    )
                )
            if cursor == 0:
                break

        summaries.sort(key=lambda s: s.updated_at, reverse=True)
        return summaries[:limit]

    async def aclose(self) -> None:
        await self._redis.aclose()
