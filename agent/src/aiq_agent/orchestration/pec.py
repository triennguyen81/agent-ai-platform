"""Planner-Executor-Critic skeleton.

This is intentionally a thin scaffold for Week 3. The Planner asks the LLM for
a short plan, the Executor runs at most one tool from the registry, and the
Critic asks the LLM to judge the result. Later milestones will add proper
tool-call parsing (JSON / structured output), retries, and memory.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from aiq_agent.llm.base import ChatMessage, LlmService
from aiq_agent.tools.registry import ToolRegistry, ToolResult


@dataclass(slots=True)
class AgentStep:
    role: str  # "planner" | "executor" | "critic"
    content: str
    tool_name: str | None = None
    tool_result: ToolResult | None = None


@dataclass(slots=True)
class AgentRun:
    final_answer: str
    steps: list[AgentStep] = field(default_factory=list)


_PLANNER_SYSTEM = (
    "You are the PLANNER. Given the user's request and the list of available tools, "
    "produce a short numbered plan (1-3 steps). If no tool is needed, say 'no_tool'. "
    "Do not execute anything yourself."
)

_CRITIC_SYSTEM = (
    "You are the CRITIC. Given the user's original request and the executor output, "
    "either approve the answer ('APPROVED: <final answer>') or request a single "
    "concrete revision ('REVISE: <what to change>'). Keep it terse."
)


class PlannerExecutorCritic:
    """Single-pass PEC loop. Returns an AgentRun with intermediate steps."""

    def __init__(self, *, llm: LlmService, tools: ToolRegistry) -> None:
        self._llm = llm
        self._tools = tools

    async def run(
        self,
        user_prompt: str,
        *,
        tool_name: str | None = None,
        tool_arguments: dict[str, Any] | None = None,
    ) -> AgentRun:
        steps: list[AgentStep] = []

        # 1. Plan
        plan = await self._llm.chat(
            [
                ChatMessage("system", _PLANNER_SYSTEM),
                ChatMessage(
                    "user",
                    f"User request: {user_prompt}\n\n"
                    f"Available tools: {[t['name'] for t in self._tools.describe()]}",
                ),
            ]
        )
        steps.append(AgentStep(role="planner", content=plan.content))

        # 2. Execute — caller picks the tool for this skeleton pass.
        executor_content: str
        tool_result: ToolResult | None = None
        if tool_name:
            tool_result = await self._tools.invoke(tool_name, tool_arguments or {})
            executor_content = f"Ran tool {tool_name!r}: ok={tool_result.ok}, output={tool_result.output}"
        else:
            executor_content = "No tool invoked."
        steps.append(
            AgentStep(
                role="executor",
                content=executor_content,
                tool_name=tool_name,
                tool_result=tool_result,
            )
        )

        # 3. Critique
        critique = await self._llm.chat(
            [
                ChatMessage("system", _CRITIC_SYSTEM),
                ChatMessage(
                    "user",
                    f"Original request: {user_prompt}\n\n"
                    f"Plan: {plan.content}\n\n"
                    f"Executor output: {executor_content}",
                ),
            ]
        )
        steps.append(AgentStep(role="critic", content=critique.content))

        final_answer = _extract_final_answer(critique.content)
        return AgentRun(final_answer=final_answer, steps=steps)


def _extract_final_answer(critique: str) -> str:
    stripped = critique.strip()
    prefix = "APPROVED:"
    if stripped.upper().startswith(prefix):
        return stripped[len(prefix) :].strip()
    return stripped
