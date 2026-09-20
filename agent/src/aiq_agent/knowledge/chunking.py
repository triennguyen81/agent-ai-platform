"""Deterministic, bounded chunks; offsets refer to normalized page text."""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass
from uuid import UUID, uuid5

NAMESPACE = UUID("13e85adb-5774-4e3e-bf28-cc90dd689d5c")


@dataclass(frozen=True)
class Chunk:
    id: str
    index: int
    content: str
    metadata: dict


def normalize(text: str) -> str:
    return unicodedata.normalize("NFC", text.replace("\r\n", "\n").replace("\r", "\n")).strip()


def chunk_text(version_id: str, text: str, *, size: int = 800, overlap: int = 120) -> list[Chunk]:
    if not 64 <= size <= 8000 or not 0 <= overlap < size:
        raise ValueError("Require 64 <= chunk size <= 8000 and 0 <= overlap < size")
    chunks = []
    for page_number, page in enumerate(text.split("\f"), 1):
        page = normalize(page)
        headings = [
            (m.start(), m.group(1)[:300]) for m in re.finditer(r"^#{1,6}\s+(.+)$", page, re.M)
        ]
        start = 0
        while start < len(page):
            end = min(start + size, len(page))
            section = next(
                (heading for offset, heading in reversed(headings) if offset <= start), None
            )
            content = page[start:end]
            if content.strip():
                key = f"{version_id}:{size}:{overlap}:{page_number}:{start}:{end}"
                chunks.append(
                    Chunk(
                        str(uuid5(NAMESPACE, key)),
                        len(chunks),
                        content,
                        {
                            "page": page_number,
                            "section": section,
                            "start": start,
                            "end": end,
                            "chunk_size": size,
                            "overlap": overlap,
                        },
                    )
                )
            if end == len(page):
                break
            start = end - overlap
    return chunks
