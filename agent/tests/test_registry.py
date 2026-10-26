"""Unit tests for aiq_agent.agentic.registry.fetch_company_context_block —
the generic no-agent-id chat path's (runner.run_task) fetch of AIQ's own
trusted company identity ("công ty chủ thể"). Mirrors
runtime/executors.py's manifest-driven _company_context_block, covered
separately in test_dynamic_runtime.py.
"""

from __future__ import annotations

import pytest

from aiq_agent.agentic.registry import fetch_company_context_block


class _FakeResponse:
    def __init__(self, status_code, body):
        self.status_code = status_code
        self._body = body

    def json(self):
        return self._body


def _fake_client(body, status_code=200, raises=None):
    class _Client:
        def __init__(self, **kwargs):
            pass

        async def __aenter__(self):
            return self

        async def __aexit__(self, *exc):
            return False

        async def get(self, url, headers=None):
            if raises is not None:
                raise raises
            _Client.last_url = url
            _Client.last_headers = headers
            return _FakeResponse(status_code, body)

    return _Client


@pytest.mark.asyncio
async def test_populated_profile_renders_trusted_fields(monkeypatch):
    from aiq_agent.agentic import registry as registry_module

    profile = {
        "legalName": "Công ty TNHH Việt Đăng",
        "taxCode": "0301234567",
        "shortName": "Việt Đăng",
        "aliases": ["AIQ", "Cát Tường AI"],
        "registeredAddress": "123 Nguyễn Huệ, Q1, TP.HCM",
        "legalRepresentative": "Nguyễn Văn A",
        "representativeTitle": "Giám đốc",
        "phone": None, "email": None, "website": None,
    }
    monkeypatch.setattr(registry_module.httpx, "AsyncClient", _fake_client({"profile": profile}))
    block = await fetch_company_context_block(gateway_base="http://gw", user_token="t")
    assert "TRUSTED" in block
    assert "legal_name: Công ty TNHH Việt Đăng" in block
    assert "tax_code: 0301234567" in block
    assert "aliases: AIQ, Cát Tường AI" in block
    assert "registered_address: 123 Nguyễn Huệ, Q1, TP.HCM" in block
    assert not block.endswith("company_identity: not_configured")


@pytest.mark.asyncio
async def test_missing_profile_renders_not_configured_never_guesses(monkeypatch):
    from aiq_agent.agentic import registry as registry_module

    monkeypatch.setattr(registry_module.httpx, "AsyncClient", _fake_client({"profile": None}))
    block = await fetch_company_context_block(gateway_base="http://gw", user_token="t")
    assert block.endswith("company_identity: not_configured")


@pytest.mark.asyncio
async def test_gateway_error_status_falls_back_to_not_configured_never_breaks_chat(monkeypatch):
    from aiq_agent.agentic import registry as registry_module

    monkeypatch.setattr(registry_module.httpx, "AsyncClient", _fake_client({}, status_code=403))
    block = await fetch_company_context_block(gateway_base="http://gw", user_token="t")
    assert block.endswith("company_identity: not_configured")


@pytest.mark.asyncio
async def test_network_failure_falls_back_to_not_configured_never_raises(monkeypatch):
    import httpx as httpx_module

    from aiq_agent.agentic import registry as registry_module

    monkeypatch.setattr(
        registry_module.httpx, "AsyncClient",
        _fake_client({}, raises=httpx_module.ConnectError("boom")),
    )
    block = await fetch_company_context_block(gateway_base="http://gw", user_token="t")
    assert block.endswith("company_identity: not_configured")


@pytest.mark.asyncio
async def test_all_fields_blank_renders_not_configured_not_an_empty_block(monkeypatch):
    from aiq_agent.agentic import registry as registry_module

    empty_profile = {
        "legalName": None, "taxCode": None, "shortName": None, "aliases": [],
        "registeredAddress": None, "legalRepresentative": None, "representativeTitle": None,
        "phone": None, "email": None, "website": None,
    }
    monkeypatch.setattr(
        registry_module.httpx, "AsyncClient", _fake_client({"profile": empty_profile})
    )
    block = await fetch_company_context_block(gateway_base="http://gw", user_token="t")
    assert block.endswith("company_identity: not_configured")
