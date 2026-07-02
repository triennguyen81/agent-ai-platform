"""Postgres-backed MemoryStore.

Persistence layout — 1 thread = 1 row:

    Table chat_threads:
        thread_id      PK (text)
        title          text
        messages       jsonb   (Claude Messages API layout — array of role/content)
        message_count  int
        created_at     timestamptz
        updated_at     timestamptz (auto-bumped via trigger)

Unlike the Redis store (LIST of per-message JSON), this keeps the whole
conversation as a single JSONB blob so:

  - `get()` = 1 SELECT
  - `append()` = 1 UPSERT that does `messages || new_msgs`
  - `list_threads(prefix)` = 1 SELECT with LIKE (uses text_pattern_ops index)

Thread lifetime is unbounded — no TTL. Users must explicitly delete a
thread from the sidebar. Cap on messages per thread is enforced client-side
by slicing to the last `max_messages` on append.
"""

from __future__ import annotations

import json
import logging
import time
from collections.abc import Sequence
from typing import Any

from aiq_agent.llm.base import ChatMessage
from aiq_agent.memory.base import MemoryStore, ThreadSummary

logger = logging.getLogger(__name__)


class PostgresMemoryStore(MemoryStore):
    """Persist chat threads in Postgres as JSONB rows.

    Lazy-imports `asyncpg` so unit tests that don't need Postgres can still
    import the class. Connection pool is created on first use.
    """

    def __init__(
        self,
        *,
        dsn: str,
        max_messages: int = 200,
        pool_min_size: int = 1,
        pool_max_size: int = 4,
    ) -> None:
        self._dsn = dsn
        self._max_messages = max_messages
        self._pool_min = pool_min_size
        self._pool_max = pool_max_size
        self._pool: Any | None = None  # asyncpg.Pool once initialised

    async def _get_pool(self) -> Any:
        if self._pool is None:
            try:
                import asyncpg  # type: ignore[import-not-found]
            except ImportError as exc:  # pragma: no cover — deps missing
                raise RuntimeError(
                    "asyncpg is not installed. Add it to the agent env "
                    "(already present in pyproject.toml)."
                ) from exc

            self._pool = await asyncpg.create_pool(
                self._dsn,
                min_size=self._pool_min,
                max_size=self._pool_max,
            )
            logger.info("PostgresMemoryStore: pool ready (dsn=%s)", _mask_dsn(self._dsn))
        return self._pool

    async def get(
        self, thread_id: str, *, limit: int | None = None
    ) -> list[ChatMessage]:
        pool = await self._get_pool()
        async with pool.acquire() as conn:
            row = await conn.fetchrow(
                "SELECT messages FROM chat_threads WHERE thread_id = $1",
                thread_id,
            )
        if row is None:
            return []

        raw = row["messages"]
        # asyncpg returns JSONB as string by default (no codec registered);
        # handle both list and str for robustness.
        if isinstance(raw, str):
            try:
                items = json.loads(raw)
            except json.JSONDecodeError:
                logger.warning("Corrupt JSONB for thread %s", thread_id)
                return []
        else:
            items = raw or []

        decoded = [
            ChatMessage(
                role=str(m.get("role", "user")),
                content=str(m.get("content", "")),
                name=m.get("name"),
            )
            for m in items
            if isinstance(m, dict)
        ]
        if limit is None or limit >= len(decoded):
            return decoded
        return decoded[-limit:]

    async def append(
        self, thread_id: str, messages: Sequence[ChatMessage]
    ) -> None:
        if not messages:
            return

        payloads = [
            {"role": m.role, "content": m.content, "name": m.name}
            for m in messages
        ]
        new_json = json.dumps(payloads)
        # First user message wins the thread title
        first_user_content = next(
            (m.content for m in messages if m.role == "user"),
            None,
        )
        title_snippet = (first_user_content or "")[:120] if first_user_content else None

        pool = await self._get_pool()
        async with pool.acquire() as conn:
            async with conn.transaction():
                # Upsert: append new messages, bump count, set title on first insert
                await conn.execute(
                    """
                    INSERT INTO chat_threads (thread_id, title, messages, message_count)
                    VALUES ($1, $2, $3::jsonb, $4)
                    ON CONFLICT (thread_id) DO UPDATE
                    SET messages       = chat_threads.messages || EXCLUDED.messages,
                        message_count  = chat_threads.message_count + EXCLUDED.message_count,
                        title          = COALESCE(chat_threads.title, EXCLUDED.title)
                    """,
                    thread_id,
                    title_snippet,
                    new_json,
                    len(payloads),
                )

                # Cap the messages array to the last max_messages entries so
                # threads that go on forever don't rewrite a giant JSONB every
                # turn. Uses jsonb_path_query_array — Postgres 12+ compatible.
                await conn.execute(
                    """
                    UPDATE chat_threads
                    SET messages = (
                        SELECT COALESCE(jsonb_agg(elem), '[]'::jsonb)
                        FROM (
                            SELECT elem
                            FROM jsonb_array_elements(messages) WITH ORDINALITY AS t(elem, ord)
                            WHERE ord > GREATEST(0, jsonb_array_length(messages) - $2)
                            ORDER BY ord
                        ) capped
                    )
                    WHERE thread_id = $1
                      AND jsonb_array_length(messages) > $2
                    """,
                    thread_id,
                    self._max_messages,
                )

    async def clear(self, thread_id: str) -> None:
        pool = await self._get_pool()
        async with pool.acquire() as conn:
            await conn.execute(
                "DELETE FROM chat_threads WHERE thread_id = $1", thread_id
            )

    async def list_threads(
        self, *, prefix: str | None = None, limit: int = 50
    ) -> list[ThreadSummary]:
        pool = await self._get_pool()
        pattern = f"{prefix}%" if prefix else "%"
        async with pool.acquire() as conn:
            rows = await conn.fetch(
                """
                SELECT thread_id, title, message_count, updated_at
                FROM chat_threads
                WHERE thread_id LIKE $1
                  AND message_count > 0
                ORDER BY updated_at DESC
                LIMIT $2
                """,
                pattern,
                max(1, min(limit, 200)),
            )

        return [
            ThreadSummary(
                thread_id=r["thread_id"],
                title=(r["title"] or "Cuộc trò chuyện")[:120],
                message_count=int(r["message_count"] or 0),
                updated_at=(
                    r["updated_at"].timestamp() if r["updated_at"] is not None else 0.0
                ),
            )
            for r in rows
        ]

    async def aclose(self) -> None:
        if self._pool is not None:
            await self._pool.close()
            self._pool = None


def _mask_dsn(dsn: str) -> str:
    """Hide the password portion of a DSN for logs."""
    try:
        # postgresql://user:pass@host/db → postgresql://user:***@host/db
        head, _, tail = dsn.partition("://")
        if not tail or "@" not in tail:
            return dsn
        creds, _, rest = tail.partition("@")
        if ":" in creds:
            user, _, _ = creds.partition(":")
            return f"{head}://{user}:***@{rest}"
        return dsn
    except Exception:  # noqa: BLE001
        return dsn
