"""M6: top-K RAG via embeddings.

Replaces full-context KB injection (which caps out around 200 notes) with
semantic top-K retrieval. Plain chat falls back to full-context if the
embedding service is unavailable so this is fail-open by design.
"""

from aiq_agent.embeddings.service import EmbeddingService, build_embedding_service

__all__ = ["EmbeddingService", "build_embedding_service"]
