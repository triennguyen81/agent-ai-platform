"""Configurable document processing — Agent runtime side.

The Gateway owns document configuration, prompt construction, schema
validation and every accounting effect. The Agent's job is narrow: accept the
document configuration as part of the strict manifest, and run a TOOL-LESS
extraction pinned to the exact version the Gateway built the prompt from.
"""

from __future__ import annotations

from types import SimpleNamespace
from uuid import uuid4

import pytest
from fastapi import HTTPException
from pydantic import ValidationError

from aiq_agent.api import routes
from aiq_agent.api.schemas import AgentRunResponse, DocumentExtractRequest
from aiq_agent.api.security import RuntimePrincipal
from aiq_agent.runtime.executors import ExecutionResult, _resolve_workflow
from aiq_agent.runtime.manifest import ExecutionManifest
from tests.test_dynamic_runtime import manifest_data


def document_processing(version_id: str, **type_overrides):
    doc_type = {
        "code": "invoice_vat_out",
        "displayName": "Hóa đơn bán",
        "enabled": True,
        "accountingHandler": "sales_invoice",
        "fields": [
            {
                "name": "document_number",
                "label": "Số hóa đơn",
                "type": "string",
                "required": True,
                "description": None,
            }
        ],
        "schema": {
            "type": "object",
            "properties": {"document_number": {"type": "string"}},
            "required": ["document_number"],
        },
        "schemaSha256": "ab" * 32,
        "extractionInstructions": "Ghi đúng số liệu.",
        "workflow": None,
        "skills": [],
    }
    doc_type.update(type_overrides)
    return {"agentVersionId": version_id, "draft": False, "types": [doc_type]}


def _manifest(with_config: bool = True, **overrides) -> ExecutionManifest:
    version_id = str(uuid4())
    data = manifest_data(publishedVersionId=version_id, provider="claude_sdk", **overrides)
    if with_config:
        data["documentProcessing"] = document_processing(version_id)
    return ExecutionManifest.model_validate(data)


# ---------------------------------------------------------------------------
# Manifest contract
# ---------------------------------------------------------------------------


def test_manifest_accepts_document_processing_with_schema_alias() -> None:
    manifest = _manifest()
    doc_type = manifest.documentProcessing.types[0]
    assert doc_type.json_schema["required"] == ["document_number"]
    assert doc_type.accountingHandler == "sales_invoice"


def test_manifest_without_document_processing_is_legacy() -> None:
    assert _manifest(with_config=False).documentProcessing is None


@pytest.mark.parametrize(
    "bad",
    [
        {
            "fields": [
                {"name": "x", "label": "x", "type": "sql", "required": False, "description": None}
            ]
        },
        {"debitAccount": "131"},  # no Dr/Cr or any unknown key can ride along
        {"skills": [{"code": "x", "sourceKind": "python"}]},
    ],
)
def test_manifest_document_type_is_strict(bad: dict) -> None:
    data = manifest_data()
    data["documentProcessing"] = document_processing(data["publishedVersionId"], **bad)
    with pytest.raises(ValidationError):
        ExecutionManifest.model_validate(data)


def test_require_review_workflow_step_is_known_and_inert_in_chat() -> None:
    manifest = _manifest(
        workflow={
            "id": str(uuid4()),
            "name": "Duyệt",
            "definition": {
                "steps": [{"type": "require_review"}, {"type": "prompt", "text": "Kiểm tra MST"}]
            },
        }
    )
    block = _resolve_workflow(manifest)
    assert "Kiểm tra MST" in block


def test_agent_run_response_carries_provenance_fields() -> None:
    fields = AgentRunResponse.model_fields
    for name in ("agent_id", "agent_version_id", "agent_version", "provider", "model"):
        assert name in fields


# ---------------------------------------------------------------------------
# POST /agent/document-extract
# ---------------------------------------------------------------------------


def _principal() -> RuntimePrincipal:
    return RuntimePrincipal(
        token="signed.jwt", user_id="u-1", roles=frozenset({"admin"}), tenant_id="t-1"
    )


def _patch(monkeypatch, manifest: ExecutionManifest):
    calls: dict = {"executed_manifests": [], "require": []}

    async def fake_require(principal, agent_id, *, test=False, version_id=None):
        calls["require"].append((agent_id, test))
        calls.setdefault("version_ids", []).append(version_id)
        return manifest

    class FakeExecutor:
        def __init__(self, m):
            self.m = m

        async def execute(self, *, prompt, token, prior_history=None):
            calls["prompt"] = prompt
            return ExecutionResult("```json\n{}\n```", self.m.provider, self.m.model)

    def fake_build(m, settings, skills):
        calls["executed_manifests"].append(m)
        return FakeExecutor(m)

    async def fake_report(**_kwargs):
        return None

    monkeypatch.setattr(routes, "require_agent_execution", fake_require)
    monkeypatch.setattr(routes, "build_agent_executor", fake_build)
    monkeypatch.setattr(routes, "report_usage", fake_report)
    return calls


async def test_document_extract_runs_without_any_tools(monkeypatch) -> None:
    manifest = _manifest(
        tools=[
            {
                "id": str(uuid4()),
                "code": "mcp__aiq__save_voucher",
                "name": "save",
                "description": None,
                "category": None,
                "riskTier": "write",
            }
        ],
        delegates=[{"id": str(uuid4()), "slug": "spec", "name": "Spec", "description": None}],
    )
    calls = _patch(monkeypatch, manifest)
    body = DocumentExtractRequest(
        agent_id=str(manifest.agent.id),
        expected_version_id=str(manifest.publishedVersionId),
        task="TASK",
    )
    response = await routes.document_extract(body, _principal(), None, SimpleNamespace(skills=[]))

    executed = calls["executed_manifests"][0]
    assert executed.tools == [] and executed.delegates == [] and executed.workflow is None
    assert executed.maxTurns <= 2
    assert calls["prompt"] == "TASK"
    assert response.agent_version_id == str(manifest.publishedVersionId)
    assert response.agent_version == manifest.version
    assert response.draft is False
    assert calls["require"] == [(str(manifest.agent.id), False)]


async def test_document_extract_rejects_version_mismatch(monkeypatch) -> None:
    manifest = _manifest()
    calls = _patch(monkeypatch, manifest)
    body = DocumentExtractRequest(
        agent_id=str(manifest.agent.id), expected_version_id=str(uuid4()), task="T"
    )
    with pytest.raises(HTTPException) as info:
        await routes.document_extract(body, _principal(), None, SimpleNamespace(skills=[]))
    assert info.value.status_code == 409
    assert calls["executed_manifests"] == []


async def test_document_extract_requires_document_configuration(monkeypatch) -> None:
    manifest = _manifest(with_config=False)
    calls = _patch(monkeypatch, manifest)
    body = DocumentExtractRequest(
        agent_id=str(manifest.agent.id),
        expected_version_id=str(manifest.publishedVersionId),
        task="T",
    )
    with pytest.raises(HTTPException) as info:
        await routes.document_extract(body, _principal(), None, SimpleNamespace(skills=[]))
    assert info.value.status_code == 409
    assert calls["executed_manifests"] == []


async def test_document_extract_test_mode_asks_gateway_for_test_manifest(monkeypatch) -> None:
    manifest = _manifest()
    calls = _patch(monkeypatch, manifest)
    body = DocumentExtractRequest(
        agent_id=str(manifest.agent.id),
        expected_version_id=str(manifest.publishedVersionId),
        task="T",
        test=True,
    )
    await routes.document_extract(body, _principal(), None, SimpleNamespace(skills=[]))
    assert calls["require"] == [(str(manifest.agent.id), True)]


def test_document_extract_request_is_strict() -> None:
    with pytest.raises(ValidationError):
        DocumentExtractRequest.model_validate(
            {
                "agent_id": "a",
                "expected_version_id": "v",
                "task": "t",
                "tools": ["mcp__aiq__save_voucher"],
            }
        )
