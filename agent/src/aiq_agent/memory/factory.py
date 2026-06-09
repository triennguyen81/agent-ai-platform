"""Builds the MemoryStore implementation selected by Settings."""

from __future__ import annotations

from aiq_agent.config import Settings
from aiq_agent.memory.base import MemoryStore
from aiq_agent.memory.in_memory import InMemoryMemoryStore
from aiq_agent.memory.redis_store import RedisMemoryStore


def build_memory_store(settings: Settings) -> MemoryStore:
    if settings.memory_backend == "redis":
        if not settings.redis_url:
            raise RuntimeError("memory_backend=redis but REDIS_URL is not set.")
        return RedisMemoryStore(
            url=settings.redis_url,
            password=settings.redis_password,
            ttl_seconds=settings.memory_ttl_seconds,
            max_messages=settings.memory_max_messages,
        )

    return InMemoryMemoryStore()
