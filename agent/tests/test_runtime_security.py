"""Security contract tests for the HTTP runtime boundary."""

from __future__ import annotations

import httpx
import pytest
from fastapi import HTTPException
from pydantic import ValidationError

from aiq_agent.agentic import runner
from aiq_agent.api import security
from aiq_agent.api.schemas import AgentRunRequest, ChatRequest
from aiq_agent.api.security import (
    RuntimeAuthorizationError,
    RuntimePrincipal,
    bearer_token,
    resolve_capability_agent,
)


def test_bearer_token_requires_authorization_header() -> None:
    with pytest.raises(RuntimeAuthorizationError):
        bearer_token(None)


def test_bearer_token_rejects_non_bearer_scheme() -> None:
    with pytest.raises(RuntimeAuthorizationError):
        bearer_token("Basic abc")


def test_bearer_token_extracts_token_without_accepting_json_credentials() -> None:
    assert bearer_token("Bearer signed.jwt.value") == "signed.jwt.value"


@pytest.mark.parametrize("model", [AgentRunRequest, ChatRequest])
def test_json_bearer_token_field_is_rejected(model: type) -> None:
    payload = {"user_token": "signed.jwt.value"}
    payload["task" if model is AgentRunRequest else "prompt"] = "test"
    with pytest.raises(ValidationError):
        model.model_validate(payload)


@pytest.mark.asyncio
async def test_tool_permission_registry_failure_fails_closed(monkeypatch) -> None:
    async def unavailable(**_kwargs):
        return None

    monkeypatch.setattr(runner, "fetch_allowed_tool_codes", unavailable)
    monkeypatch.setattr(runner, "build_tools", lambda **_kwargs: [])
    monkeypatch.setattr(runner, "create_sdk_mcp_server", lambda **_kwargs: object())

    with pytest.raises(PermissionError, match="refusing fail-open execution"):
        await runner.run_task(
            task="test",
            user_token="signed.jwt.value",
            gateway_base="http://gateway:8080",
            agent_id="00000000-0000-0000-0000-000000000001",
        )


# ---------------------------------------------------------------------------
# D4: capability -> agent resolution (aiq_agent.api.security.resolve_capability_agent)
# ---------------------------------------------------------------------------


class _FakeResponse:
    def __init__(self, status_code: int, body: dict | None = None) -> None:
        self.status_code = status_code
        self._body = body or {}

    def json(self) -> dict:
        return self._body


class _FakeAsyncClient:
    def __init__(self, response: _FakeResponse) -> None:
        self._response = response
        self.requested_url: str | None = None
        self.requested_headers: dict | None = None
        self.requested_params: dict | None = None

    async def __aenter__(self) -> _FakeAsyncClient:
        return self

    async def __aexit__(self, *exc_info: object) -> bool:
        return False

    async def get(self, url: str, headers: dict | None = None, params: dict | None = None) -> _FakeResponse:
        self.requested_url = url
        self.requested_headers = headers
        self.requested_params = params
        return self._response


def _patch_gateway_response(
    monkeypatch: pytest.MonkeyPatch, response: _FakeResponse
) -> _FakeAsyncClient:
    fake_client = _FakeAsyncClient(response)
    monkeypatch.setattr(httpx, "AsyncClient", lambda *_a, **_kw: fake_client)
    return fake_client


def _principal() -> RuntimePrincipal:
    return RuntimePrincipal(
        token="signed.jwt.value", user_id="u1", roles=frozenset({"admin"}), tenant_id="t1"
    )


@pytest.mark.asyncio
async def test_resolve_capability_agent_returns_the_resolved_id_on_200(monkeypatch) -> None:
    fake_client = _patch_gateway_response(
        monkeypatch, _FakeResponse(200, {"agentId": "11111111-1111-1111-1111-111111111111"})
    )
    resolved = await resolve_capability_agent(_principal(), "accounting.journal")
    assert resolved == "11111111-1111-1111-1111-111111111111"
    base = security.get_settings().gateway_base_url.rstrip("/")
    assert fake_client.requested_url == f"{base}/me/capabilities/accounting.journal/agent"
    assert fake_client.requested_headers == {"Authorization": "Bearer signed.jwt.value"}


@pytest.mark.asyncio
async def test_resolve_capability_agent_omits_preferred_param_when_not_given(monkeypatch) -> None:
    # Every existing caller (Voucher/Invoice/etc., not yet migrated to the
    # "Xử lý bằng trợ lý" selector) must see byte-for-byte the same request
    # this always sent before preferred_agent_id existed.
    fake_client = _patch_gateway_response(
        monkeypatch, _FakeResponse(200, {"agentId": "11111111-1111-1111-1111-111111111111"})
    )
    await resolve_capability_agent(_principal(), "accounting.journal")
    assert fake_client.requested_params is None


@pytest.mark.asyncio
async def test_resolve_capability_agent_forwards_preferred_agent_id_as_query_param(monkeypatch) -> None:
    fake_client = _patch_gateway_response(
        monkeypatch, _FakeResponse(200, {"agentId": "22222222-2222-2222-2222-222222222222"})
    )
    resolved = await resolve_capability_agent(
        _principal(), "contract.processing", preferred_agent_id="22222222-2222-2222-2222-222222222222"
    )
    assert resolved == "22222222-2222-2222-2222-222222222222"
    assert fake_client.requested_params == {"preferredAgentId": "22222222-2222-2222-2222-222222222222"}


@pytest.mark.asyncio
async def test_resolve_capability_agent_rejects_unauthorized_preferred_agent_id(monkeypatch) -> None:
    # The Gateway is the one re-validating preferred_agent_id — this just
    # proves the 403 it returns for a rejected preference surfaces exactly
    # like any other denial, never silently falls back to a different agent.
    _patch_gateway_response(monkeypatch, _FakeResponse(403, {"error": "capability_not_available"}))
    with pytest.raises(HTTPException) as exc_info:
        await resolve_capability_agent(
            _principal(), "contract.processing", preferred_agent_id="not-actually-eligible"
        )
    assert exc_info.value.status_code == 403


@pytest.mark.asyncio
async def test_resolve_capability_agent_maps_ambiguous_to_409(monkeypatch) -> None:
    _patch_gateway_response(monkeypatch, _FakeResponse(409, {"error": "capability_ambiguous"}))
    with pytest.raises(HTTPException) as exc_info:
        await resolve_capability_agent(_principal(), "accounting.journal")
    assert exc_info.value.status_code == 409


@pytest.mark.asyncio
async def test_resolve_capability_agent_maps_denial_to_403(monkeypatch) -> None:
    _patch_gateway_response(monkeypatch, _FakeResponse(403, {"error": "capability_not_available"}))
    with pytest.raises(HTTPException) as exc_info:
        await resolve_capability_agent(_principal(), "accounting.journal")
    assert exc_info.value.status_code == 403


@pytest.mark.asyncio
async def test_resolve_capability_agent_fails_closed_on_transport_error(monkeypatch) -> None:
    class _BrokenClient:
        async def __aenter__(self) -> _BrokenClient:
            return self

        async def __aexit__(self, *exc_info: object) -> bool:
            return False

        async def get(self, *_a, **_kw):
            raise httpx.ConnectError("boom")

    monkeypatch.setattr(httpx, "AsyncClient", lambda *_a, **_kw: _BrokenClient())
    with pytest.raises(HTTPException) as exc_info:
        await resolve_capability_agent(_principal(), "accounting.journal")
    assert exc_info.value.status_code == 503


def test_agent_run_request_accepts_capability_in_place_of_agent_id() -> None:
    request = AgentRunRequest(task="t", capability="accounting.journal")
    assert request.agent_id is None
    assert request.capability == "accounting.journal"
