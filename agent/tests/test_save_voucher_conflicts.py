"""save_voucher never overwrites a posted or duplicate voucher (chứng từ flow v2 PR1).

The Gateway is the authority (409 voucher_posted / duplicate_voucher); the
tool must surface that refusal as a stop signal, not a retryable failure.
"""

from __future__ import annotations

import json

import httpx
import pytest

from aiq_agent.agentic import voucher_tools
from aiq_agent.agentic.voucher_tools import build_voucher_tools

_FIELDS = {"voucher_type": "invoice_vat_in", "document_number": "123", "seller_mst": "0312345678"}


def _gateway(monkeypatch: pytest.MonkeyPatch, response: httpx.Response) -> list[httpx.Request]:
    seen: list[httpx.Request] = []
    real_async_client = httpx.AsyncClient

    def recording(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return response

    def client_factory(*args, **kwargs):
        kwargs["transport"] = httpx.MockTransport(recording)
        return real_async_client(*args, **kwargs)

    monkeypatch.setattr(voucher_tools.httpx, "AsyncClient", client_factory)
    return seen


async def _save(args: dict) -> str:
    tools = {t.name: t for t in build_voucher_tools(token="tok", gateway_base="http://gateway")}
    result = await tools["save_voucher"].handler(args)
    return result["content"][0]["text"]


async def test_posted_voucher_conflict_tells_the_model_to_stop(monkeypatch) -> None:
    body = {"error": "voucher_posted", "message": "Chứng từ đã ghi sổ — không thể sửa."}
    seen = _gateway(monkeypatch, httpx.Response(409, json=body))

    text = await _save({"fields": _FIELDS, "voucher_id": "v-posted"})

    assert len(seen) == 1 and seen[0].url.path == "/vouchers/v-posted/apply-extraction"
    assert "đã ghi sổ" in text
    assert "KHÔNG thử lại" in text
    assert "✓" not in text


async def test_locked_voucher_conflict_tells_the_model_to_stop(monkeypatch) -> None:
    body = {"error": "voucher_locked", "message": "Chứng từ đang chờ duyệt — không sửa được."}
    _gateway(monkeypatch, httpx.Response(409, json=body))

    text = await _save({"fields": _FIELDS, "voucher_id": "v-pending"})

    assert "chờ duyệt" in text
    assert "KHÔNG thử lại" in text
    assert "trả lại" in text
    assert "✓" not in text


async def test_duplicate_conflict_names_the_existing_voucher_without_retrying(monkeypatch) -> None:
    body = {
        "error": "duplicate_voucher",
        "message": "Chứng từ trùng.",
        "existingVoucherId": "v-existing",
    }
    seen = _gateway(monkeypatch, httpx.Response(409, json=body))

    text = await _save({"fields": _FIELDS})

    assert len(seen) == 1 and seen[0].method == "POST" and seen[0].url.path == "/vouchers"
    assert "v-existing" in text
    assert "KHÔNG thử lại" in text
    assert "✓" not in text


async def test_duplicate_conflict_from_another_department_has_no_id(monkeypatch) -> None:
    body = {"error": "duplicate_voucher", "message": "Chứng từ trùng."}
    _gateway(monkeypatch, httpx.Response(409, json=body))

    text = await _save({"fields": _FIELDS})

    assert "trùng" in text and "id=" not in text


async def test_successful_save_is_unchanged(monkeypatch) -> None:
    seen = _gateway(monkeypatch, httpx.Response(201, json={"voucher": {"id": "v-new"}}))

    text = await _save({"fields": _FIELDS})

    assert text.startswith("✓ Đã tạo chứng từ id=v-new")
    assert json.loads(seen[0].content)["documentNumber"] == "123"


async def test_period_locked_tells_the_model_to_stop_and_names_the_period(monkeypatch) -> None:
    # Chứng từ flow v2 PR4: a closed accounting period refuses the write.
    body = {"error": "period_locked", "period": "2026-09", "message": "Kỳ kế toán 2026-09 đã khoá."}
    seen = _gateway(monkeypatch, httpx.Response(409, json=body))

    text = await _save({"fields": _FIELDS})

    assert len(seen) == 1
    assert "2026-09" in text
    assert "KHÔNG thử lại" in text
    assert "ADMIN" in text
    assert "✓" not in text


async def test_journal_save_in_a_locked_period_stops_too(monkeypatch) -> None:
    body = {"error": "period_locked", "period": "2026-09", "message": "Kỳ kế toán 2026-09 đã khoá."}
    seen = _gateway(monkeypatch, httpx.Response(409, json=body))
    tools = {t.name: t for t in build_voucher_tools(token="tok", gateway_base="http://gateway")}

    entry = {"debit_account": "1561", "credit_account": "331", "amount": 100}
    save = tools["save_journal_suggestions"].handler
    result = await save({"voucher_id": "v-sep", "entries": [entry]})
    text = result["content"][0]["text"]

    assert len(seen) == 1 and seen[0].url.path == "/vouchers/v-sep/journal-suggestions"
    assert "2026-09" in text and "KHÔNG thử lại" in text
    assert "✓" not in text


def test_the_agent_has_no_tool_to_close_or_reopen_a_period() -> None:
    names = {t.name for t in build_voucher_tools(token="tok", gateway_base="http://gateway")}
    assert not {n for n in names if "period" in n}
    source = json.dumps(sorted(names))
    assert "accounting-periods" not in source
