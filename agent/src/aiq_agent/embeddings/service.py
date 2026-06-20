"""Embedding service: self-hosted sentence-transformer via fastembed +
Qdrant top-K retrieval.

Lazy-upsert pattern:
- Each note has a content hash (sha256 of title + content)
- On query, we receive the full list of notes from the caller
- For any note whose hash differs from what Qdrant has, we re-embed + upsert
- Then we semantic-search top-K for the prompt, filtered by user_id
"""

from __future__ import annotations

import hashlib
import logging
import uuid
from dataclasses import dataclass
from typing import Any

logger = logging.getLogger(__name__)

# Stable namespace for hashing arbitrary note_id strings into UUIDs that
# Qdrant accepts as point IDs. Don't change this — it would invalidate
# all existing vectors in the collection.
_NOTE_ID_NAMESPACE = uuid.UUID("00000000-1111-2222-3333-444455556666")


def _note_id_to_point_id(note_id: str) -> str:
    """Map any note_id string to a deterministic UUID Qdrant will accept."""
    try:
        # If note_id already is a UUID, keep it (real notes from gateway).
        return str(uuid.UUID(note_id))
    except ValueError:
        return str(uuid.uuid5(_NOTE_ID_NAMESPACE, note_id))


@dataclass(frozen=True, slots=True)
class IngestNote:
    """A note as the embedding service sees it for upsert."""

    note_id: str
    user_id: str
    title: str
    content: str

    @property
    def hash(self) -> str:
        return hashlib.sha256(
            f"{self.title}\n\n{self.content}".encode("utf-8")
        ).hexdigest()[:32]

    @property
    def embed_text(self) -> str:
        # e5 family expects "passage: {text}" on indexing.
        return f"passage: {self.title}\n\n{self.content[:8000]}"


@dataclass(frozen=True, slots=True)
class TopKHit:
    note_id: str
    score: float


class EmbeddingService:
    """Wraps fastembed + qdrant_client. Methods are async-safe by virtue
    of being called from FastAPI executor (the underlying libs are sync).
    """

    def __init__(
        self,
        *,
        model_name: str,
        embedding_dim: int,
        qdrant_url: str,
        collection: str,
        qdrant_api_key: str | None = None,
    ) -> None:
        self._model_name = model_name
        self._dim = embedding_dim
        self._qdrant_url = qdrant_url
        self._qdrant_api_key = qdrant_api_key
        self._collection = collection
        self._encoder: Any = None  # fastembed.TextEmbedding lazy init
        self._qdrant: Any = None  # qdrant_client.QdrantClient lazy init

    def _ensure_encoder(self) -> Any:
        if self._encoder is None:
            from fastembed import TextEmbedding  # lazy import

            logger.info("Loading fastembed model %s", self._model_name)
            self._encoder = TextEmbedding(model_name=self._model_name)
        return self._encoder

    def _ensure_qdrant(self) -> Any:
        if self._qdrant is None:
            from qdrant_client import QdrantClient  # lazy import
            from qdrant_client.http import models as qmodels

            client = QdrantClient(
                url=self._qdrant_url,
                api_key=self._qdrant_api_key,
                timeout=10,
                # Server is 1.12, client is 1.18 — minor mismatch is fine.
                check_compatibility=False,
            )
            existing = {c.name for c in client.get_collections().collections}
            if self._collection not in existing:
                logger.info(
                    "Creating Qdrant collection %s (dim=%d)",
                    self._collection,
                    self._dim,
                )
                client.create_collection(
                    collection_name=self._collection,
                    vectors_config=qmodels.VectorParams(
                        size=self._dim,
                        distance=qmodels.Distance.COSINE,
                    ),
                )
                # Index on user_id for fast filter
                client.create_payload_index(
                    collection_name=self._collection,
                    field_name="user_id",
                    field_schema=qmodels.PayloadSchemaType.KEYWORD,
                )
            self._qdrant = client
        return self._qdrant

    def _embed_one(self, text: str) -> list[float]:
        encoder = self._ensure_encoder()
        vectors = list(encoder.embed([text]))
        if not vectors:
            raise RuntimeError("Embedder returned no vectors")
        return list(vectors[0].tolist() if hasattr(vectors[0], "tolist") else vectors[0])

    def _embed_many(self, texts: list[str]) -> list[list[float]]:
        if not texts:
            return []
        encoder = self._ensure_encoder()
        vectors = list(encoder.embed(texts))
        return [
            list(v.tolist() if hasattr(v, "tolist") else v) for v in vectors
        ]

    def sync_notes(self, notes: list[IngestNote]) -> int:
        """Upsert any notes whose hash differs from what Qdrant has.

        Returns the number of vectors re-embedded.
        """
        if not notes:
            return 0

        from qdrant_client.http import models as qmodels

        client = self._ensure_qdrant()

        # Fetch current state for these note ids — Qdrant only accepts
        # UUID or uint for point IDs, so map note_id strings to UUIDs.
        point_ids = [_note_id_to_point_id(n.note_id) for n in notes]
        existing = client.retrieve(
            collection_name=self._collection,
            ids=point_ids,
            with_payload=True,
            with_vectors=False,
        )
        existing_hashes: dict[str, str] = {
            str((point.payload or {}).get("note_id", "")): str(
                (point.payload or {}).get("hash", "")
            )
            for point in existing
        }

        # Pick changed/new notes
        to_embed = [n for n in notes if existing_hashes.get(n.note_id) != n.hash]
        if not to_embed:
            return 0

        logger.info("Embedding %d notes (out of %d total)", len(to_embed), len(notes))
        vectors = self._embed_many([n.embed_text for n in to_embed])

        points = [
            qmodels.PointStruct(
                id=_note_id_to_point_id(note.note_id),
                vector=vector,
                payload={
                    "user_id": note.user_id,
                    "note_id": note.note_id,
                    "title": note.title,
                    "hash": note.hash,
                },
            )
            for note, vector in zip(to_embed, vectors, strict=True)
        ]
        client.upsert(collection_name=self._collection, points=points, wait=True)
        return len(points)

    def search(
        self,
        *,
        query: str,
        user_id: str,
        k: int,
    ) -> list[TopKHit]:
        """Return top-K nearest note ids for this user."""
        from qdrant_client.http import models as qmodels

        client = self._ensure_qdrant()
        # e5 family expects "query: {text}" prefix at search time.
        vector = self._embed_one(f"query: {query}")
        hits = client.search(
            collection_name=self._collection,
            query_vector=vector,
            query_filter=qmodels.Filter(
                must=[
                    qmodels.FieldCondition(
                        key="user_id",
                        match=qmodels.MatchValue(value=user_id),
                    )
                ]
            ),
            limit=k,
            with_payload=True,
        )
        # Use the original note_id from payload — the point.id is a UUID,
        # but the caller wants the original string id from gateway.
        return [
            TopKHit(
                note_id=str((h.payload or {}).get("note_id", h.id)),
                score=float(h.score),
            )
            for h in hits
        ]


def build_embedding_service(
    *,
    model_name: str,
    embedding_dim: int,
    qdrant_url: str,
    collection: str,
    qdrant_api_key: str | None = None,
) -> EmbeddingService:
    return EmbeddingService(
        model_name=model_name,
        embedding_dim=embedding_dim,
        qdrant_url=qdrant_url,
        qdrant_api_key=qdrant_api_key,
        collection=collection,
    )
