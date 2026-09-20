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

from aiq_agent.agentic.accounting_tools import build_accounting_tools
from aiq_agent.agentic.contract_tools import build_contract_tools
from aiq_agent.agentic.registry import fetch_agent_model, fetch_allowed_tool_codes
from aiq_agent.agentic.report_tools import build_report_tools
from aiq_agent.agentic.tools import build_tools
from aiq_agent.agentic.voucher_tools import build_voucher_tools

logger = logging.getLogger(__name__)

# Fallback list for callers that don't supply an agent_id (chat, notes, etc.).
# Once those callers migrate to the agent registry, this constant can shrink
# to just the always-on/system tools.
DEFAULT_ALLOWED_TOOLS = [
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
    # Contract module Phase 1
    "mcp__aiq__extract_contract_fields",
    "mcp__aiq__save_contract",
    # Contract module Phase 2
    "mcp__aiq__get_contract",
    "mcp__aiq__save_checklist_items",
    # Voucher module Phase 1
    "mcp__aiq__extract_voucher_fields",
    "mcp__aiq__save_voucher",
    "mcp__aiq__verify_einvoice_tct",
    "mcp__aiq__suggest_journal_entry",
    "mcp__aiq__save_journal_suggestions",
    # Report/query tools — customer/supplier/AR/AP/voucher-list read access
    # for natural-language accounting questions (never mutates anything).
    "mcp__aiq__list_customers",
    "mcp__aiq__get_customer_receivables",
    "mcp__aiq__list_suppliers",
    "mcp__aiq__get_supplier_payables",
    "mcp__aiq__get_ap_summary",
    "mcp__aiq__list_vouchers",
    "mcp__aiq__get_weekly_summary_report",
]


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
    agent_id: str | None = None,
    model: str | None = None,
) -> AgenticRunResult:
    """Execute `task` agentically.

    When `agent_id` is supplied:
    - allowed tool list is fetched from the governance registry
    - the per-agent `config.model` override is fetched and passed to the
      Claude SDK (lets e.g. bsn-ma-advisor run on Sonnet 4.6 while
      bsn-assistant stays on Haiku 4.5)

    Explicit `model` param takes precedence over the per-agent override.
    When neither is set, the CLI default applies.

    Returns the assistant's final text + a list of intermediate text
    blocks (so the UI can render a step-by-step trace).
    """
    # Same fix as runtime/executors.py's _installed_tools: this only ever
    # built the generic notes/URL tools, so calculate_vat/save_contract/
    # suggest_journal_entry/etc. — all listed in DEFAULT_ALLOWED_TOOLS below
    # as if usable — were never actually registered on the MCP server.
    tools = [
        *build_tools(token=user_token, gateway_base=gateway_base),
        *build_contract_tools(token=user_token, gateway_base=gateway_base),
        *build_voucher_tools(token=user_token, gateway_base=gateway_base),
        *build_accounting_tools(token=user_token, gateway_base=gateway_base),
        *build_report_tools(token=user_token, gateway_base=gateway_base),
    ]
    mcp_server = create_sdk_mcp_server(
        name="aiq",
        version="0.1.0",
        tools=tools,
    )

    allowed_tools: list[str] = list(DEFAULT_ALLOWED_TOOLS)
    resolved_model = model
    if agent_id:
        registry_codes = await fetch_allowed_tool_codes(
            gateway_base=gateway_base,
            user_token=user_token,
            agent_id=agent_id,
        )
        if registry_codes is not None:
            allowed_tools = registry_codes
            logger.info(
                "agent %s: using registry-supplied tool list (%d tools)",
                agent_id,
                len(registry_codes),
            )
        else:
            raise PermissionError(
                f"Tool authorization lookup failed for agent {agent_id}; refusing fail-open execution"
            )

        if resolved_model is None:
            fetched_model = await fetch_agent_model(
                gateway_base=gateway_base,
                user_token=user_token,
                agent_id=agent_id,
            )
            if fetched_model:
                resolved_model = fetched_model
                logger.info("agent %s: using registered model %s", agent_id, fetched_model)

    options = ClaudeAgentOptions(
        mcp_servers={"aiq": mcp_server},
        allowed_tools=allowed_tools,
        skills=skills,
        max_turns=max_turns,
        model=resolved_model,
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
