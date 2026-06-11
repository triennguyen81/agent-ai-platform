"""Request/response models for the agent HTTP API."""

from __future__ import annotations

from typing import Any

from pydantic import BaseModel, Field


class ChatRequest(BaseModel):
    # 200K chars leaves headroom for the web tier to prepend a knowledge
    # block. Still well inside Claude Haiku 4.5's 200K-token context.
    prompt: str = Field(min_length=1, max_length=200_000)
    tool: str | None = Field(default=None, description="Optional tool to invoke in Executor step.")
    tool_arguments: dict[str, Any] = Field(default_factory=dict)
    temperature: float = Field(default=0.2, ge=0.0, le=2.0)
    thread_id: str | None = Field(
        default=None,
        max_length=128,
        description="Conversation id. When provided, prior turns are loaded from memory and the new exchange is appended.",
    )
    memory_limit: int = Field(default=20, ge=0, le=200)


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
    thread_id: str | None = None
    history_length: int = 0
    thinking: str | None = None


class ThreadSummaryResponse(BaseModel):
    thread_id: str
    title: str
    message_count: int
    updated_at: float


class ThreadHistoryMessage(BaseModel):
    role: str
    content: str


class ThreadHistoryResponse(BaseModel):
    thread_id: str
    messages: list[ThreadHistoryMessage]


class HealthResponse(BaseModel):
    status: str
    provider: str
    model: str
