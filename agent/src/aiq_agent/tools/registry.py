"""Tool registry — minimal function-calling framework.

A Tool wraps an async callable with a JSON-schema-like input description.
The Planner-Executor-Critic loop consults `ToolRegistry.describe()` to expose
available tools to the LLM, then calls `ToolRegistry.invoke()` once the LLM
emits a tool selection.
"""

from __future__ import annotations

from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field
from typing import Any

ToolHandler = Callable[[dict[str, Any]], Awaitable["ToolResult"]]


@dataclass(frozen=True, slots=True)
class ToolResult:
    ok: bool
    output: Any
    error: str | None = None


@dataclass(frozen=True, slots=True)
class Tool:
    name: str
    description: str
    parameters: dict[str, Any]  # JSON-schema-like
    handler: ToolHandler = field(repr=False)


class ToolExecutionError(RuntimeError):
    """Raised when a tool handler crashes unexpectedly."""


class ToolRegistry:
    def __init__(self) -> None:
        self._tools: dict[str, Tool] = {}

    def register(self, tool: Tool) -> None:
        if tool.name in self._tools:
            raise ValueError(f"Tool already registered: {tool.name!r}")
        self._tools[tool.name] = tool

    def get(self, name: str) -> Tool | None:
        return self._tools.get(name)

    def describe(self) -> list[dict[str, Any]]:
        return [
            {
                "name": tool.name,
                "description": tool.description,
                "parameters": tool.parameters,
            }
            for tool in self._tools.values()
        ]

    async def invoke(self, name: str, arguments: dict[str, Any]) -> ToolResult:
        tool = self._tools.get(name)
        if tool is None:
            return ToolResult(ok=False, output=None, error=f"Unknown tool: {name!r}")
        try:
            return await tool.handler(arguments)
        except Exception as exc:  # noqa: BLE001 — boundary capture
            raise ToolExecutionError(f"Tool {name!r} failed: {exc}") from exc


# ---------------------------------------------------------------------------
# Default registry with a smoke-test tool. Real domain tools (PaddleOCR,
# pgvector lookup, etc.) will be added in later milestones.
# ---------------------------------------------------------------------------

async def _echo_handler(args: dict[str, Any]) -> ToolResult:
    message = str(args.get("message", ""))
    return ToolResult(ok=True, output={"echo": message})


def _build_default_registry() -> ToolRegistry:
    registry = ToolRegistry()
    registry.register(
        Tool(
            name="echo",
            description="Echo a message back. Smoke test only.",
            parameters={
                "type": "object",
                "properties": {
                    "message": {"type": "string", "description": "Text to echo."}
                },
                "required": ["message"],
            },
            handler=_echo_handler,
        )
    )
    return registry


default_registry = _build_default_registry()
