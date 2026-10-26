"""Authentication and agent-execution authorization at the runtime boundary."""

from __future__ import annotations

from dataclasses import dataclass

import httpx
from fastapi import HTTPException, Request, status

from aiq_agent.config import get_settings
from aiq_agent.runtime.manifest import (
    ExecutionManifest,
    ManifestLookupError,
    fetch_execution_manifest,
)


class RuntimeAuthorizationError(ValueError):
    pass


def bearer_token(authorization: str | None) -> str:
    if not authorization:
        raise RuntimeAuthorizationError("Authorization header is required")
    scheme, separator, token = authorization.partition(" ")
    if not separator or scheme.lower() != "bearer" or not token.strip():
        raise RuntimeAuthorizationError("A Bearer token is required")
    return token.strip()


@dataclass(frozen=True, slots=True)
class RuntimePrincipal:
    token: str
    user_id: str
    roles: frozenset[str]
    tenant_id: str | None = None
    department_id: str | None = None

    @property
    def is_user_only(self) -> bool:
        return not ({"super_admin", "admin"} & self.roles)


async def require_principal(request: Request) -> RuntimePrincipal:
    try:
        token = bearer_token(request.headers.get("Authorization"))
    except RuntimeAuthorizationError as exc:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail=str(exc)) from exc

    settings = get_settings()
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            response = await client.get(
                f"{settings.gateway_base_url.rstrip('/')}/me",
                headers={"Authorization": f"Bearer {token}"},
            )
    except httpx.RequestError as exc:
        raise HTTPException(status_code=503, detail="Authentication service unavailable") from exc
    if response.status_code != 200:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid session")
    body = response.json()
    tenant_id = body.get("tenantId")
    department_id = body.get("departmentId")
    principal = RuntimePrincipal(
        token=token,
        user_id=str(body.get("sub") or ""),
        roles=frozenset(str(role) for role in body.get("roles") or []),
        tenant_id=str(tenant_id) if tenant_id else None,
        department_id=str(department_id) if department_id else None,
    )
    if not principal.user_id or (principal.is_user_only and not principal.tenant_id):
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid tenant-bound session"
        )
    return principal


async def require_agent_execution(
    principal: RuntimePrincipal,
    agent_id: str,
    *,
    test: bool = False,
    version_id: str | None = None,
) -> ExecutionManifest:
    settings = get_settings()
    try:
        return await fetch_execution_manifest(
            gateway_base=settings.gateway_base_url,
            token=principal.token,
            agent_id=agent_id,
            test=test,
            version_id=version_id,
        )
    except ManifestLookupError as exc:
        raise HTTPException(status_code=403, detail="Agent execution is not allowed") from exc
    except Exception as exc:  # validation and protocol errors fail closed
        raise HTTPException(status_code=503, detail="Authorization service unavailable") from exc


async def resolve_capability_agent(
    principal: RuntimePrincipal,
    capability: str,
    preferred_agent_id: str | None = None,
) -> str:
    """D4: resolve a business capability code to an authorized agent id.

    The Gateway is the sole authority here — it derives tenant/department/
    user scope from the principal's own token and applies the canonical
    binding-first / exactly-one-eligible / ambiguity-error resolution rule.
    This function only translates that decision into an HTTP outcome; the
    resolved agent id still has to pass require_agent_execution's full
    manifest check afterward, unchanged.

    preferred_agent_id ("Xử lý bằng trợ lý" selector, contracts/upload and
    later Voucher/Invoice/etc.) is passed straight through as a query
    parameter — the Gateway re-validates it against a freshly computed
    eligible set for THIS principal before ever honoring it; it is never
    trusted here or anywhere else client-side.
    """
    settings = get_settings()
    url = f"{settings.gateway_base_url.rstrip('/')}/me/capabilities/{capability}/agent"
    params = {"preferredAgentId": preferred_agent_id} if preferred_agent_id else None
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            response = await client.get(
                url,
                headers={"Authorization": f"Bearer {principal.token}"},
                params=params,
            )
    except httpx.RequestError as exc:
        raise HTTPException(status_code=503, detail="Authorization service unavailable") from exc
    if response.status_code == 200:
        agent_id = response.json().get("agentId")
        if not agent_id:
            raise HTTPException(status_code=403, detail="Capability is not available")
        return str(agent_id)
    if response.status_code == 409:
        detail = "Capability is ambiguous; a tenant owner must configure a binding"
        raise HTTPException(status_code=409, detail=detail)
    raise HTTPException(status_code=403, detail="Capability is not available")
