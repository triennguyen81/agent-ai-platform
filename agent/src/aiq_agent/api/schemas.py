"""Request/response models for the agent HTTP API."""

from __future__ import annotations

from typing import Any

from pydantic import BaseModel, Field


class ChatRequest(BaseModel):
    # 200K chars leaves headroom for the web tier to prepend a knowledge
    # block. Still well inside Claude Haiku 4.5's 200K-token context.
    prompt: str = Field(min_length=1, max_length=200_000)
    # Side-channel context that the agent injects into the system prompt
    # (e.g. user notes for RAG) but never stores in memory. Keeps thread
    # titles equal to the actual user question.
    knowledge_context: str | None = Field(default=None, max_length=200_000)
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


class AgentRunRequest(BaseModel):
    task: str = Field(min_length=1, max_length=20_000)
    user_token: str = Field(min_length=10, max_length=8_000)
    skills: list[str] | None = Field(
        default=None,
        description="Skill names to enable (default: all auto-discovered).",
    )
    max_turns: int = Field(default=8, ge=1, le=20)
    agent_id: str | None = Field(
        default=None,
        description=(
            "Optional governance agent id. When set, allowed_tools is fetched "
            "from /agents/{id}/tools/allowed instead of the hardcoded default. "
            "Backward-compatible: omit to use the legacy hardcoded tool list."
        ),
    )


class AgentRunStep(BaseModel):
    role: str
    text: str


class AgentRunResponse(BaseModel):
    final_answer: str
    steps: list[AgentRunStep]


class SkillSummary(BaseModel):
    name: str
    description: str
    keywords: list[str]
    always_on: bool
    requires_knowledge_context: bool


class AuditEntryResponse(BaseModel):
    ts: float
    event: str
    user_prefix: str
    name: str
    ms: int
    ok: bool
    detail: str | None = None


class EmbeddingNoteInput(BaseModel):
    """One note input for embedding upsert + search."""

    id: str = Field(min_length=1, max_length=64)
    title: str = Field(max_length=500)
    content: str = Field(max_length=200_000)


class EmbeddingQueryRequest(BaseModel):
    query: str = Field(min_length=1, max_length=10_000)
    user_id: str = Field(min_length=1, max_length=128)
    notes: list[EmbeddingNoteInput]
    k: int = Field(default=10, ge=1, le=50)


class EmbeddingQueryHit(BaseModel):
    note_id: str
    score: float


class EmbeddingQueryResponse(BaseModel):
    top_k: list[EmbeddingQueryHit]
    upserted: int
    enabled: bool


class HealthResponse(BaseModel):
    status: str
    provider: str
    model: str
