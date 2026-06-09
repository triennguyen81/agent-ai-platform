"""InMemoryMemoryStore behaviour — multi-turn, isolation between threads, clear."""

from __future__ import annotations

import pytest

from aiq_agent.llm.base import ChatMessage
from aiq_agent.memory import InMemoryMemoryStore


@pytest.mark.asyncio
async def test_get_returns_empty_for_unknown_thread() -> None:
    store = InMemoryMemoryStore()
    assert await store.get("does-not-exist") == []


@pytest.mark.asyncio
async def test_append_preserves_insertion_order() -> None:
    store = InMemoryMemoryStore()
    await store.append(
        "t1",
        [
            ChatMessage("user", "Xin chào"),
            ChatMessage("assistant", "Chào bạn"),
            ChatMessage("user", "Bạn khỏe không?"),
        ],
    )

    history = await store.get("t1")
    assert [m.content for m in history] == ["Xin chào", "Chào bạn", "Bạn khỏe không?"]


@pytest.mark.asyncio
async def test_limit_returns_last_n_messages() -> None:
    store = InMemoryMemoryStore()
    await store.append("t1", [ChatMessage("user", f"msg {i}") for i in range(5)])

    tail = await store.get("t1", limit=2)
    assert [m.content for m in tail] == ["msg 3", "msg 4"]


@pytest.mark.asyncio
async def test_threads_are_isolated() -> None:
    store = InMemoryMemoryStore()
    await store.append("alice", [ChatMessage("user", "alice prompt")])
    await store.append("bob",   [ChatMessage("user", "bob prompt")])

    alice = await store.get("alice")
    bob = await store.get("bob")

    assert [m.content for m in alice] == ["alice prompt"]
    assert [m.content for m in bob] == ["bob prompt"]


@pytest.mark.asyncio
async def test_clear_drops_thread_history() -> None:
    store = InMemoryMemoryStore()
    await store.append("t1", [ChatMessage("user", "hello")])
    await store.clear("t1")
    assert await store.get("t1") == []


@pytest.mark.asyncio
async def test_empty_append_is_noop() -> None:
    store = InMemoryMemoryStore()
    await store.append("t1", [])
    assert await store.get("t1") == []
