"""Request/response models for the agent HTTP API."""

from __future__ import annotations

from typing import Any

from pydantic import BaseModel, Field


class ChatRequest(BaseModel):
    prompt: str = Field(min_length=1, max_length=8_000)
    tool: str | None = Field(default=None, description="Optional tool to invoke in Executor step.")
    tool_arguments: dict[str, Any] = Field(default_factory=dict)
    temperature: float = Field(default=0.2, ge=0.0, le=2.0)


class StepResponse(BaseModel):
    role: str
    content: str
    tool_name: str | None = None
    tool_ok: bool | None = None
    tool_output: Any = None


class ChatResponse(BaseModel):
    final_answer: str
    provider: str
    model: str
    steps: list[StepResponse]


class HealthResponse(BaseModel):
    status: str
    provider: str
    model: str
