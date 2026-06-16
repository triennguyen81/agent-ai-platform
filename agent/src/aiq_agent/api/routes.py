"""FastAPI routes."""

from __future__ import annotations

import re

from fastapi import APIRouter, Depends, HTTPException, Request, status

from aiq_agent.api.schemas import (
    ChatRequest,
    ChatResponse,
    HealthResponse,
    StepResponse,
    ThreadHistoryMessage,
    ThreadHistoryResponse,
    ThreadSummaryResponse,
)
from aiq_agent.llm.base import ChatMessage, LlmService
from aiq_agent.memory.base import MemoryStore
from aiq_agent.orchestration import PlannerExecutorCritic
from aiq_agent.skills import SkillRegistry, select_skills
from aiq_agent.tools.registry import ToolExecutionError, ToolRegistry

router = APIRouter()

_DEFAULT_SYSTEM = """Bạn là trợ lý AI cá nhân thông minh, trả lời người dùng bằng tiếng Việt tự nhiên.

PHẢI tuân thủ format trả lời:
1. Bắt đầu bằng <thinking>...</thinking> ngắn (2-4 câu) tóm tắt cách bạn suy luận.
2. Tiếp theo là câu trả lời chính bằng Markdown (heading, bullet, bảng nếu phù hợp).

Nếu prompt có chứa USER KNOWLEDGE BASE thì luôn ưu tiên trích dẫn thông tin từ đó và ghi rõ tên Note đã tham chiếu (vd "theo Note 3"). Nếu câu hỏi không liên quan knowledge thì trả lời chung như AI thông thường.
"""


_THINK_RE = re.compile(r"<thinking>(.*?)</thinking>", flags=re.DOTALL | re.IGNORECASE)
# Catch dangling open tags the model sometimes leaves without a close tag.
_ORPHAN_OPEN_RE = re.compile(r"<thinking>", flags=re.IGNORECASE)
_ORPHAN_CLOSE_RE = re.compile(r"</thinking>", flags=re.IGNORECASE)


def _split_thinking(raw: str) -> tuple[str | None, str]:
    """Extract all <thinking>…</thinking> blocks and return (thinking, answer).

    The answer is the original text with every thinking block removed so the
    UI never accidentally renders reasoning as the final response.
    """
    matches = _THINK_RE.findall(raw)
    answer = _THINK_RE.sub("", raw)
    # Strip any orphan open/close tags so the answer is fully clean.
    answer = _ORPHAN_OPEN_RE.sub("", answer)
    answer = _ORPHAN_CLOSE_RE.sub("", answer)
    answer = answer.strip()
    thinking = "\n\n".join(m.strip() for m in matches if m.strip()) or None
    return thinking, answer


def _llm(request: Request) -> LlmService:
    return request.app.state.llm


def _tools(request: Request) -> ToolRegistry:
    return request.app.state.tools


def _memory(request: Request) -> MemoryStore:
    return request.app.state.memory


def _skills(request: Request) -> SkillRegistry:
    return getattr(request.app.state, "skills", SkillRegistry())


@router.get("/healthz/live", response_model=HealthResponse)
async def live(llm: LlmService = Depends(_llm)) -> HealthResponse:
    return HealthResponse(status="live", provider=llm.provider, model=llm.model)


@router.get("/healthz/ready", response_model=HealthResponse)
async def ready(llm: LlmService = Depends(_llm)) -> HealthResponse:
    # Stub: we don't actively probe the upstream LLM here to keep this cheap.
    return HealthResponse(status="ready", provider=llm.provider, model=llm.model)


@router.post("/chat", response_model=ChatResponse)
async def chat(
    body: ChatRequest,
    llm: LlmService = Depends(_llm),
    tools: ToolRegistry = Depends(_tools),
    memory: MemoryStore = Depends(_memory),
    skills: SkillRegistry = Depends(_skills),
) -> ChatResponse:
    if body.tool and tools.get(body.tool) is None:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Unknown tool: {body.tool!r}",
        )

    prior_history: list[ChatMessage] = []
    if body.thread_id:
        prior_history = await memory.get(body.thread_id, limit=body.memory_limit)

    thinking: str | None = None
    steps: list[StepResponse] = []

    if body.tool:
        # Tool-augmented path keeps the Planner-Executor-Critic loop.
        orchestrator = PlannerExecutorCritic(llm=llm, tools=tools)
        try:
            run = await orchestrator.run(
                body.prompt,
                tool_name=body.tool,
                tool_arguments=body.tool_arguments,
                prior_history=prior_history,
            )
        except ToolExecutionError as exc:
            raise HTTPException(
                status_code=status.HTTP_502_BAD_GATEWAY,
                detail=str(exc),
            ) from exc

        thinking, final_answer = _split_thinking(run.final_answer)
        steps = [
            StepResponse(
                role=step.role,
                content=step.content,
                tool_name=step.tool_name,
                tool_ok=step.tool_result.ok if step.tool_result else None,
                tool_output=step.tool_result.output if step.tool_result else None,
            )
            for step in run.steps
        ]
    else:
        # Plain chat: single LLM call with the thinking-style system prompt.
        # knowledge_context is fed via system, NOT user prompt — that keeps
        # the memory store's user message equal to the actual question, so
        # thread titles in the sidebar stay readable.
        has_kb = bool(body.knowledge_context)

        # Skills: every active SKILL.md body is appended to the system
        # message so the model picks up domain conventions (citation style,
        # tone, dental terminology, etc.) for this turn only.
        active_skills = select_skills(
            skills,
            prompt=body.prompt,
            has_knowledge_context=has_kb,
        )
        skill_block = "\n\n".join(
            f"===== SKILL: {s.name} =====\n{s.body}" for s in active_skills
        )

        system_parts = [_DEFAULT_SYSTEM]
        if skill_block:
            system_parts.append(skill_block)
        if has_kb:
            system_parts.append(
                "===== USER KNOWLEDGE BASE =====\n"
                f"{body.knowledge_context}\n"
                "===== END KNOWLEDGE BASE ====="
            )
        system_content = "\n\n".join(system_parts)

        messages: list[ChatMessage] = [ChatMessage("system", system_content)]
        messages.extend(prior_history)
        messages.append(ChatMessage("user", body.prompt))

        result = await llm.chat(messages, temperature=body.temperature)
        thinking, final_answer = _split_thinking(result.content)

    history_length = len(prior_history)
    if body.thread_id:
        await memory.append(
            body.thread_id,
            [
                ChatMessage("user", body.prompt),
                ChatMessage("assistant", final_answer),
            ],
        )
        history_length += 2

    return ChatResponse(
        final_answer=final_answer,
        provider=llm.provider,
        model=llm.model,
        thread_id=body.thread_id,
        history_length=history_length,
        thinking=thinking,
        steps=steps,
    )


@router.get("/chat/threads", response_model=list[ThreadSummaryResponse])
async def list_threads(
    memory: MemoryStore = Depends(_memory),
    prefix: str | None = None,
    limit: int = 50,
) -> list[ThreadSummaryResponse]:
    summaries = await memory.list_threads(prefix=prefix, limit=limit)
    return [
        ThreadSummaryResponse(
            thread_id=s.thread_id,
            title=s.title,
            message_count=s.message_count,
            updated_at=s.updated_at,
        )
        for s in summaries
    ]


@router.get("/chat/threads/{thread_id}", response_model=ThreadHistoryResponse)
async def get_thread(
    thread_id: str,
    memory: MemoryStore = Depends(_memory),
) -> ThreadHistoryResponse:
    history = await memory.get(thread_id)
    return ThreadHistoryResponse(
        thread_id=thread_id,
        messages=[
            ThreadHistoryMessage(role=m.role, content=m.content)
            for m in history
            if m.role in ("user", "assistant")
        ],
    )


@router.delete("/chat/threads/{thread_id}", status_code=status.HTTP_204_NO_CONTENT)
async def clear_thread(thread_id: str, memory: MemoryStore = Depends(_memory)) -> None:
    await memory.clear(thread_id)


@router.get("/tools", response_model=list[dict])
async def list_tools(tools: ToolRegistry = Depends(_tools)) -> list[dict]:
    return tools.describe()
