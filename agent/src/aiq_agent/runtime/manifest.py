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


class ManifestCompanyProfile(BaseModel):
    """AIQ's own legal-entity identity for the tenant ("công ty chủ thể") —
    server-resolved from tenant_company_profiles by the authenticated
    tenant_id, TENANT_OWNER-managed, never accepted from chat/request
    input. Absent entirely (None on ExecutionManifest) means the tenant has
    not configured one yet — the runtime must render that as
    not_configured, never guess an identity from conversation content."""

    model_config = ConfigDict(extra="forbid")
    legalName: str | None
    taxCode: str | None
    shortName: str | None
    aliases: list[str]
    registeredAddress: str | None
    legalRepresentative: str | None
    representativeTitle: str | None
    phone: str | None
    email: str | None
    website: str | None


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
    knowledgeBaseIds: list[UUID] = Field(default_factory=list)
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
    # D3: the caller's own department at execution time (Gateway already made
    # the department-entitlement decision before issuing this manifest).
    # Audit/thread-key context only — never re-checked or trusted as an
    # authorization input here; None for the departmentless super_admin
    # test path.
    departmentId: UUID | None = None
    # Trusted company identity, resolved server-side from the authenticated
    # tenant_id — see ManifestCompanyProfile. None means "not configured".
    companyProfile: ManifestCompanyProfile | None = None


class ManifestLookupError(PermissionError):
    """Manifest lookup or validation failed; execution must stop."""


async def fetch_execution_manifest(
    *, gateway_base: str, token: str, agent_id: str, timeout: float = 10.0, test: bool = False
) -> ExecutionManifest:
    # ACL V2-C: `test=True` asks the Gateway for the CURRENT DRAFT (falling
    # back to published) of a Department Agent the caller's own department
    # owns — the same additive `?mode=test` signal SUPER_ADMIN's Agent
    # Builder Test panel never needed (their own manifest resolution is
    # already draft-aware unconditionally). Ignored by the Gateway for every
    # other caller/agent shape, so omitting it (the default) is byte-for-byte
    # the pre-V2-C request.
    params = {"mode": "test"} if test else None
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            response = await client.get(
                f"{gateway_base.rstrip('/')}/runtime/agents/{agent_id}/manifest",
                headers={"Authorization": f"Bearer {token}"},
                params=params,
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
