"""RecordedLlmService Prometheus metrics — token counters + outcome labels."""

from __future__ import annotations

import pytest

from aiq_agent.llm.base import ChatMessage
from aiq_agent.observability.cost import (
    LLM_CALLS_TOTAL,
    LLM_TOKENS_TOTAL,
    RecordedLlmService,
)

from .fake_llm import FakeLlmService


def _token_count(provider: str, model: str, direction: str) -> float:
    metric = LLM_TOKENS_TOTAL.labels(
        provider=provider, model=model, direction=direction
    )
    return metric._value.get()  # type: ignore[attr-defined]


def _call_count(provider: str, model: str, outcome: str) -> float:
    metric = LLM_CALLS_TOTAL.labels(
        provider=provider, model=model, outcome=outcome
    )
    return metric._value.get()  # type: ignore[attr-defined]


@pytest.mark.asyncio
async def test_records_input_and_output_tokens_on_successful_call() -> None:
    before_in = _token_count("fake", "fake-model", "input")
    before_out = _token_count("fake", "fake-model", "output")
    before_ok = _call_count("fake", "fake-model", "ok")

    fake = FakeLlmService(replies=["Xin chào"])  # produces a usage tuple
    wrapped = RecordedLlmService(fake)
    await wrapped.chat([ChatMessage("user", "hi")])

    # FakeLlmService usage: input=len(messages), output=len(content)
    assert _token_count("fake", "fake-model", "input") == before_in + 1
    assert _token_count("fake", "fake-model", "output") == before_out + len("Xin chào")
    assert _call_count("fake", "fake-model", "ok") == before_ok + 1


@pytest.mark.asyncio
async def test_records_error_outcome_when_inner_raises() -> None:
    class ExplodingLlm(FakeLlmService):
        async def chat(self, *args, **kwargs):  # type: ignore[override]
            raise RuntimeError("boom")

    before_err = _call_count("fake", "fake-model", "error")

    wrapped = RecordedLlmService(ExplodingLlm(replies=[]))
    with pytest.raises(RuntimeError, match="boom"):
        await wrapped.chat([ChatMessage("user", "hi")])

    assert _call_count("fake", "fake-model", "error") == before_err + 1
