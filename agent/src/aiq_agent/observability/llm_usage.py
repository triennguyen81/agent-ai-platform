"""Per-request LLM usage collection for Claude CLI / Agent SDK sessions.

One request may make several API calls (tool rounds). The SDK streams one
AssistantMessage per content block, several sharing the same API message id,
so calls are counted by DISTINCT message id. Token totals and cost come from
the session's ResultMessage (the CLI's own accounting, already priced per
token class: uncached input, cache write, cache read, output) — never
re-derived here, so cache tokens are not double counted or double priced.
Only numbers and model codes are kept — never message content.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from aiq_agent.llm.base import LlmUsage

COST_SOURCE_CLAUDE_CLI = "claude_cli_total_cost_usd"


@dataclass(slots=True)
class UsageCollector:
    message_ids: set[str] = field(default_factory=set)
    models: set[str] = field(default_factory=set)
    anonymous_calls: int = 0
    num_turns: int | None = None
    usage: dict[str, Any] | None = None
    cost_usd: float | None = None

    def add_assistant(self, message: Any) -> None:
        message_id = getattr(message, "message_id", None)
        if message_id:
            self.message_ids.add(message_id)
        elif getattr(message, "usage", None):
            self.anonymous_calls += 1
        model = getattr(message, "model", None)
        if model:
            self.models.add(str(model))

    def add_result(self, message: Any) -> None:
        self.usage = getattr(message, "usage", None) or None
        self.cost_usd = getattr(message, "total_cost_usd", None)
        self.num_turns = getattr(message, "num_turns", None)

    @property
    def llm_call_count(self) -> int:
        counted = len(self.message_ids) + self.anonymous_calls
        # A result without any observed assistant message still made a call.
        return counted or (1 if self.usage is not None else 0)

    def _token(self, key: str) -> int | None:
        if self.usage is None:
            return None
        value = self.usage.get(key)
        return int(value) if isinstance(value, (int, float)) else 0

    @property
    def input_tokens(self) -> int | None:
        return self._token("input_tokens")

    @property
    def output_tokens(self) -> int | None:
        return self._token("output_tokens")

    @property
    def cache_creation_input_tokens(self) -> int | None:
        return self._token("cache_creation_input_tokens")

    @property
    def cache_read_input_tokens(self) -> int | None:
        return self._token("cache_read_input_tokens")

    @property
    def model(self) -> str | None:
        return (
            sorted(self.models)[0]
            if len(self.models) == 1
            else (",".join(sorted(self.models)) or None)
        )

    def to_llm_usage(self) -> LlmUsage | None:
        if self.usage is None:
            return None
        return LlmUsage(
            input_tokens=self.input_tokens or 0,
            output_tokens=self.output_tokens or 0,
            cache_creation_input_tokens=self.cache_creation_input_tokens or 0,
            cache_read_input_tokens=self.cache_read_input_tokens or 0,
            llm_call_count=self.llm_call_count,
            cost_usd=self.cost_usd,
        )


def merge_usage(items: list[LlmUsage | None]) -> LlmUsage | None:
    present = [u for u in items if u is not None]
    if not present:
        return None
    costs = [u.cost_usd for u in present if u.cost_usd is not None]
    return LlmUsage(
        input_tokens=sum(u.input_tokens for u in present),
        output_tokens=sum(u.output_tokens for u in present),
        cache_creation_input_tokens=sum(u.cache_creation_input_tokens for u in present),
        cache_read_input_tokens=sum(u.cache_read_input_tokens for u in present),
        llm_call_count=sum(u.llm_call_count for u in present),
        cost_usd=sum(costs) if costs else None,
    )
