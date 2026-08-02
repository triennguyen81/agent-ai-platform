"""Claude provider via the local `claude` CLI subprocess — subscription auth.

Uses `ClaudeSDKClient` from `claude-agent-sdk`, which spawns the `claude`
CLI binary and talks to it over stdio. If the CLI has been logged in
with `claude login` using a Max/Pro subscription, no API key is needed —
requests are billed against the subscription instead of pay-per-token.

Trade-offs vs the direct Anthropic HTTPS provider:
- $0 API cost, but rate-limited to subscription quota
  (Max ~50 msg/5h, Pro ~40 msg/3h — do not use for multi-user production)
- OAuth token lives in /home/app/.claude — must be persisted via a
  volume mount so `docker compose up` restarts don't wipe login state
- No structured token usage returned (subscription is metered by
  message count, not tokens) — LlmUsage always reports 0

The mock token counts keep the `LlmService` contract intact without
misleading downstream observability.
"""

from __future__ import annotations

import logging
from collections.abc import Sequence

from claude_agent_sdk import (
    AssistantMessage,
    ClaudeAgentOptions,
    ClaudeSDKClient,
    TextBlock,
)

from aiq_agent.llm.base import ChatMessage, ChatResult, LlmService, LlmUsage

logger = logging.getLogger(__name__)


class ClaudeCliLlmService(LlmService):
    """LlmService that reuses Claude Code CLI subscription auth."""

    provider = "claude_cli"

    def __init__(self, *, model: str, max_turns: int = 4) -> None:
        self.model = model
        self._max_turns = max_turns

    async def chat(
        self,
        messages: Sequence[ChatMessage],
        *,
        temperature: float = 0.2,
        max_tokens: int | None = None,
    ) -> ChatResult:
        # ClaudeAgentOptions accepts a single system_prompt string, and one
        # user query per session. Fold multi-turn history into the query
        # by prefixing prior turns; keep the last user message as the ask.
        system_parts = [m.content for m in messages if m.role == "system"]
        system_prompt = "\n\n".join(p for p in system_parts if p) or None

        history: list[str] = []
        last_user = ""
        for m in messages:
            if m.role == "user":
                if last_user:
                    history.append(f"[Người dùng]: {last_user}")
                last_user = m.content
            elif m.role == "assistant":
                history.append(f"[Trợ lý]: {m.content}")

        if history:
            query = "\n\n".join(history) + f"\n\n[Người dùng]: {last_user}"
        else:
            query = last_user or "(empty)"

        options = ClaudeAgentOptions(
            system_prompt=system_prompt,
            max_turns=self._max_turns,
        )

        chunks: list[str] = []
        async with ClaudeSDKClient(options=options) as client:
            await client.query(query)
            async for message in client.receive_response():
                if isinstance(message, AssistantMessage):
                    for block in message.content:
                        if isinstance(block, TextBlock):
                            text = (block.text or "").strip()
                            if text:
                                chunks.append(text)

        content = "\n\n".join(chunks).strip()

        # Subscription auth is billed per message, not per token — we
        # can't return meaningful usage counts.
        usage = LlmUsage(input_tokens=0, output_tokens=0)

        return ChatResult(
            content=content or "(Claude không trả nội dung.)",
            model=self.model,
            provider=self.provider,
            usage=usage,
        )

    async def aclose(self) -> None:
        # No persistent client to close — each chat() spawns a fresh
        # short-lived ClaudeSDKClient session.
        return None
