"""FastAPI routes."""

from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, Request, status

from aiq_agent.api.schemas import ChatRequest, ChatResponse, HealthResponse, StepResponse
from aiq_agent.llm.base import LlmService
from aiq_agent.orchestration import PlannerExecutorCritic
from aiq_agent.tools.registry import ToolExecutionError, ToolRegistry

router = APIRouter()


def _llm(request: Request) -> LlmService:
    return request.app.state.llm


def _tools(request: Request) -> ToolRegistry:
    return request.app.state.tools


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
) -> ChatResponse:
    if body.tool and tools.get(body.tool) is None:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Unknown tool: {body.tool!r}",
        )

    orchestrator = PlannerExecutorCritic(llm=llm, tools=tools)
    try:
        run = await orchestrator.run(
            body.prompt,
            tool_name=body.tool,
            tool_arguments=body.tool_arguments,
        )
    except ToolExecutionError as exc:
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail=str(exc),
        ) from exc

    return ChatResponse(
        final_answer=run.final_answer,
        provider=llm.provider,
        model=llm.model,
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


@router.get("/tools", response_model=list[dict])
async def list_tools(tools: ToolRegistry = Depends(_tools)) -> list[dict]:
    return tools.describe()
