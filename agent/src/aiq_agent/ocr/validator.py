"""Validation stage — compare corrected text against the reference corpus.

The pgvector implementation is wired but optional; the default
PassthroughValidator returns a non-blocking report so the pipeline runs end-
to-end even when Postgres is not reachable (useful for unit tests and the
first OCR smoke run).
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import Sequence
from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class ValidationReport:
    page_number: int
    similarity: float | None
    notes: str | None = None


class OcrValidator(ABC):
    @abstractmethod
    async def validate(self, page_texts: Sequence[str]) -> list[ValidationReport]:
        ...

    async def aclose(self) -> None:
        return None


class PassthroughValidator(OcrValidator):
    """No-op validator. Returns one report per page with no similarity score."""

    async def validate(self, page_texts: Sequence[str]) -> list[ValidationReport]:
        return [
            ValidationReport(
                page_number=index + 1,
                similarity=None,
                notes="passthrough",
            )
            for index in range(len(page_texts))
        ]


class PgVectorValidator(OcrValidator):
    """Look up nearest reference embedding in pgvector and report similarity.

    Lazy-imports asyncpg and pgvector. Requires the corrected text to be
    embedded by `embed_fn` before lookup.
    """

    def __init__(
        self,
        *,
        dsn: str,
        embed_fn,  # async callable: str -> list[float]
        top_k: int = 1,
        match_threshold: float = 0.85,
    ) -> None:
        self._dsn = dsn
        self._embed_fn = embed_fn
        self._top_k = top_k
        self._match_threshold = match_threshold
        self._pool = None

    async def _ensure_pool(self):
        if self._pool is not None:
            return self._pool
        try:
            import asyncpg  # type: ignore[import-not-found]
        except ImportError as exc:
            raise RuntimeError(
                "asyncpg is not installed. Install pgvector extras: "
                "`pip install '.[pgvector]'`"
            ) from exc

        self._pool = await asyncpg.create_pool(self._dsn, min_size=1, max_size=4)
        return self._pool

    async def validate(self, page_texts: Sequence[str]) -> list[ValidationReport]:
        pool = await self._ensure_pool()
        reports: list[ValidationReport] = []

        async with pool.acquire() as conn:
            for index, text in enumerate(page_texts):
                stripped = text.strip()
                if not stripped:
                    reports.append(ValidationReport(index + 1, None, "empty page"))
                    continue

                embedding = await self._embed_fn(stripped)
                row = await conn.fetchrow(
                    """
                    SELECT 1 - (embedding <=> $1::vector) AS similarity
                    FROM embeddings
                    WHERE source_type = 'reference_corpus'
                    ORDER BY embedding <=> $1::vector
                    LIMIT 1;
                    """,
                    embedding,
                )

                if row is None:
                    reports.append(
                        ValidationReport(index + 1, None, "no reference embeddings")
                    )
                    continue

                similarity = float(row["similarity"])
                note = (
                    "match"
                    if similarity >= self._match_threshold
                    else f"below threshold {self._match_threshold:.2f}"
                )
                reports.append(ValidationReport(index + 1, similarity, note))

        return reports

    async def aclose(self) -> None:
        if self._pool is not None:
            await self._pool.close()
            self._pool = None
