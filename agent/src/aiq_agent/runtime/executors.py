"""Manifest-driven provider executors for dynamic agents."""

from __future__ import annotations

import asyncio
import dataclasses
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Any

import httpx
from claude_agent_sdk import (
    AssistantMessage,
    ClaudeAgentOptions,
    ClaudeSDKClient,
    ResultMessage,
    TextBlock,
    create_sdk_mcp_server,
    tool,
)

from aiq_agent.agentic.tools import build_tools
from aiq_agent.config import Settings
from aiq_agent.llm import ChatMessage
from aiq_agent.llm.ollama import OllamaLlmService
from aiq_agent.runtime.manifest import ExecutionManifest, ManifestLookupError, fetch_delegate_manifest
from aiq_agent.skills import SkillRegistry


class ProviderResolutionError(PermissionError):
    pass


class ExecutionTimeoutError(TimeoutError):
    """Execution exceeded the manifest's own configured timeoutSeconds."""


@dataclass(frozen=True, slots=True)
class ExecutionStep:
    role: str
    text: str


@dataclass(frozen=True, slots=True)
class ExecutionResult:
    final_answer: str
    provider: str
    model: str
    steps: list[ExecutionStep] = field(default_factory=list)
    # Phase 5E usage/cost/audit fields. All best-effort: None/[] when the
    # provider genuinely doesn't expose the figure (e.g. Ollama has no
    # per-token cost model) rather than a fabricated value. Only tool CODES
    # and specialist SLUGS are recorded here — never call arguments, tool
    # output content, or prompt/knowledge text.
    input_tokens: int | None = None
    output_tokens: int | None = None
    estimated_cost_usd: float | None = None
    tools_used: list[str] = field(default_factory=list)
    delegations: list[str] = field(default_factory=list)
    workflow_id: str | None = None


def _knowledge_block(manifest: ExecutionManifest) -> str:
    if not manifest.knowledge:
        return ""
    notes = "\n\n".join(
        f"===== NOTE {item.noteId}: {item.title} =====\n{item.content}"
        for item in manifest.knowledge
    )
    return f"===== AUTHORIZED TENANT KNOWLEDGE =====\n{notes}\n===== END AUTHORIZED KNOWLEDGE ====="


def _resolve_skills(manifest: ExecutionManifest, registry: SkillRegistry) -> tuple[list[str], str]:
    requested = list(dict.fromkeys(manifest.skills))
    installed = {skill.name: skill for skill in registry.skills}
    missing = [name for name in requested if name not in installed]
    if missing:
        raise ProviderResolutionError(f"Manifest skills are not installed: {', '.join(missing)}")
    block = "\n\n".join(
        f"===== SKILL: {name} =====\n{installed[name].body}" for name in requested
    )
    return requested, block


# Kept in sync with Gateway's WorkflowStepTypes.Known (belt-and-suspenders —
# Gateway already rejects unknown types at create/update/publish time, but a
# manifest is a separate read path and must not trust that check happened).
_WORKFLOW_STEP_TYPES = {"prompt", "require_knowledge", "require_tool"}


def _resolve_workflow(manifest: ExecutionManifest) -> str:
    """Validates and renders the manifest's optional, database-driven
    workflow. No workflow (the common case) is a no-op — unchanged
    single-turn execution. An unknown step type, or a require_* gate the
    manifest doesn't actually satisfy, fails closed rather than silently
    skipping the step (same posture as _resolve_tools/_resolve_skills).
    """
    if manifest.workflow is None:
        return ""
    steps = manifest.workflow.definition.get("steps")
    if not isinstance(steps, list):
        raise ProviderResolutionError("Workflow definition is missing a 'steps' list")

    granted_tool_codes = {tool.code for tool in manifest.tools}
    prompt_lines: list[str] = []
    for index, step in enumerate(steps):
        if not isinstance(step, dict):
            raise ProviderResolutionError(f"Workflow step {index} is not an object")
        step_type = step.get("type")
        if step_type not in _WORKFLOW_STEP_TYPES:
            raise ProviderResolutionError(f"Workflow step {index} has unknown type: {step_type!r}")
        if step_type == "prompt":
            text = str(step.get("text", "")).strip()
            if text:
                prompt_lines.append(f"{index + 1}. {text}")
        elif step_type == "require_knowledge":
            if not manifest.knowledge:
                raise ProviderResolutionError(
                    f"Workflow step {index} requires attached knowledge but none is authorized for this tenant"
                )
        elif step_type == "require_tool":
            tool_code = step.get("tool")
            if tool_code not in granted_tool_codes:
                raise ProviderResolutionError(
                    f"Workflow step {index} requires granted tool {tool_code!r}, which is not in this agent's manifest"
                )

    if not prompt_lines:
        return ""
    steps_block = "\n".join(prompt_lines)
    return (
        f"===== WORKFLOW ({manifest.workflow.name}) =====\n"
        f"Follow these steps in order:\n{steps_block}\n"
        f"===== END WORKFLOW ====="
    )


def _wrap_tool_with_hitl_gate(tool_obj: Any, *, agent_id: str, token: str, gateway_base: str, used_sink: list[str] | None = None) -> Any:
    """Phase 5D: intercepts every granted-tool call and asks Gateway whether
    any of this (tenant, agent)'s active HITL gates match it, BEFORE the
    real handler ever runs. Read-only/no-gate calls are unaffected (Gateway
    returns "allowed" immediately, no DB write). A matching gate blocks
    execution this turn — either because it's newly pending, or because a
    human already rejected it — the real handler is only ever invoked on an
    "allowed" outcome, so a denied or still-pending action truly cannot run.
    """
    tool_code = f"mcp__aiq__{tool_obj.name}"
    original_handler = tool_obj.handler

    async def gated_handler(args: dict[str, Any]) -> dict[str, Any]:
        try:
            async with httpx.AsyncClient(timeout=10) as client:
                res = await client.post(
                    f"{gateway_base.rstrip('/')}/runtime/agents/{agent_id}/hitl/evaluate",
                    headers={"Authorization": f"Bearer {token}"},
                    json={"toolCode": tool_code, "payload": args},
                )
        except httpx.RequestError as exc:
            return {"content": [{"type": "text", "text": f"HITL policy check unavailable ({exc}); action blocked."}]}
        if res.status_code != 200:
            return {"content": [{"type": "text", "text": f"HITL policy check failed ({res.status_code}); action blocked."}]}
        body = res.json()
        outcome = body.get("outcome")
        if outcome == "rejected":
            return {"content": [{"type": "text", "text": (
                f"This action was reviewed and REJECTED by a human approver "
                f"(gate: {body.get('gateName')}). It has not been executed."
            )}]}
        if outcome == "pending":
            return {"content": [{"type": "text", "text": (
                f"This action requires human approval before it can run "
                f"(gate: {body.get('gateName')}, request {body.get('actionId')}). "
                "It has NOT been executed. Ask an approver to review it, then retry."
            )}]}
        if used_sink is not None:
            used_sink.append(tool_code)
        return await original_handler(args)

    return dataclasses.replace(tool_obj, handler=gated_handler)


def _installed_tools(token: str, gateway_base: str) -> tuple[list[Any], dict[str, Any]]:
    tools = build_tools(token=token, gateway_base=gateway_base)
    indexed = {f"mcp__aiq__{tool.name}": tool for tool in tools}
    return tools, indexed


def _resolve_tools(
    manifest: ExecutionManifest, token: str, gateway_base: str, used_sink: list[str] | None = None
) -> tuple[list[Any], list[str]]:
    # Tool handlers call Gateway resource endpoints (contracts/vouchers/etc)
    # on the user's behalf. Use the short-lived tool_execution token Gateway
    # minted alongside this manifest when present — it is scoped to exactly
    # this tenant and expires quickly, so a role=user session never needs its
    # own admin-level credential to use an authorized granted tool. Falls
    # back to the caller's own token only when Gateway didn't mint one (the
    # super_admin test path, which already carries native admin rights).
    tool_token = manifest.toolExecutionToken or token
    _, installed = _installed_tools(tool_token, gateway_base)
    granted = list(dict.fromkeys(tool.code for tool in manifest.tools))
    missing = [code for code in granted if code not in installed]
    if missing:
        raise ProviderResolutionError(f"Manifest tools have no installed handler: {', '.join(missing)}")
    gated = [
        _wrap_tool_with_hitl_gate(installed[code], agent_id=str(manifest.agent.id), token=tool_token,
                                   gateway_base=gateway_base, used_sink=used_sink)
        for code in granted
    ]
    return gated, granted


class AgentExecutor(ABC):
    def __init__(self, manifest: ExecutionManifest, settings: Settings, skills: SkillRegistry) -> None:
        self.manifest = manifest
        self.settings = settings
        self.skills = skills

    @abstractmethod
    async def execute(self, *, prompt: str, token: str, prior_history: list[ChatMessage] | None = None) -> ExecutionResult:
        raise NotImplementedError


class OllamaAgentExecutor(AgentExecutor):
    async def execute(self, *, prompt: str, token: str, prior_history: list[ChatMessage] | None = None) -> ExecutionResult:
        # Resolve even though Ollama currently has no function-calling loop: a
        # bad/missing granted handler must never be silently ignored.
        _, allowed = _resolve_tools(self.manifest, token, self.settings.gateway_base_url)
        if allowed:
            raise ProviderResolutionError("Ollama agent execution does not yet support granted tools")
        if self.manifest.delegates:
            raise ProviderResolutionError("Ollama agent execution does not yet support router delegation")
        _, skill_block = _resolve_skills(self.manifest, self.skills)
        system_parts = [self.manifest.systemPrompt]
        if skill_block:
            system_parts.append(skill_block)
        knowledge = _knowledge_block(self.manifest)
        if knowledge:
            system_parts.append(knowledge)
        workflow_block = _resolve_workflow(self.manifest)
        if workflow_block:
            system_parts.append(workflow_block)
        messages = [ChatMessage("system", "\n\n".join(system_parts))]
        messages.extend(prior_history or [])
        messages.append(ChatMessage("user", prompt))
        llm = OllamaLlmService(
            base_url=self.settings.ollama_base_url,
            model=self.manifest.model,
            timeout_seconds=self.manifest.timeoutSeconds,
        )
        try:
            result = await llm.chat(
                messages,
                temperature=self.manifest.temperature,
                max_tokens=self.manifest.maxTokens,
            )
        finally:
            await llm.aclose()
        return ExecutionResult(
            result.content, "ollama", self.manifest.model, [ExecutionStep("assistant", result.content)],
            input_tokens=usage.input_tokens if (usage := getattr(result, "usage", None)) else None,
            output_tokens=usage.output_tokens if usage else None,
            # No per-token cost model for a self-hosted Ollama deployment —
            # None (unknown), never a fabricated figure.
            estimated_cost_usd=None,
            workflow_id=str(self.manifest.workflow.id) if self.manifest.workflow else None,
        )


def _build_delegate_tool(
    manifest: ExecutionManifest, settings: Settings, skills: SkillRegistry, caller_token: str,
    delegation_sink: list[str] | None = None,
) -> Any | None:
    """Builds the router-only delegate_to_agent tool from manifest.delegates
    — the database-persisted, SUPER_ADMIN-curated list of specialists this
    agent may hand work to. None (no tool at all) when the list is empty, so
    an ordinary agent's tool surface is completely unchanged. Delegation is
    single-hop: the specialist manifest Gateway returns always carries an
    empty delegates list of its own (see GetDelegateManifestAsync), so this
    tool is never present on a delegated sub-execution regardless of what
    that specialist's own configuration contains.
    """
    if not manifest.delegates:
        return None
    by_slug = {d.slug: d for d in manifest.delegates}
    router_agent_id = str(manifest.agent.id)
    gateway_base = settings.gateway_base_url

    @tool(
        "delegate_to_agent",
        "Delegate a task to one of this router's assigned specialist agents. "
        f"Available specialists (slug: name): "
        + ", ".join(f"{d.slug}: {d.name}" for d in manifest.delegates),
        {"specialist_slug": str, "task": str},
    )
    async def delegate_to_agent(args: dict[str, Any]) -> dict[str, Any]:
        slug = str(args.get("specialist_slug", "")).strip()
        task = str(args.get("task", "")).strip()
        specialist = by_slug.get(slug)
        if specialist is None:
            return {"content": [{"type": "text", "text": f"Unknown specialist {slug!r}. Available: {', '.join(sorted(by_slug))}"}]}
        if not task:
            return {"content": [{"type": "text", "text": "task is required"}]}
        try:
            specialist_manifest = await fetch_delegate_manifest(
                gateway_base=gateway_base, token=caller_token,
                router_agent_id=router_agent_id, specialist_agent_id=str(specialist.id),
            )
        except ManifestLookupError as exc:
            return {"content": [{"type": "text", "text": f"Delegation denied: {exc}"}]}
        try:
            specialist_executor = build_agent_executor(specialist_manifest, settings, skills)
            result = await specialist_executor.execute(prompt=task, token=caller_token)
        except Exception as exc:  # noqa: BLE001 - surfaced as tool output, doesn't crash the router
            return {"content": [{"type": "text", "text": f"Specialist execution failed: {exc}"}]}
        if delegation_sink is not None:
            delegation_sink.append(slug)
        return {"content": [{"type": "text", "text": f"[{specialist.name}]: {result.final_answer}"}]}

    return delegate_to_agent


class ClaudeSdkAgentExecutor(AgentExecutor):
    async def execute(self, *, prompt: str, token: str, prior_history: list[ChatMessage] | None = None) -> ExecutionResult:
        tools_used: list[str] = []
        delegations: list[str] = []
        tools, allowed = _resolve_tools(self.manifest, token, self.settings.gateway_base_url, used_sink=tools_used)
        skill_names, skill_block = _resolve_skills(self.manifest, self.skills)
        system_parts = [self.manifest.systemPrompt]
        if skill_block:
            system_parts.append(skill_block)
        knowledge = _knowledge_block(self.manifest)
        if knowledge:
            system_parts.append(knowledge)
        workflow_block = _resolve_workflow(self.manifest)
        if workflow_block:
            system_parts.append(workflow_block)
        delegate_tool = _build_delegate_tool(self.manifest, self.settings, self.skills, token, delegation_sink=delegations)
        if delegate_tool is not None:
            tools = [*tools, delegate_tool]
            allowed = [*allowed, "mcp__aiq__delegate_to_agent"]
        query_parts = [f"[{message.role}]: {message.content}" for message in (prior_history or [])]
        query_parts.append(prompt)
        options = ClaudeAgentOptions(
            mcp_servers={"aiq": create_sdk_mcp_server(name="aiq", version="0.1.0", tools=tools)},
            allowed_tools=allowed,
            skills=skill_names,
            max_turns=self.manifest.maxTurns,
            model=self.manifest.model,
            system_prompt="\n\n".join(system_parts),
        )
        steps: list[ExecutionStep] = []
        chunks: list[str] = []
        result_message: ResultMessage | None = None

        async def _run() -> None:
            nonlocal result_message
            async with ClaudeSDKClient(options=options) as client:
                await client.query("\n\n".join(query_parts))
                async for message in client.receive_response():
                    if isinstance(message, AssistantMessage):
                        for block in message.content:
                            if isinstance(block, TextBlock) and (block.text or "").strip():
                                text = block.text.strip()
                                chunks.append(text)
                                steps.append(ExecutionStep("assistant", text))
                    elif isinstance(message, ResultMessage):
                        result_message = message

        # ClaudeAgentOptions has no wall-clock timeout of its own (max_turns
        # bounds turn count, not time) — a stuck CLI subprocess would hang the
        # request forever otherwise. Enforce the manifest's own configured cap.
        try:
            await asyncio.wait_for(_run(), timeout=self.manifest.timeoutSeconds)
        except asyncio.TimeoutError as exc:
            raise ExecutionTimeoutError(
                f"Agent execution exceeded its configured timeout of {self.manifest.timeoutSeconds}s"
            ) from exc
        answer = "\n\n".join(chunks).strip() or "(Agent không trả lời gì.)"
        usage = result_message.usage if result_message else None
        return ExecutionResult(
            answer, "claude_sdk", self.manifest.model, steps,
            input_tokens=(usage or {}).get("input_tokens") if usage else None,
            output_tokens=(usage or {}).get("output_tokens") if usage else None,
            estimated_cost_usd=result_message.total_cost_usd if result_message else None,
            tools_used=tools_used,
            delegations=delegations,
            workflow_id=str(self.manifest.workflow.id) if self.manifest.workflow else None,
        )


def build_agent_executor(manifest: ExecutionManifest, settings: Settings, skills: SkillRegistry) -> AgentExecutor:
    if manifest.provider == "ollama":
        return OllamaAgentExecutor(manifest, settings, skills)
    if manifest.provider == "claude_sdk":
        return ClaudeSdkAgentExecutor(manifest, settings, skills)
    raise ProviderResolutionError(f"Unknown manifest provider: {manifest.provider}")
