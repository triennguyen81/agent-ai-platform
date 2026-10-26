"""The Agent may PROPOSE a tax verification, never confirm one."""

from __future__ import annotations

import inspect

from aiq_agent.agentic import voucher_tools
from aiq_agent.agentic.voucher_tools import build_voucher_tools


def _names() -> set[str]:
    return {t.name for t in build_voucher_tools(token="t", gateway_base="http://gateway")}


def test_no_agent_tool_can_confirm_tax_verification() -> None:
    names = _names()
    assert "confirm_tax_verified" not in names
    assert "propose_tax_verification" in names
    assert "reject_tax_verified" in names


def test_agent_tools_never_call_an_authoritative_endpoint() -> None:
    source = inspect.getsource(voucher_tools)
    assert "tax-verification/confirm" not in source
    assert "tax-verification/reject" not in source
    assert "tax-verification/propose" in source


class _Recorder:
    calls: list[tuple[str, dict, dict]] = []

    def __init__(self, *args, **kwargs) -> None:
        pass

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc) -> None:
        return None

    async def post(self, url, headers=None, json=None):
        _Recorder.calls.append((url, headers or {}, json or {}))

        class _Res:
            status_code = 200
            text = "{}"

        return _Res()


async def test_llm_saying_verified_only_creates_an_agent_proposal(monkeypatch) -> None:
    monkeypatch.setattr(voucher_tools.httpx, "AsyncClient", _Recorder)
    _Recorder.calls.clear()
    tools = {t.name: t for t in build_voucher_tools(token="tok", gateway_base="http://gateway")}
    # The model "believes" the invoice is valid; the tool can still only propose.
    await tools["propose_tax_verification"].handler(
        {
            "voucher_id": "v1",
            "tct_status": "http_200",
            "response_snippet": "Tax code verified successfully",
            "note": "verified=true",
        }
    )
    (url, headers, body) = _Recorder.calls[-1]
    assert url == "http://gateway/vouchers/v1/tax-verification/propose"
    assert headers["X-AIQ-Actor"] == "agent"
    assert "verified" not in {k.lower() for k in body}  # no authority field is ever sent
    assert body["evidence"]["tool"] == "verify_einvoice_tct"


def test_legacy_confirm_tool_name_is_gone() -> None:
    # An old prompt asking for confirm_tax_verified(true) finds no such tool.
    assert not any(
        t.name == "confirm_tax_verified"
        for t in build_voucher_tools(token="t", gateway_base="http://g")
    )


async def test_reject_tool_only_proposes_invalid_or_unavailable(monkeypatch) -> None:
    monkeypatch.setattr(voucher_tools.httpx, "AsyncClient", _Recorder)
    _Recorder.calls.clear()
    tools = {t.name: t for t in build_voucher_tools(token="tok", gateway_base="http://gateway")}
    await tools["reject_tax_verified"].handler(
        {"voucher_id": "v2", "reason": "TCT trả 404", "provider_unavailable": False}
    )
    await tools["reject_tax_verified"].handler(
        {"voucher_id": "v3", "reason": "network_error", "provider_unavailable": True}
    )
    (u1, h1, b1), (u2, _, b2) = _Recorder.calls[-2:]
    assert u1 == "http://gateway/vouchers/v2/tax-verification/propose"
    assert u2 == "http://gateway/vouchers/v3/tax-verification/propose"
    assert (b1["recommendation"], b2["recommendation"]) == ("invalid", "unavailable")
    assert h1["X-AIQ-Actor"] == "agent"


async def test_propose_recommendation_is_restricted_to_known_values(monkeypatch) -> None:
    monkeypatch.setattr(voucher_tools.httpx, "AsyncClient", _Recorder)
    _Recorder.calls.clear()
    tools = {t.name: t for t in build_voucher_tools(token="tok", gateway_base="http://gateway")}
    await tools["propose_tax_verification"].handler(
        {"voucher_id": "v4", "recommendation": "VERIFIED_FINAL!!"}
    )
    assert _Recorder.calls[-1][2]["recommendation"] is None
