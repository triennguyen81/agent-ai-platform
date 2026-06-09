"""Redis-backed MemoryStore. Uses LIST per thread + JSON payloads.

Layout:
    Key:   aiq:mem:{thread_id}
    Type:  LIST of JSON-encoded ChatMessage dicts
    TTL:   refreshed on every append (sliding expiration)

Lazy-imports redis-py so unit tests can run without a live Redis.
"""

from __future__ import annotations

import json
from collections.abc import Sequence
from typing import Any

from aiq_agent.llm.base import ChatMessage
from aiq_agent.memory.base import MemoryStore


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

    async def append(self, thread_id: str, messages: Sequence[ChatMessage]) -> None:
        if not messages:
            return

        key = self._key(thread_id)
        payloads = [
            json.dumps({"role": m.role, "content": m.content, "name": m.name})
            for m in messages
        ]

        pipe = self._redis.pipeline()
        pipe.rpush(key, *payloads)
        pipe.ltrim(key, -self._max_messages, -1)  # cap memory growth
        pipe.expire(key, self._ttl_seconds)
        await pipe.execute()

    async def clear(self, thread_id: str) -> None:
        await self._redis.delete(self._key(thread_id))

    async def aclose(self) -> None:
        await self._redis.aclose()
