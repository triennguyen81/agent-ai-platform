"""FastAPI routes."""

from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, Request, status

from aiq_agent.api.schemas import ChatRequest, ChatResponse, HealthResponse, StepResponse
from aiq_agent.llm.base import ChatMessage, LlmService
from aiq_agent.memory.base import MemoryStore
from aiq_agent.orchestration import PlannerExecutorCritic
from aiq_agent.tools.registry import ToolExecutionError, ToolRegistry

router = APIRouter()


def _llm(request: Request) -> LlmService:
    return request.app.state.llm


def _tools(request: Request) -> ToolRegistry:
    return request.app.state.tools


def _memory(request: Request) -> MemoryStore:
    return request.app.state.memory


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
) -> ChatResponse:
    if body.tool and tools.get(body.tool) is None:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Unknown tool: {body.tool!r}",
        )

    prior_history: list[ChatMessage] = []
    if body.thread_id:
        prior_history = await memory.get(body.thread_id, limit=body.memory_limit)

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

    history_length = len(prior_history)
    if body.thread_id:
        await memory.append(
            body.thread_id,
            [
                ChatMessage("user", body.prompt),
                ChatMessage("assistant", run.final_answer),
            ],
        )
        history_length += 2

    return ChatResponse(
        final_answer=run.final_answer,
        provider=llm.provider,
        model=llm.model,
        thread_id=body.thread_id,
        history_length=history_length,
        steps=[
            StepResponse(
                role=step.role,
                content=step.content,
                tool_name=step.tool_name,
                tool_ok=step.tool_result.ok if step.tool_result else None,
                tool_output=step.tool_result.output if step.tool_result else None,
            )
            for step in run.steps
        ],
    )


@router.delete("/chat/threads/{thread_id}", status_code=status.HTTP_204_NO_CONTENT)
async def clear_thread(thread_id: str, memory: MemoryStore = Depends(_memory)) -> None:
    await memory.clear(thread_id)


@router.get("/tools", response_model=list[dict])
async def list_tools(tools: ToolRegistry = Depends(_tools)) -> list[dict]:
    return tools.describe()
