"""FastAPI routes."""

from __future__ import annotations

import re

from fastapi import APIRouter, Depends, HTTPException, Request, status

from aiq_agent.agentic import run_task
from aiq_agent.api.schemas import (
    AgentRunRequest,
    AgentRunResponse,
    AgentRunStep,
    AuditEntryResponse,
    ChatRequest,
    ChatResponse,
    EmbeddingQueryHit,
    EmbeddingQueryRequest,
    EmbeddingQueryResponse,
    HealthResponse,
    SkillSummary,
    StepResponse,
    ThreadHistoryMessage,
    ThreadHistoryResponse,
    ThreadSummaryResponse,
)
from aiq_agent.embeddings import EmbeddingService
from aiq_agent.embeddings.service import IngestNote
from aiq_agent.config import get_settings
from aiq_agent.audit import AuditStore
from aiq_agent.llm.base import ChatMessage, LlmService
from aiq_agent.memory.base import MemoryStore
from aiq_agent.orchestration import PlannerExecutorCritic
from aiq_agent.skills import SkillRegistry, select_skills
from aiq_agent.tools.registry import ToolExecutionError, ToolRegistry

router = APIRouter()

_DEFAULT_SYSTEM = """Bạn là trợ lý AI cá nhân thông minh, trả lời người dùng bằng tiếng Việt tự nhiên.

RÀNG BUỘC NGÔN NGỮ (BẮT BUỘC):
- Toàn bộ câu trả lời PHẢI 100% bằng tiếng Việt (bảng chữ Latin có dấu Việt).
- TUYỆT ĐỐI KHÔNG dùng ký tự Hán/Trung/Nhật/Hàn (CJK) — kể cả trong từ Hán-Việt.
- Ví dụ SAI: "tiếp接" (chữ 接 là Hán). Ví dụ ĐÚNG: "tiếp đãi".
- Nếu định viết một từ Hán-Việt, luôn viết bằng chữ Quốc ngữ (âm La-tinh có dấu), không chèn hanzi.
- Số + đơn vị tiếng Việt: dùng "triệu / tỷ / VNĐ" thay cho "万 / 亿 / 元".

PHẢI tuân thủ format trả lời:
1. Bắt đầu bằng <thinking>...</thinking> ngắn (2-4 câu) tóm tắt cách bạn suy luận.
2. Tiếp theo là câu trả lời chính bằng Markdown (heading, bullet, bảng nếu phù hợp).

Nếu prompt có chứa USER KNOWLEDGE BASE thì luôn ưu tiên trích dẫn thông tin từ đó và ghi rõ tên Note đã tham chiếu (vd "theo Note 3"). Nếu câu hỏi không liên quan knowledge thì trả lời chung như AI thông thường.
"""


# CJK code points: Han/Hiragana/Katakana/Hangul + CJK Compatibility.
# Guardrail against Claude occasionally emitting hanzi in Vietnamese output
# (mostly for Sino-Vietnamese vocabulary where shared etymology confuses
# the tokenizer). System prompt already forbids it — this is belt-and-braces.
_CJK_RE = re.compile(
    "[一-鿿㐀-䶿぀-ヿ가-힯豈-﫿]+",
    flags=re.UNICODE,
)


def _strip_cjk(text: str) -> str:
    """Remove stray CJK characters and tidy surrounding whitespace/punctuation.

    Handles the common failure mode where Claude emits "tiếp接" instead of
    "tiếp đãi". Dropping the hanzi loses the Sino-Vietnamese word but keeps
    the sentence readable — better than gibberish for a Vietnamese user.
    """
    if not text or not _CJK_RE.search(text):
        return text
    cleaned = _CJK_RE.sub("", text)
    cleaned = re.sub(r"[ \t]{2,}", " ", cleaned)
    cleaned = re.sub(r" +([,.:;!?])", r"\1", cleaned)
    return cleaned


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
    # Belt-and-braces: strip stray CJK hanzi that occasionally leaks into
    # Sino-Vietnamese words. System prompt already forbids it.
    answer = _strip_cjk(answer)
    answer = answer.strip()
    thinking_raw = "\n\n".join(m.strip() for m in matches if m.strip())
    thinking = _strip_cjk(thinking_raw) if thinking_raw else None
    return thinking, answer


def _llm(request: Request) -> LlmService:
    return request.app.state.llm


def _tools(request: Request) -> ToolRegistry:
    return request.app.state.tools


def _memory(request: Request) -> MemoryStore:
    return request.app.state.memory


def _skills(request: Request) -> SkillRegistry:
    return getattr(request.app.state, "skills", SkillRegistry())


def _audit(request: Request) -> AuditStore | None:
    return getattr(request.app.state, "audit", None)


def _embeddings(request: Request) -> EmbeddingService | None:
    return getattr(request.app.state, "embeddings", None)


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
    audit: AuditStore | None = Depends(_audit),
) -> ChatResponse:
    import time as _t

    _started = _t.monotonic()
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

    if audit is not None:
        user_prefix = (body.thread_id or "anon").split(":")[0]
        if body.tool:
            audit_name = f"tool:{body.tool}"
        else:
            try:
                audit_name = ",".join(s.name for s in active_skills) or "no-skill"  # type: ignore[name-defined]
            except NameError:
                audit_name = "no-skill"
        await audit.write(
            event="chat",
            name=audit_name,
            user_prefix=user_prefix,
            ms=int((_t.monotonic() - _started) * 1000),
            ok=True,
            detail=body.prompt[:200],
        )

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


@router.get("/skills/list", response_model=list[SkillSummary])
async def list_skills(skills: SkillRegistry = Depends(_skills)) -> list[SkillSummary]:
    return [
        SkillSummary(
            name=s.name,
            description=s.description,
            keywords=list(s.keywords),
            always_on=s.always_on,
            requires_knowledge_context=s.requires_knowledge_context,
        )
        for s in skills.skills
    ]


@router.post("/embeddings/query", response_model=EmbeddingQueryResponse)
async def embeddings_query(
    body: EmbeddingQueryRequest,
    embeddings: EmbeddingService | None = Depends(_embeddings),
) -> EmbeddingQueryResponse:
    """Lazy-upsert notes + return top-K nearest to the query.

    Designed to be called from the web /api/chat path right before the LLM
    call: web sends all the user's notes + the prompt, gets back the K most
    relevant note ids, and only inserts those into knowledge_context.

    If embeddings are disabled or fail, returns enabled=False — caller must
    fall back to full-context RAG.
    """
    if embeddings is None:
        return EmbeddingQueryResponse(top_k=[], upserted=0, enabled=False)

    try:
        import asyncio

        ingest_notes = [
            IngestNote(
                note_id=n.id,
                user_id=body.user_id,
                title=n.title,
                content=n.content,
            )
            for n in body.notes
        ]
        # fastembed + qdrant_client are sync — push to thread pool so we
        # don't block the FastAPI event loop on first-call model download.
        upserted = await asyncio.to_thread(embeddings.sync_notes, ingest_notes)
        hits = await asyncio.to_thread(
            embeddings.search,
            query=body.query,
            user_id=body.user_id,
            k=body.k,
        )
        return EmbeddingQueryResponse(
            top_k=[EmbeddingQueryHit(note_id=h.note_id, score=h.score) for h in hits],
            upserted=upserted,
            enabled=True,
        )
    except Exception as exc:  # noqa: BLE001 — never let embedding errors break /chat
        import logging

        logging.getLogger(__name__).warning(
            "Embedding query failed, caller will fall back: %s", exc
        )
        return EmbeddingQueryResponse(top_k=[], upserted=0, enabled=False)


@router.get("/admin/audit", response_model=list[AuditEntryResponse])
async def admin_audit(
    limit: int = 100,
    event: str | None = None,
    audit: AuditStore | None = Depends(_audit),
) -> list[AuditEntryResponse]:
    if audit is None:
        return []
    entries = await audit.list(limit=max(1, min(limit, 500)), event=event)
    return [
        AuditEntryResponse(
            ts=e.ts,
            event=e.event,
            user_prefix=e.user_prefix,
            name=e.name,
            ms=e.ms,
            ok=e.ok,
            detail=e.detail,
        )
        for e in entries
    ]


@router.post("/agent/run", response_model=AgentRunResponse)
async def agentic_run(
    body: AgentRunRequest,
    audit: AuditStore | None = Depends(_audit),
) -> AgentRunResponse:
    """Run a multi-step agentic task via the Claude Agent SDK.

    Tools available to the model:
    - mcp__aiq__search_notes / get_note / save_note (gateway-backed)
    - mcp__aiq__fetch_url

    Skills auto-discovered from /app/.claude/skills (Dockerfile symlink).
    Each run gets fresh tools bound to the caller's JWT — no cross-user
    leakage.
    """
    settings = get_settings()
    import time as _t

    _started = _t.monotonic()
    user_prefix = "anon"
    try:
        # Best-effort decode JWT to derive user prefix for audit; tools.py
        # also uses this token verbatim so we don't want to verify it twice.
        import base64
        import json as _json

        parts = body.user_token.split(".")
        if len(parts) >= 2:
            padded = parts[1] + "=" * (-len(parts[1]) % 4)
            claims = _json.loads(base64.urlsafe_b64decode(padded))
            user_prefix = str(claims.get("sub", "anon"))[:12]
    except Exception:  # noqa: BLE001
        pass

    try:
        result = await run_task(
            task=body.task,
            user_token=body.user_token,
            gateway_base=settings.gateway_base_url,
            skills=body.skills or "all",
            max_turns=body.max_turns,
            agent_id=body.agent_id,
        )
    except Exception as exc:  # noqa: BLE001 — surface root cause to caller
        if audit is not None:
            await audit.write(
                event="agentic",
                name="run_task",
                user_prefix=user_prefix,
                ms=int((_t.monotonic() - _started) * 1000),
                ok=False,
                detail=f"{exc.__class__.__name__}: {exc}"[:200],
            )
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail=f"Agentic run failed: {exc.__class__.__name__}: {exc}",
        ) from exc

    if audit is not None:
        await audit.write(
            event="agentic",
            name="run_task",
            user_prefix=user_prefix,
            ms=int((_t.monotonic() - _started) * 1000),
            ok=True,
            detail=body.task[:200],
        )

    return AgentRunResponse(
        final_answer=result.final_answer,
        steps=[AgentRunStep(role=s.role, text=s.text) for s in result.steps],
    )
