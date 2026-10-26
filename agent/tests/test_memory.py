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


# --- mode (Chat vs Agentic history separation) ------------------------------


@pytest.mark.asyncio
async def test_thread_defaults_to_chat_mode_when_not_specified() -> None:
    store = InMemoryMemoryStore()
    await store.append("t1", [ChatMessage("user", "hi")])
    assert await store.get_mode("t1") == "chat"


@pytest.mark.asyncio
async def test_get_mode_returns_chat_for_unknown_thread() -> None:
    store = InMemoryMemoryStore()
    assert await store.get_mode("does-not-exist") == "chat"


@pytest.mark.asyncio
async def test_agentic_mode_is_persisted_explicitly() -> None:
    store = InMemoryMemoryStore()
    await store.append("t1", [ChatMessage("user", "run a task")], mode="agentic")
    assert await store.get_mode("t1") == "agentic"


@pytest.mark.asyncio
async def test_mode_is_set_once_and_never_changed_by_a_later_append() -> None:
    store = InMemoryMemoryStore()
    await store.append("t1", [ChatMessage("user", "first")], mode="agentic")
    await store.append("t1", [ChatMessage("user", "second")], mode="chat")
    assert await store.get_mode("t1") == "agentic"


@pytest.mark.asyncio
async def test_list_threads_filters_by_mode_without_mixing() -> None:
    store = InMemoryMemoryStore()
    await store.append("chat-1", [ChatMessage("user", "chat question")], mode="chat")
    await store.append("agentic-1", [ChatMessage("user", "agentic task")], mode="agentic")

    chat_only = await store.list_threads(mode="chat")
    agentic_only = await store.list_threads(mode="agentic")

    assert [s.thread_id for s in chat_only] == ["chat-1"]
    assert [s.thread_id for s in agentic_only] == ["agentic-1"]
    assert chat_only[0].mode == "chat"
    assert agentic_only[0].mode == "agentic"


@pytest.mark.asyncio
async def test_list_threads_with_no_mode_filter_returns_both() -> None:
    store = InMemoryMemoryStore()
    await store.append("chat-1", [ChatMessage("user", "chat question")], mode="chat")
    await store.append("agentic-1", [ChatMessage("user", "agentic task")], mode="agentic")

    all_threads = await store.list_threads()

    assert {s.thread_id for s in all_threads} == {"chat-1", "agentic-1"}


# --- agent_id (per-agent history separation / no cross-agent bleed) ---------


@pytest.mark.asyncio
async def test_thread_defaults_to_no_agent_when_not_specified() -> None:
    store = InMemoryMemoryStore()
    await store.append("t1", [ChatMessage("user", "hi")])
    assert await store.get_agent_id("t1") is None


@pytest.mark.asyncio
async def test_get_agent_id_returns_none_for_unknown_thread() -> None:
    store = InMemoryMemoryStore()
    assert await store.get_agent_id("does-not-exist") is None


@pytest.mark.asyncio
async def test_agent_id_is_persisted_explicitly() -> None:
    store = InMemoryMemoryStore()
    await store.append("t1", [ChatMessage("user", "hi")], agent_id="agent-zzz")
    assert await store.get_agent_id("t1") == "agent-zzz"


@pytest.mark.asyncio
async def test_agent_id_is_set_once_and_never_changed_by_a_later_append() -> None:
    store = InMemoryMemoryStore()
    await store.append("t1", [ChatMessage("user", "first")], agent_id="agent-zzz")
    await store.append("t1", [ChatMessage("user", "second")], agent_id="agent-other")
    assert await store.get_agent_id("t1") == "agent-zzz"


@pytest.mark.asyncio
async def test_list_threads_filters_by_agent_id_without_mixing() -> None:
    store = InMemoryMemoryStore()
    await store.append("zzz-1", [ChatMessage("user", "zzz question")], agent_id="agent-zzz")
    await store.append(
        "acct-1", [ChatMessage("user", "accounting question")], agent_id="agent-accounting"
    )

    zzz_only = await store.list_threads(agent_id="agent-zzz")
    acct_only = await store.list_threads(agent_id="agent-accounting")

    assert [s.thread_id for s in zzz_only] == ["zzz-1"]
    assert [s.thread_id for s in acct_only] == ["acct-1"]
    assert zzz_only[0].agent_id == "agent-zzz"
    assert acct_only[0].agent_id == "agent-accounting"


@pytest.mark.asyncio
async def test_list_threads_agent_filter_excludes_legacy_no_agent_threads() -> None:
    """Requirement: a thread with no stored agent_id must never silently
    attach to whichever agent the caller is currently filtering by."""
    store = InMemoryMemoryStore()
    await store.append("legacy-1", [ChatMessage("user", "old thread, no agent")])
    await store.append("zzz-1", [ChatMessage("user", "zzz question")], agent_id="agent-zzz")

    zzz_only = await store.list_threads(agent_id="agent-zzz")

    assert [s.thread_id for s in zzz_only] == ["zzz-1"]


@pytest.mark.asyncio
async def test_list_threads_combines_mode_and_agent_id_filters() -> None:
    store = InMemoryMemoryStore()
    await store.append("zzz-chat", [ChatMessage("user", "q1")], mode="chat", agent_id="agent-zzz")
    await store.append(
        "zzz-agentic", [ChatMessage("user", "q2")], mode="agentic", agent_id="agent-zzz"
    )
    await store.append(
        "acct-chat", [ChatMessage("user", "q3")], mode="chat", agent_id="agent-accounting"
    )

    result = await store.list_threads(mode="chat", agent_id="agent-zzz")

    assert [s.thread_id for s in result] == ["zzz-chat"]


@pytest.mark.asyncio
async def test_list_threads_with_no_agent_filter_returns_every_agent() -> None:
    store = InMemoryMemoryStore()
    await store.append("zzz-1", [ChatMessage("user", "q1")], agent_id="agent-zzz")
    await store.append("acct-1", [ChatMessage("user", "q2")], agent_id="agent-accounting")

    all_threads = await store.list_threads()

    assert {s.thread_id for s in all_threads} == {"zzz-1", "acct-1"}
