"""Security contract tests for the HTTP runtime boundary."""

from __future__ import annotations

import pytest
from pydantic import ValidationError

from aiq_agent.api.security import RuntimeAuthorizationError, bearer_token
from aiq_agent.api.schemas import AgentRunRequest, ChatRequest
from aiq_agent.agentic import runner


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
