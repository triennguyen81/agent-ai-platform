"""Run against an isolated schema and Qdrant collection; never touches application rows.

TEST_KNOWLEDGE_DSN=postgresql://... TEST_QDRANT_URL=http://... pytest tests/test_knowledge_integration.py
Omit TEST_QDRANT_URL to use Qdrant's real local implementation.
"""

import os
from pathlib import Path
from types import SimpleNamespace
from uuid import uuid4

import pytest
import pytest_asyncio

from aiq_agent.config import Settings
from aiq_agent.embeddings.service import EmbeddingService
from aiq_agent.knowledge.service import KnowledgeService
from aiq_agent.runtime.manifest import ExecutionManifest
from tests.test_dynamic_runtime import manifest_data

DSN = os.getenv("TEST_KNOWLEDGE_DSN")
pytestmark = pytest.mark.skipif(not DSN, reason="TEST_KNOWLEDGE_DSN not configured")


@pytest_asyncio.fixture
async def kb():
    import asyncpg
    from qdrant_client import QdrantClient
    from qdrant_client.http import models as qm

    schema = "kb_test_" + uuid4().hex
    admin = await asyncpg.connect(DSN)
    await admin.execute(f"CREATE SCHEMA {schema}")
    pool = await asyncpg.create_pool(
        DSN, min_size=1, max_size=5, server_settings={"search_path": schema}
    )
    collection = "kb_test_" + uuid4().hex
    client = (
        QdrantClient(
            url=os.getenv("TEST_QDRANT_URL"),
            api_key=os.getenv("TEST_QDRANT_API_KEY"),
            check_compatibility=False,
        )
        if os.getenv("TEST_QDRANT_URL")
        else QdrantClient(":memory:")
    )
    try:
        async with pool.acquire() as conn:
            await conn.execute("""
                CREATE TABLE tenants(id uuid PRIMARY KEY);
                CREATE TABLE users(id uuid PRIMARY KEY);
                CREATE TABLE departments(id uuid PRIMARY KEY,tenant_id uuid NOT NULL,UNIQUE(id,tenant_id));
                CREATE TABLE agents(id uuid PRIMARY KEY);
                CREATE TABLE notes(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),tenant_id uuid,
                  department_id uuid,user_id uuid,created_by uuid,title text NOT NULL,content text NOT NULL,
                  tags text[] NOT NULL DEFAULT '{}',scope text NOT NULL DEFAULT 'department',is_archived boolean NOT NULL DEFAULT false);
                CREATE TABLE agent_knowledge(agent_id uuid,note_id uuid,PRIMARY KEY(agent_id,note_id));
            """)
            tenant, other, department, agent, unattached = [uuid4() for _ in range(5)]
            await conn.executemany("INSERT INTO tenants VALUES($1)", [(tenant,), (other,)])
            await conn.execute("INSERT INTO departments VALUES($1,$2)", department, tenant)
            await conn.executemany("INSERT INTO agents VALUES($1)", [(agent,), (unattached,)])
            note = await conn.fetchval(
                "INSERT INTO notes(tenant_id,department_id,title,content) VALUES($1,$2,'Overdue policy','Contact customers after 30 days overdue.') RETURNING id",
                tenant,
                department,
            )
            await conn.execute("INSERT INTO agent_knowledge VALUES($1,$2)", agent, note)
            migration = Path(
                os.getenv(
                    "KB_MIGRATION",
                    "/repo/gateway/src/Aiq.Gateway/Data/Migrations/047_knowledge_rag.sql",
                )
            )
            await conn.execute(migration.read_text())
        client.create_collection(
            collection, vectors_config=qm.VectorParams(size=2, distance=qm.Distance.COSINE)
        )
        embeddings = EmbeddingService(
            model_name="test", embedding_dim=2, qdrant_url="unused", collection=collection
        )
        embeddings._qdrant = client
        # Deterministic vectors make lifecycle/authorization tests independent of model quality.
        embeddings._encoder = SimpleNamespace(embed=lambda texts: [[1.0, 0.0] for _ in texts])
        settings = Settings(
            knowledge_score_threshold=0.0, knowledge_chunk_size=80, knowledge_chunk_overlap=20
        )
        service = KnowledgeService(pool, embeddings, settings)
        manifest = ExecutionManifest.model_validate(
            manifest_data(
                tenantId=str(tenant),
                departmentId=str(department),
                agent=dict(
                    id=str(agent), slug="kb-agent", name="KB", description=None, iconMetadata={}
                ),
                knowledgeBaseIds=[str(note)],
                knowledge=[dict(noteId=str(note), title="Overdue policy", content="", tags=[])],
            )
        )
        yield SimpleNamespace(
            pool=pool,
            service=service,
            manifest=manifest,
            note=note,
            tenant=tenant,
            other=other,
            department=department,
            agent=agent,
            unattached=unattached,
            embeddings=embeddings,
        )
    finally:
        client.delete_collection(collection)
        client.close()
        await pool.close()
        await admin.execute(f"DROP SCHEMA {schema} CASCADE")
        await admin.close()


async def drain(kb):
    for _ in range(20):
        if not await kb.service.process_one():
            break


async def test_legacy_ingestion_retrieval_and_isolation(kb):
    async with kb.pool.acquire() as conn:
        foreign_note = await conn.fetchval(
            "INSERT INTO notes(tenant_id,scope,title,content) VALUES($1,'tenant','Secret','Other tenant overdue policy') RETURNING id",
            kb.other,
        )
    await drain(kb)
    hits = await kb.service.retrieve(kb.manifest, "overdue")
    assert hits and hits[0]["version"] == 1 and hits[0]["page"] == 1
    forged_refs = kb.manifest.model_copy(update={"knowledgeBaseIds": [kb.note, foreign_note]})
    assert all(hit["document_id"] == str(kb.note) for hit in await kb.service.retrieve(forged_refs, "overdue"))
    other_agent = kb.manifest.model_copy(deep=True)
    other_agent.agent.id = kb.unattached
    assert await kb.service.retrieve(other_agent, "overdue") == []
    other_tenant = kb.manifest.model_copy(update={"tenantId": kb.other})
    assert await kb.service.retrieve(other_tenant, "overdue") == []
    async with kb.pool.acquire() as conn:
        await conn.execute("DELETE FROM agent_knowledge WHERE agent_id=$1", kb.agent)
    assert await kb.service.retrieve(kb.manifest, "overdue") == []


async def test_new_version_active_only_after_index_and_audit_preserved(kb):
    await drain(kb)
    async with kb.pool.acquire() as conn:
        await conn.execute(
            "UPDATE notes SET content='New policy: escalate after 35 days.' WHERE id=$1", kb.note
        )
    assert (await kb.service.retrieve(kb.manifest, "policy"))[0]["version"] == 1
    await drain(kb)
    hits = await kb.service.retrieve(kb.manifest, "policy")
    assert hits and all(h["version"] == 2 for h in hits)
    async with kb.pool.acquire() as conn:
        assert (
            await conn.fetchval(
                "SELECT count(*) FROM document_versions WHERE document_id=$1", kb.note
            )
            == 2
        )
        old = await conn.fetchval(
            "SELECT id FROM document_versions WHERE document_id=$1 AND version=1", kb.note
        )
        with pytest.raises(Exception, match="immutable"):
            await conn.execute(
                "UPDATE document_versions SET extracted_text='overwrite' WHERE id=$1", old
            )
    points = kb.embeddings._qdrant.scroll(kb.embeddings._collection, limit=100)[0]
    assert all(p.payload["document_version_id"] == hits[0]["document_version_id"] for p in points)


async def test_disable_delete_reindex_and_retry(kb):
    original = kb.embeddings.index_chunks

    def fail(_):
        raise RuntimeError("Qdrant unavailable")

    kb.embeddings.index_chunks = fail
    assert not await kb.service.process_one()
    assert await kb.service.retrieve(kb.manifest, "policy") == []
    async with kb.pool.acquire() as conn:
        assert await conn.fetchval("SELECT count(*) FROM indexing_jobs WHERE status='failed'") == 1
        await conn.execute("UPDATE indexing_jobs SET started_at=now()-interval '2 minutes'")
    kb.embeddings.index_chunks = original
    await drain(kb)
    initial = await kb.service.retrieve(kb.manifest, "policy")
    assert initial
    async with kb.pool.acquire() as conn:
        await conn.execute(
            "INSERT INTO indexing_jobs(document_id,document_version_id) SELECT id,current_version_id FROM notes WHERE id=$1",
            kb.note,
        )
    await drain(kb)
    assert (await kb.service.retrieve(kb.manifest, "policy"))[0]["chunk_id"] == initial[0][
        "chunk_id"
    ]
    async with kb.pool.acquire() as conn:
        await conn.execute("UPDATE notes SET is_archived=true WHERE id=$1", kb.note)
    # Revocation works even before vector cleanup runs.
    assert await kb.service.retrieve(kb.manifest, "policy") == []
    await drain(kb)
    assert kb.embeddings._qdrant.count(kb.embeddings._collection).count == 0
    async with kb.pool.acquire() as conn:
        await conn.execute("UPDATE notes SET is_archived=false WHERE id=$1", kb.note)
    await drain(kb)
    assert await kb.service.retrieve(kb.manifest, "policy")
    async with kb.pool.acquire() as conn:
        await conn.execute("DELETE FROM notes WHERE id=$1", kb.note)
    assert await kb.service.retrieve(kb.manifest, "policy") == []
    await drain(kb)
    assert kb.embeddings._qdrant.count(kb.embeddings._collection).count == 0


async def test_create_kb_attach_detach_deactivate_and_cross_tenant_constraint(kb):
    async with kb.pool.acquire() as conn:
        base = await conn.fetchval(
            "INSERT INTO knowledge_bases(tenant_id,department_id,name) VALUES($1,$2,'Company policies') RETURNING id",
            kb.tenant,
            kb.department,
        )
        await conn.execute(
            "INSERT INTO notes(tenant_id,department_id,knowledge_base_id,title,content) VALUES($1,$2,$3,'Policy','Do not put live accounting transactions in KB.')",
            kb.tenant,
            kb.department,
            base,
        )
        with pytest.raises(Exception):
            await conn.execute(
                "INSERT INTO agent_knowledge_bases(agent_id,tenant_id,knowledge_base_id) VALUES($1,$2,$3)",
                kb.agent,
                kb.other,
                base,
            )
    manifest = kb.manifest.model_copy(update={"knowledgeBaseIds": [base], "knowledge": []})
    await drain(kb)
    assert await kb.service.retrieve(manifest, "policy") == []
    async with kb.pool.acquire() as conn:
        await conn.execute(
            "INSERT INTO agent_knowledge_bases(agent_id,tenant_id,knowledge_base_id) VALUES($1,$2,$3)",
            kb.agent,
            kb.tenant,
            base,
        )
    assert await kb.service.retrieve(manifest, "policy")
    async with kb.pool.acquire() as conn:
        await conn.execute("UPDATE knowledge_bases SET status='disabled' WHERE id=$1", base)
    assert await kb.service.retrieve(manifest, "policy") == []
    await drain(kb)
    async with kb.pool.acquire() as conn:
        await conn.execute("UPDATE knowledge_bases SET status='active' WHERE id=$1", base)
    await drain(kb)
    assert await kb.service.retrieve(manifest, "policy")
    async with kb.pool.acquire() as conn:
        await conn.execute("DELETE FROM agent_knowledge_bases WHERE knowledge_base_id=$1", base)
    assert await kb.service.retrieve(manifest, "policy") == []


async def test_reconciliation_repairs_missing_vectors(kb):
    await drain(kb)
    hits = await kb.service.retrieve(kb.manifest, "policy")
    kb.embeddings.delete_chunks([h["chunk_id"] for h in hits])
    assert await kb.service.retrieve(kb.manifest, "policy") == []
    await kb.service.reconcile()
    async with kb.pool.acquire() as conn:
        assert await conn.fetchval("SELECT count(*) FROM indexing_jobs WHERE status='pending'") > 0
    await drain(kb)
    assert await kb.service.retrieve(kb.manifest, "policy")


@pytest.mark.skipif(
    os.getenv("TEST_REAL_EMBEDDING") != "1",
    reason="Set TEST_REAL_EMBEDDING=1 to load the production encoder",
)
async def test_real_encoder_postgres_qdrant_retrieval(kb):
    from qdrant_client.http import models as qm

    collection = "kb_model_test_" + uuid4().hex
    client = kb.embeddings._qdrant
    previous = kb.embeddings._collection
    client.create_collection(
        collection, vectors_config=qm.VectorParams(size=384, distance=qm.Distance.COSINE)
    )
    try:
        kb.embeddings._collection = collection
        kb.embeddings._dim = 384
        kb.embeddings._model_name = Settings().embedding_model
        kb.embeddings._encoder = None
        await drain(kb)
        hits = await kb.service.retrieve(kb.manifest, "When should we contact overdue customers?")
        assert hits and "30 days" in hits[0]["content"]
    finally:
        kb.embeddings._collection = previous
        client.delete_collection(collection)


async def test_partial_replacement_failure_keeps_old_evidence_and_delete_cleans_orphans(kb):
    await drain(kb)
    original = kb.embeddings.index_chunks

    def partial_then_fail(chunks):
        original(chunks[:1])
        raise RuntimeError("Interrupted after first vector write")

    async with kb.pool.acquire() as conn:
        await conn.execute(
            "UPDATE notes SET content='Replacement policy. ' || repeat('new evidence ',30) WHERE id=$1",
            kb.note,
        )
    kb.embeddings.index_chunks = partial_then_fail
    assert not await kb.service.process_one()
    hits = await kb.service.retrieve(kb.manifest, "policy")
    assert hits and all(h["version"] == 1 for h in hits)
    async with kb.pool.acquire() as conn:
        await conn.execute("DELETE FROM notes WHERE id=$1", kb.note)
    kb.embeddings.index_chunks = original
    await drain(kb)
    assert kb.embeddings._qdrant.count(kb.embeddings._collection).count == 0


async def test_explicit_document_read_cannot_widen_manifest_scope(kb):
    await drain(kb)
    assert await kb.service.retrieve(kb.manifest,'policy',document_id=kb.note)
    async with kb.pool.acquire() as conn:
        hidden=await conn.fetchval("INSERT INTO notes(tenant_id,department_id,title,content) VALUES($1,$2,'Private','Private policy') RETURNING id",kb.tenant,kb.department)
    await drain(kb)
    assert await kb.service.retrieve(kb.manifest,'policy',document_id=hidden)==[]
