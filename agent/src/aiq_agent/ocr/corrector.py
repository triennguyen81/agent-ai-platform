"""Vietnamese diacritic-aware correction stage.

Wraps the existing `LlmService` so any provider (Qwen3 via Ollama or Claude)
can perform the correction. The system prompt enforces structural fidelity:
the model must preserve newlines and only fix diacritics, OCR garble, and
obvious spacing — no paraphrasing.
"""

from __future__ import annotations

from abc import ABC, abstractmethod

from aiq_agent.llm.base import ChatMessage, LlmService

_CORRECTION_SYSTEM = (
    "You correct Vietnamese OCR output. Rules:\n"
    "1. Preserve every newline and paragraph break exactly.\n"
    "2. Restore Vietnamese diacritics (dấu) where the word is unambiguous.\n"
    "3. Fix obvious OCR garble (rn->m, vv->w, broken word splits).\n"
    "4. Do NOT paraphrase, summarize, translate, or add commentary.\n"
    "5. If a fragment is unrecoverable, keep it verbatim.\n"
    "Reply with ONLY the corrected text, no preamble, no markdown fences."
)


class TextCorrector(ABC):
    @abstractmethod
    async def correct(self, text: str) -> str:
        ...


class NoopCorrector(TextCorrector):
    async def correct(self, text: str) -> str:
        return text


class DiacriticCorrector(TextCorrector):
    """LLM-backed corrector. Skips empty pages to save tokens."""

    def __init__(self, llm: LlmService, *, temperature: float = 0.1) -> None:
        self._llm = llm
        self._temperature = temperature

    async def correct(self, text: str) -> str:
        stripped = text.strip()
        if not stripped:
            return text

        result = await self._llm.chat(
            [
                ChatMessage("system", _CORRECTION_SYSTEM),
                ChatMessage("user", text),
            ],
            temperature=self._temperature,
        )
        return result.content.strip()
