"""LLM cost tracking — exposes token counters via Prometheus.

The Counter is split by direction (input vs output) so cost can be derived
from per-model price sheets. Latency is observed in a Histogram, which also
gives Prometheus a `_count` rate it can multiply by mean tokens-per-call when
the histogram is unavailable.
"""

from __future__ import annotations

import time
from collections.abc import Sequence

from prometheus_client import Counter, Histogram

from aiq_agent.llm.base import ChatMessage, ChatResult, LlmService

LLM_TOKENS_TOTAL = Counter(
    "aiq_llm_tokens_total",
    "Tokens consumed per LLM call, broken down by provider/model/direction.",
    labelnames=("provider", "model", "direction"),
)

LLM_CALLS_TOTAL = Counter(
    "aiq_llm_calls_total",
    "Number of LLM chat invocations.",
    labelnames=("provider", "model", "outcome"),
)

LLM_CALL_DURATION_SECONDS = Histogram(
    "aiq_llm_call_duration_seconds",
    "Wall-clock duration of a single LLM chat call.",
    labelnames=("provider", "model"),
    buckets=(0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300),
)


class RecordedLlmService(LlmService):
    """Wraps any LlmService and emits Prometheus metrics for each call."""

    def __init__(self, inner: LlmService) -> None:
        self._inner = inner
        self.provider = inner.provider
        self.model = inner.model

    async def chat(
        self,
        messages: Sequence[ChatMessage],
        *,
        temperature: float = 0.2,
        max_tokens: int | None = None,
    ) -> ChatResult:
        start = time.perf_counter()
        try:
            result = await self._inner.chat(
                messages, temperature=temperature, max_tokens=max_tokens
            )
        except Exception:
            LLM_CALLS_TOTAL.labels(
                provider=self.provider, model=self.model, outcome="error"
            ).inc()
            raise
        finally:
            LLM_CALL_DURATION_SECONDS.labels(
                provider=self.provider, model=self.model
            ).observe(time.perf_counter() - start)

        LLM_CALLS_TOTAL.labels(
            provider=result.provider, model=result.model, outcome="ok"
        ).inc()

        if result.usage is not None:
            LLM_TOKENS_TOTAL.labels(
                provider=result.provider, model=result.model, direction="input"
            ).inc(result.usage.input_tokens)
            LLM_TOKENS_TOTAL.labels(
                provider=result.provider, model=result.model, direction="output"
            ).inc(result.usage.output_tokens)
            # Disjoint prompt classes (never summed into "input").
            LLM_TOKENS_TOTAL.labels(
                provider=result.provider, model=result.model, direction="cache_creation"
            ).inc(result.usage.cache_creation_input_tokens)
            LLM_TOKENS_TOTAL.labels(
                provider=result.provider, model=result.model, direction="cache_read"
            ).inc(result.usage.cache_read_input_tokens)

        return result

    async def aclose(self) -> None:
        await self._inner.aclose()
