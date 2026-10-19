"""Vietnamese diacritic-aware correction stage.

Wraps the existing `LlmService` so any provider (Qwen3 via Ollama or Claude)
can perform the correction. The system prompt enforces structural fidelity:
the model must preserve newlines and only fix diacritics, OCR garble, and
obvious spacing — no paraphrasing.
"""

from __future__ import annotations

import re
from abc import ABC, abstractmethod

from aiq_agent.llm.base import ChatMessage, LlmService, LlmUsage
from aiq_agent.observability.llm_usage import merge_usage

_CORRECTION_SYSTEM = (
    "You correct Vietnamese OCR output. Rules:\n"
    "1. Preserve every newline and paragraph break exactly.\n"
    "2. Restore Vietnamese diacritics (dấu) where the word is unambiguous.\n"
    "3. Fix obvious OCR garble (rn->m, vv->w, broken word splits).\n"
    "4. Do NOT paraphrase, summarize, translate, or add commentary.\n"
    "5. If a fragment is unrecoverable, keep it verbatim.\n"
    "Reply with ONLY the corrected text, no preamble, no markdown fences."
)

_BATCH_RULE = (
    "\n6. The input holds several pages, each starting with a line <<<PAGE n>>>. Copy every such "
    "line exactly, in the same order, and correct each page's text below its own line."
)
_PAGE_LINE = re.compile(r"^<<<PAGE (\d+)>>>[ \t]*$", re.MULTILINE)


def split_pages(reply: str, expected: list[int]) -> list[str] | None:
    """Corrected text per page, or None when the reply does not carry exactly
    the expected page markers, in order, with nothing before the first one
    and no page left empty (the caller then corrects page by page)."""
    marks = list(_PAGE_LINE.finditer(reply))
    if not marks or [int(m.group(1)) for m in marks] != expected:
        return None
    if reply[: marks[0].start()].strip():
        return None
    pages = []
    for i, mark in enumerate(marks):
        end = marks[i + 1].start() if i + 1 < len(marks) else len(reply)
        page = reply[mark.end():end].strip("\n")
        if not page.strip():
            return None
        pages.append(page)
    return pages


class TextCorrector(ABC):
    @abstractmethod
    async def correct(self, text: str) -> str:
        ...

    async def correct_with_usage(self, text: str) -> tuple[str, LlmUsage | None]:
        """Corrected text plus the LLM usage it cost (None = no LLM call)."""
        return await self.correct(text), None

    async def correct_many_with_usage(self, texts: list[str]) -> tuple[list[str], LlmUsage | None]:
        """Correct several pages; default is one call per page."""
        results = [await self.correct_with_usage(text) for text in texts]
        return [text for text, _ in results], merge_usage([usage for _, usage in results])


class NoopCorrector(TextCorrector):
    async def correct(self, text: str) -> str:
        return text


class DiacriticCorrector(TextCorrector):
    """LLM-backed corrector. Skips empty pages to save tokens."""

    def __init__(self, llm: LlmService, *, temperature: float = 0.1) -> None:
        self._llm = llm
        self._temperature = temperature

    async def correct(self, text: str) -> str:
        corrected, _ = await self.correct_with_usage(text)
        return corrected

    async def correct_with_usage(self, text: str) -> tuple[str, LlmUsage | None]:
        stripped = text.strip()
        if not stripped:
            return text, None

        result = await self._llm.chat(
            [
                ChatMessage("system", _CORRECTION_SYSTEM),
                ChatMessage("user", text),
            ],
            temperature=self._temperature,
        )
        return result.content.strip(), result.usage

    async def correct_many_with_usage(self, texts: list[str]) -> tuple[list[str], LlmUsage | None]:
        """One LLM call for several pages, delimited by <<<PAGE n>>> lines.

        Empty pages are not sent. If the reply does not keep every page
        marker in order, that batch falls back to one call per page — never
        a guessed page split.
        """
        indexed = [(i, t) for i, t in enumerate(texts) if t.strip()]
        if len(indexed) <= 1:
            return await super().correct_many_with_usage(texts)
        numbers = [i + 1 for i, _ in indexed]
        body = "\n".join(f"<<<PAGE {n}>>>\n{t}" for n, (_, t) in zip(numbers, indexed, strict=True))
        result = await self._llm.chat(
            [ChatMessage("system", _CORRECTION_SYSTEM + _BATCH_RULE), ChatMessage("user", body)],
            temperature=self._temperature,
        )
        split = split_pages(result.content.strip(), numbers)
        if split is None:
            fallback, fallback_usage = await super().correct_many_with_usage(texts)
            return fallback, merge_usage([result.usage, fallback_usage])
        out = list(texts)
        for (i, _), corrected in zip(indexed, split, strict=True):
            out[i] = corrected
        return out, result.usage
