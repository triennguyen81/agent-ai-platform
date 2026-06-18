"""Run a multi-step agentic task via the Claude Agent SDK.

POST /agent/run lands here. Each call spawns a short-lived ClaudeSDKClient
session with:
- skills auto-discovered from /app/.claude/skills (the Dockerfile symlinks
  /app/skills there)
- in-process MCP tools bound to the caller's JWT and the gateway base URL
"""

from __future__ import annotations

import logging
from dataclasses import dataclass, field

from claude_agent_sdk import (
    AssistantMessage,
    ClaudeAgentOptions,
    ClaudeSDKClient,
    TextBlock,
    create_sdk_mcp_server,
)

from aiq_agent.agentic.tools import build_tools

logger = logging.getLogger(__name__)


@dataclass(frozen=True, slots=True)
class AgenticStep:
    """One observable thing the agent did during the run."""

    role: str
    text: str


@dataclass(slots=True)
class AgenticRunResult:
    final_answer: str
    steps: list[AgenticStep] = field(default_factory=list)


async def run_task(
    *,
    task: str,
    user_token: str,
    gateway_base: str,
    skills: list[str] | str | None = "all",
    max_turns: int = 8,
) -> AgenticRunResult:
    """Execute `task` agentically.

    Returns the assistant's final text + a list of intermediate text
    blocks (so the UI can render a step-by-step trace).
    """
    tools = build_tools(token=user_token, gateway_base=gateway_base)
    mcp_server = create_sdk_mcp_server(
        name="aiq",
        version="0.1.0",
        tools=tools,
    )

    allowed_tools = [
        # KB / web
        "mcp__aiq__search_notes",
        "mcp__aiq__get_note",
        "mcp__aiq__save_note",
        "mcp__aiq__fetch_url",
        # Accounting Mức 2
        "mcp__aiq__calculate_vat",
        "mcp__aiq__calculate_pit",
        "mcp__aiq__calculate_cit",
        "mcp__aiq__lookup_account",
        "mcp__aiq__check_filing_deadline",
        "mcp__aiq__journal_entry_validator",
        "mcp__aiq__import_excel_ledger",
    ]

    options = ClaudeAgentOptions(
        mcp_servers={"aiq": mcp_server},
        allowed_tools=allowed_tools,
        skills=skills,
        max_turns=max_turns,
        system_prompt=(
            "Bạn là agentic assistant của AIQ. Có quyền dùng tools để search "
            "/ đọc / lưu note vào knowledge base, và fetch URL công khai. "
            "Luôn kết thúc bằng 1 đoạn tổng kết ngắn cho người dùng bằng "
            "tiếng Việt."
        ),
    )

    steps: list[AgenticStep] = []
    final_chunks: list[str] = []

    async with ClaudeSDKClient(options=options) as client:
        await client.query(task)
        async for message in client.receive_response():
            if isinstance(message, AssistantMessage):
                for block in message.content:
                    if isinstance(block, TextBlock):
                        text = (block.text or "").strip()
                        if not text:
                            continue
                        steps.append(AgenticStep(role="assistant", text=text))
                        final_chunks.append(text)

    final_answer = "\n\n".join(final_chunks).strip() or "(Agent không trả lời gì.)"
    return AgenticRunResult(final_answer=final_answer, steps=steps)
