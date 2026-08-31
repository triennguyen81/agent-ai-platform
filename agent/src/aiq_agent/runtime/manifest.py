"""Strict Gateway execution-manifest contract for agent-scoped requests."""

from __future__ import annotations

from typing import Any, Literal
from uuid import UUID

import httpx
from pydantic import BaseModel, ConfigDict, Field


class ManifestAgent(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: UUID
    slug: str = Field(min_length=2, max_length=80)
    name: str = Field(min_length=1, max_length=160)
    description: str | None
    iconMetadata: dict[str, Any]


class ManifestTool(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: UUID
    code: str = Field(min_length=1, max_length=200)
    name: str = Field(min_length=1, max_length=200)
    description: str | None
    category: str | None
    riskTier: str = Field(min_length=1, max_length=40)


class ManifestKnowledge(BaseModel):
    model_config = ConfigDict(extra="forbid")
    noteId: UUID
    title: str
    content: str
    tags: list[str]


class ManifestWorkflow(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: UUID
    name: str = Field(min_length=1, max_length=160)
    # Shape validated structurally here; step *type* values are validated by
    # runtime.executors at resolution time (fail closed on anything unknown —
    # see _WORKFLOW_STEP_TYPES) so a new step type added only on one side of
    # the Gateway/runtime deploy never silently no-ops.
    definition: dict[str, Any]


class ManifestDelegate(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: UUID
    slug: str = Field(min_length=2, max_length=80)
    name: str = Field(min_length=1, max_length=160)
    description: str | None


class ExecutionManifest(BaseModel):
    """Every field is required; extra Gateway/client data is rejected."""

    model_config = ConfigDict(extra="forbid")
    userId: UUID
    tenantId: UUID
    agent: ManifestAgent
    publishedVersionId: UUID
    version: int = Field(gt=0)
    provider: Literal["ollama", "claude_sdk"]
    model: str = Field(min_length=1, max_length=200, pattern=r"^[^\s@]+$")
    systemPrompt: str = Field(min_length=1)
    temperature: float = Field(ge=0, le=2)
    maxTurns: int = Field(ge=1, le=20)
    timeoutSeconds: int = Field(ge=1, le=3600)
    maxTokens: int = Field(ge=1, le=1_000_000)
    memoryConfig: dict[str, Any]
    tools: list[ManifestTool]
    skills: list[str]
    knowledge: list[ManifestKnowledge]
    # None means "run as a single prompt/tool-use turn" (unchanged behavior).
    workflow: ManifestWorkflow | None = None
    # Specialist agents this agent (acting as a router) may delegate to.
    # Empty for ordinary agents. Always [] on a manifest fetched THROUGH a
    # delegation (see fetch_delegate_manifest) — delegation is single-hop.
    delegates: list[ManifestDelegate] = Field(default_factory=list)
    # Short-lived, Gateway-minted credential for granted-tool HTTP calls back
    # to Gateway resource endpoints (contracts/vouchers/etc). Distinct from
    # the caller's own bearer token so a role=user session can only reach
    # those endpoints through an authorized, audited tool grant — never
    # directly. None on the super_admin test path (which already has native
    # admin rights) and for providers/paths that never call Gateway tools.
    toolExecutionToken: str | None = None


class ManifestLookupError(PermissionError):
    """Manifest lookup or validation failed; execution must stop."""


async def fetch_execution_manifest(
    *, gateway_base: str, token: str, agent_id: str, timeout: float = 10.0
) -> ExecutionManifest:
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            response = await client.get(
                f"{gateway_base.rstrip('/')}/runtime/agents/{agent_id}/manifest",
                headers={"Authorization": f"Bearer {token}"},
            )
    except httpx.RequestError as exc:
        raise ManifestLookupError("Manifest service unavailable") from exc
    if response.status_code != 200:
        raise ManifestLookupError(f"Manifest authorization failed ({response.status_code})")
    try:
        manifest = ExecutionManifest.model_validate(response.json())
    except (ValueError, TypeError) as exc:
        raise ManifestLookupError("Gateway returned an invalid execution manifest") from exc
    if str(manifest.agent.id) != str(agent_id):
        raise ManifestLookupError("Manifest agent identity mismatch")
    return manifest


async def fetch_delegate_manifest(
    *, gateway_base: str, token: str, router_agent_id: str, specialist_agent_id: str, timeout: float = 10.0
) -> ExecutionManifest:
    """Same contract as fetch_execution_manifest, but for a specialist reached
    THROUGH an authorized router delegation (see runtime/executors.py's
    delegate_to_agent tool). Gateway re-derives the router's own authorization
    from scratch — this call carries no assumption that a prior fetch succeeded.
    """
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            response = await client.get(
                f"{gateway_base.rstrip('/')}/runtime/agents/{router_agent_id}/delegate/{specialist_agent_id}/manifest",
                headers={"Authorization": f"Bearer {token}"},
            )
    except httpx.RequestError as exc:
        raise ManifestLookupError("Delegate manifest service unavailable") from exc
    if response.status_code != 200:
        raise ManifestLookupError(f"Delegate manifest authorization failed ({response.status_code})")
    try:
        manifest = ExecutionManifest.model_validate(response.json())
    except (ValueError, TypeError) as exc:
        raise ManifestLookupError("Gateway returned an invalid delegate execution manifest") from exc
    if str(manifest.agent.id) != str(specialist_agent_id):
        raise ManifestLookupError("Delegate manifest agent identity mismatch")
    return manifest
