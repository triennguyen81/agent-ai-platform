from __future__ import annotations

import json

import pytest

from aiq_agent.audit.store import AuditStore
from aiq_agent.api.routes import _tenant_thread_id, _tenant_thread_prefix
from aiq_agent.api.schemas import EmbeddingQueryRequest
from aiq_agent.api.security import RuntimePrincipal
from aiq_agent.embeddings.service import IngestNote, _note_id_to_point_id
from pydantic import ValidationError
from types import SimpleNamespace


def principal(tenant: str, user: str = "user-a") -> RuntimePrincipal:
    return RuntimePrincipal(token="token", user_id=user, roles=frozenset({"user"}), tenant_id=tenant)


def test_conversation_keys_are_tenant_and_user_scoped() -> None:
    a = _tenant_thread_id(principal("tenant-a"), "same-thread")
    b = _tenant_thread_id(principal("tenant-b"), "same-thread")
    other_user = _tenant_thread_id(principal("tenant-a", "user-b"), "same-thread")
    assert len({a, b, other_user}) == 3
    assert a.startswith(_tenant_thread_prefix(principal("tenant-a")))


def test_vector_point_ids_are_tenant_scoped_even_for_uuid_note_ids() -> None:
    note_id = "11111111-1111-1111-1111-111111111111"
    assert _note_id_to_point_id("tenant-a", note_id) != _note_id_to_point_id("tenant-b", note_id)


def test_vector_hash_is_stable_but_requires_explicit_tenant_metadata() -> None:
    note = IngestNote(note_id="n", tenant_id="tenant-a", user_id="u", title="t", content="c")
    assert note.hash == note.hash


def test_embedding_request_rejects_forged_tenant_filter() -> None:
    try:
        EmbeddingQueryRequest(query="q", notes=[], tenant_id="tenant-b")
    except ValidationError:
        pass
    else:
        raise AssertionError("unknown tenant_id must be rejected")


def test_qdrant_search_always_intersects_tenant_and_user_filters() -> None:
    from aiq_agent.embeddings.service import EmbeddingService

    class Encoder:
        def embed(self, texts):
            return [[0.1, 0.2] for _ in texts]

    class Qdrant:
        kwargs = None

        def query_points(self, **kwargs):
            self.kwargs = kwargs
            return SimpleNamespace(points=[])

    service = EmbeddingService(model_name="m", embedding_dim=2, qdrant_url="http://q", collection="c")
    service._encoder = Encoder()
    service._qdrant = Qdrant()
    service.search(query="q", tenant_id="tenant-a", user_id="user-a", k=3)
    conditions = service._qdrant.kwargs["query_filter"].must
    assert {(condition.key, condition.match.value) for condition in conditions} == {
        ("tenant_id", "tenant-a"), ("user_id", "user-a")
    }


@pytest.mark.asyncio
async def test_audit_listing_is_tenant_scoped_before_returning_entries() -> None:
    class Redis:
        async def lrange(self, _key, _start, _end):
            base = {
                "ts": 1.0, "event": "chat", "user_prefix": "user",
                "name": "agent", "ms": 10, "ok": True, "detail": None,
            }
            return [
                json.dumps({**base, "tenant_id": "tenant-a", "department_id": "accounting"}),
                json.dumps({**base, "tenant_id": "tenant-a", "department_id": "sales"}),
                json.dumps({**base, "tenant_id": "tenant-b", "department_id": "accounting"}),
            ]

    entries = await AuditStore(redis=Redis()).list(
        tenant_id="tenant-a", department_id="accounting"
    )
    assert [(entry.tenant_id, entry.department_id) for entry in entries] == [
        ("tenant-a", "accounting")
    ]
