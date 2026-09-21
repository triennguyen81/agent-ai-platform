"""Request/response models for the agent HTTP API."""

from __future__ import annotations

from typing import Any

from pydantic import BaseModel, ConfigDict, Field


class ChatRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
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
    agent_id: str | None = Field(default=None, max_length=64)
    # ACL V2-C: Department Agent Builder's "Test" action — resolves the
    # CURRENT DRAFT of the caller's own Department Agent instead of
    # requiring a published version. Ignored unless the caller is an ADMIN
    # testing their own department's agent (see Gateway's
    # GET /runtime/agents/{id}/manifest?mode=test); false is the default,
    # unchanged production-chat request shape.
    test: bool = Field(default=False)


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
    # Explicit, persisted 'chat' | 'agentic' — see memory/base.py
    # DEFAULT_THREAD_MODE. Never inferred client-side from title/content.
    mode: str = "chat"
    # Which agent this thread was created against, or None for a plain
    # no-agent chat. Set once at creation — see memory/base.py ThreadSummary.
    agent_id: str | None = None


class ThreadHistoryMessage(BaseModel):
    role: str
    content: str


class ThreadHistoryResponse(BaseModel):
    thread_id: str
    messages: list[ThreadHistoryMessage]
    mode: str = "chat"
    agent_id: str | None = None


class AgentRunRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    task: str = Field(min_length=1, max_length=20_000)
    skills: list[str] | None = Field(
        default=None,
        description="Skill names to enable (default: all auto-discovered).",
    )
    max_turns: int = Field(default=8, ge=1, le=20)
    thread_id: str | None = Field(
        default=None,
        max_length=128,
        description=(
            "Agentic conversation id. When provided, prior turns are loaded "
            "from memory (manifest-driven execution only) and the new "
            "exchange is appended, persisted under mode='agentic' so it "
            "never mixes with mode='chat' threads from /chat."
        ),
    )
    agent_id: str | None = Field(
        default=None,
        description=(
            "Agent id. For agent-scoped execution, runtime settings, skills, "
            "tools and max turns come only from the Gateway manifest."
        ),
    )
    capability: str | None = Field(
        default=None,
        description=(
            "D4: a business capability code (e.g. 'accounting.journal') to "
            "resolve to an authorized agent id server-side, in place of a "
            "client-supplied agent_id. Mutually exclusive with agent_id — "
            "the browser should never need to know or choose an agent id."
        ),
    )
    preferred_agent_id: str | None = Field(
        default=None,
        description=(
            "Only meaningful together with `capability` (ignored otherwise): "
            "the agent id a user picked from the 'Xử lý bằng trợ lý' "
            "selector when more than one agent is eligible for this "
            "capability. The Gateway ALWAYS re-validates this against a "
            "freshly computed eligible set for the caller's own tenant/"
            "department/assignments before honoring it — this value is "
            "never trusted directly, exactly like agent_id never is."
        ),
    )


class AgentRunStep(BaseModel):
    role: str
    text: str


class AgentRunResponse(BaseModel):
    final_answer: str
    steps: list[AgentRunStep]
    # ExecutionResult.tools_used was already computed and audited internally
    # but never reached the caller — this field was simply missing from the
    # response schema, so FastAPI's response_model silently dropped it.
    # Empty on the legacy no-manifest path (AgenticRunResult has no tracing).
    tools_used: list[str] = []
    # Echoes AgentRunRequest.thread_id (mirrors ChatResponse.thread_id) so
    # the caller can confirm which agentic thread this exchange was
    # persisted under.
    thread_id: str | None = None


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
    user_id: str | None = None
    tenant_id: str | None = None
    department_id: str | None = None
    agent_id: str | None = None
    version: int | None = None
    provider: str | None = None
    model: str | None = None
    input_tokens: int | None = None
    output_tokens: int | None = None
    estimated_cost_usd: float | None = None
    tools_used: list[str] | None = None
    delegations: list[str] | None = None
    workflow_id: str | None = None


class EmbeddingNoteInput(BaseModel):
    """One note input for embedding upsert + search."""

    id: str = Field(min_length=1, max_length=64)
    title: str = Field(max_length=500)
    content: str = Field(max_length=200_000)


class EmbeddingQueryRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    query: str = Field(min_length=1, max_length=10_000)
    user_id: str | None = Field(default=None, max_length=128, description="Deprecated; authenticated user is authoritative.")
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
