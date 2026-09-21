from __future__ import annotations

import errno
import json
import os
from types import SimpleNamespace
from typing import Any
from uuid import uuid4

import pytest
from claude_agent_sdk import CLIConnectionError
from pydantic import ValidationError

import asyncio

from aiq_agent.audit.store import AuditStore
from aiq_agent.config import Settings
from aiq_agent.runtime.executors import (
    ClaudeSdkAgentExecutor,
    ExecutionTimeoutError,
    OllamaAgentExecutor,
    ProviderResolutionError,
    build_agent_executor,
)
from aiq_agent.runtime.manifest import ExecutionManifest
from aiq_agent.skills import Skill, SkillRegistry


def manifest_data(**overrides):
    data = {
        "userId": str(uuid4()), "tenantId": str(uuid4()),
        "agent": {"id": str(uuid4()), "slug": "dynamic-test", "name": "Dynamic Test",
                  "description": None, "iconMetadata": {}},
        "publishedVersionId": str(uuid4()), "version": 2, "provider": "ollama",
        "model": "model-a", "systemPrompt": "PROMPT-A", "temperature": 0.3,
        "maxTurns": 4, "timeoutSeconds": 30, "maxTokens": 512,
        "memoryConfig": {"mode": "session"}, "tools": [], "skills": [], "knowledge": [],
    }
    data.update(overrides)
    return data


def test_manifest_is_strict_and_requires_security_fields():
    ExecutionManifest.model_validate(manifest_data())
    with pytest.raises(ValidationError):
        ExecutionManifest.model_validate(manifest_data(clientTenant="forged"))
    missing = manifest_data()
    del missing["publishedVersionId"]
    with pytest.raises(ValidationError):
        ExecutionManifest.model_validate(missing)
    missing = manifest_data()
    del missing["model"]
    with pytest.raises(ValidationError):
        ExecutionManifest.model_validate(missing)


def test_unknown_provider_fails_closed():
    with pytest.raises(ValidationError):
        ExecutionManifest.model_validate(manifest_data(provider="unknown"))


# ---------------------------------------------------------------------
# Company Profile ("công ty chủ thể") — companyProfile is server-resolved
# by Gateway from the authenticated tenant_id and bundled into the
# manifest; the user can never supply or override it. Absent (None) must
# render as an explicit not_configured, never a guessed identity.
# ---------------------------------------------------------------------

def test_manifest_parses_company_profile_when_present():
    from aiq_agent.runtime.manifest import ManifestCompanyProfile

    manifest = ExecutionManifest.model_validate(manifest_data(companyProfile={
        "legalName": "Công ty TNHH Việt Đăng", "taxCode": "0301234567", "shortName": "Việt Đăng",
        "aliases": ["AIQ"], "registeredAddress": "123 Nguyễn Huệ", "legalRepresentative": "Nguyễn Văn A",
        "representativeTitle": "Giám đốc", "phone": None, "email": None, "website": None,
    }))
    assert isinstance(manifest.companyProfile, ManifestCompanyProfile)
    assert manifest.companyProfile.legalName == "Công ty TNHH Việt Đăng"
    assert manifest.companyProfile.taxCode == "0301234567"


def test_manifest_company_profile_defaults_to_none_when_absent():
    manifest = ExecutionManifest.model_validate(manifest_data())
    assert manifest.companyProfile is None


def test_company_context_block_renders_not_configured_when_profile_absent():
    from aiq_agent.runtime.executors import _company_context_block

    manifest = ExecutionManifest.model_validate(manifest_data())
    block = _company_context_block(manifest)
    assert block.endswith("company_identity: not_configured")


def test_company_context_block_renders_trusted_fields_when_profile_present():
    from aiq_agent.runtime.executors import _company_context_block

    manifest = ExecutionManifest.model_validate(manifest_data(companyProfile={
        "legalName": "Công ty TNHH Việt Đăng", "taxCode": "0301234567", "shortName": "Việt Đăng",
        "aliases": ["AIQ", "Cát Tường AI"], "registeredAddress": "123 Nguyễn Huệ, Q1, TP.HCM",
        "legalRepresentative": "Nguyễn Văn A", "representativeTitle": "Giám đốc",
        "phone": None, "email": None, "website": None,
    }))
    block = _company_context_block(manifest)
    assert "TRUSTED" in block
    assert "legal_name: Công ty TNHH Việt Đăng" in block
    assert "tax_code: 0301234567" in block
    assert "aliases: AIQ, Cát Tường AI" in block
    assert "registered_address: 123 Nguyễn Huệ, Q1, TP.HCM" in block
    assert not block.endswith("company_identity: not_configured")


@pytest.mark.asyncio
async def test_ollama_executor_prepends_company_context_to_system_prompt(monkeypatch):
    captured = []

    class FakeOllama:
        def __init__(self, **kwargs):
            pass

        async def chat(self, messages, **kwargs):
            captured.append(messages[0].content)
            return SimpleNamespace(content="reply")

        async def aclose(self):
            pass

    monkeypatch.setattr("aiq_agent.runtime.executors.OllamaLlmService", FakeOllama)
    settings = Settings(embedding_enabled=False)
    manifest = ExecutionManifest.model_validate(manifest_data(companyProfile={
        "legalName": "Công ty TNHH Việt Đăng", "taxCode": "0301234567", "shortName": None,
        "aliases": [], "registeredAddress": None, "legalRepresentative": None,
        "representativeTitle": None, "phone": None, "email": None, "website": None,
    }))
    await build_agent_executor(manifest, settings, SkillRegistry()).execute(prompt="hi", token="t")
    assert "legal_name: Công ty TNHH Việt Đăng" in captured[0]
    assert "tax_code: 0301234567" in captured[0]


def test_provider_factory_selects_ollama_and_claude_sdk():
    settings = Settings(embedding_enabled=False)
    skills = SkillRegistry()
    assert isinstance(build_agent_executor(ExecutionManifest.model_validate(manifest_data()), settings, skills), OllamaAgentExecutor)
    claude = ExecutionManifest.model_validate(manifest_data(provider="claude_sdk"))
    assert isinstance(build_agent_executor(claude, settings, skills), ClaudeSdkAgentExecutor)


@pytest.mark.asyncio
async def test_two_manifests_change_ollama_prompt_and_model_without_process_config(monkeypatch):
    captured = []

    class FakeOllama:
        def __init__(self, **kwargs):
            self.model = kwargs["model"]
        async def chat(self, messages, **kwargs):
            captured.append((self.model, messages[0].content, kwargs))
            return SimpleNamespace(content=f"reply:{self.model}")
        async def aclose(self):
            pass

    monkeypatch.setattr("aiq_agent.runtime.executors.OllamaLlmService", FakeOllama)
    settings = Settings(embedding_enabled=False, ollama_model="process-default")
    skills = SkillRegistry()
    first = ExecutionManifest.model_validate(manifest_data(model="db-model-one", systemPrompt="DB PROMPT ONE"))
    second = ExecutionManifest.model_validate(manifest_data(model="db-model-two", systemPrompt="DB PROMPT TWO"))
    await build_agent_executor(first, settings, skills).execute(prompt="same", token="token")
    await build_agent_executor(second, settings, skills).execute(prompt="same", token="token")
    # Each call's system prompt always carries its OWN manifest's
    # systemPrompt first — the trusted company-context block
    # (_company_context_block) is always appended after it, so this checks
    # the per-manifest prefix rather than an exact match, without hardcoding
    # that block's wording into an otherwise-unrelated test.
    assert [(row[0], row[1].startswith("DB PROMPT ONE")) for row in captured[:1]] == [("db-model-one", True)]
    assert [(row[0], row[1].startswith("DB PROMPT TWO")) for row in captured[1:]] == [("db-model-two", True)]
    assert captured[0][2] == {"temperature": 0.3, "max_tokens": 512}


@pytest.mark.asyncio
async def test_ollama_executor_reports_real_token_usage_no_fabricated_cost(monkeypatch):
    from aiq_agent.llm.base import ChatResult, LlmUsage

    class FakeOllamaWithUsage:
        def __init__(self, **kwargs): pass
        async def chat(self, messages, **kwargs):
            return ChatResult(content="ok", model="m", provider="ollama",
                               usage=LlmUsage(input_tokens=123, output_tokens=45))
        async def aclose(self): pass

    monkeypatch.setattr("aiq_agent.runtime.executors.OllamaLlmService", FakeOllamaWithUsage)
    settings = Settings(embedding_enabled=False)
    manifest = ExecutionManifest.model_validate(manifest_data())
    result = await build_agent_executor(manifest, settings, SkillRegistry()).execute(prompt="x", token="t")
    assert result.input_tokens == 123
    assert result.output_tokens == 45
    # No per-token pricing exists for a self-hosted Ollama deployment --
    # None (unknown), never a made-up number.
    assert result.estimated_cost_usd is None
    assert result.tools_used == []
    assert result.delegations == []


@pytest.mark.asyncio
async def test_ungranted_or_missing_tool_and_unassigned_skill_fail_closed(monkeypatch):
    settings = Settings(embedding_enabled=False)
    installed_skill = Skill("installed", "", "ONLY INSTALLED")
    registry = SkillRegistry((installed_skill,))
    missing_tool = {"id": str(uuid4()), "code": "mcp__aiq__missing", "name": "Missing",
                    "description": None, "category": None, "riskTier": "low"}
    manifest = ExecutionManifest.model_validate(manifest_data(tools=[missing_tool]))
    with pytest.raises(ProviderResolutionError, match="no installed handler"):
        await build_agent_executor(manifest, settings, registry).execute(prompt="x", token="t")

    manifest = ExecutionManifest.model_validate(manifest_data(skills=["not-assigned-on-disk"]))
    with pytest.raises(ProviderResolutionError, match="not installed"):
        await build_agent_executor(manifest, settings, registry).execute(prompt="x", token="t")


@pytest.mark.asyncio
async def test_claude_sdk_execution_enforces_manifest_timeout(monkeypatch):
    from aiq_agent.runtime import executors as executors_module

    class HangingClient:
        def __init__(self, **kwargs):
            pass
        async def __aenter__(self):
            return self
        async def __aexit__(self, *exc):
            return False
        async def query(self, *_args, **_kwargs):
            return None
        async def receive_response(self):
            await asyncio.sleep(10)
            if False:
                yield None  # pragma: no cover - makes this an async generator

    monkeypatch.setattr(executors_module, "ClaudeSDKClient", HangingClient)
    settings = Settings(embedding_enabled=False)
    skills = SkillRegistry()
    manifest = ExecutionManifest.model_validate(manifest_data(provider="claude_sdk", timeoutSeconds=1))
    executor = build_agent_executor(manifest, settings, skills)
    with pytest.raises(ExecutionTimeoutError, match="exceeded its configured timeout"):
        await executor.execute(prompt="x", token="t")


class _StubClient:
    """Captures the ClaudeAgentOptions a real spawn would receive, without
    ever touching a subprocess. Mirrors HangingClient's shape above but
    completes immediately with one assistant message."""

    captured_options: list[Any] = []

    def __init__(self, **kwargs):
        _StubClient.captured_options.append(kwargs.get("options"))

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        return False

    async def query(self, *_args, **_kwargs):
        return None

    async def receive_response(self):
        from claude_agent_sdk import AssistantMessage, TextBlock

        yield AssistantMessage(content=[TextBlock(text="ok")], model="m")


@pytest.mark.asyncio
async def test_claude_sdk_writes_system_prompt_to_file_not_argv(monkeypatch, tmp_path):
    """The CLI transport puts a string system_prompt directly on argv, which
    is exactly what overflowed for the real accounting agent (see
    test_claude_sdk_survives_oversized_skill_content_without_e2big below).
    ClaudeAgentOptions must always receive the file form, never the raw
    string, regardless of size."""
    from aiq_agent.runtime import executors as executors_module

    _StubClient.captured_options = []
    monkeypatch.setattr(executors_module, "ClaudeSDKClient", _StubClient)
    settings = Settings(embedding_enabled=False)
    manifest = ExecutionManifest.model_validate(
        manifest_data(provider="claude_sdk", systemPrompt="Bạn là trợ lý kế toán."))
    executor = build_agent_executor(manifest, settings, SkillRegistry())

    result = await executor.execute(prompt="hello", token="t")

    assert result.final_answer == "ok"
    [options] = _StubClient.captured_options
    assert isinstance(options.system_prompt, dict)
    assert options.system_prompt["type"] == "file"
    # File must be gone once the subprocess has exited -- it's a transient
    # transport artifact, not a place to leave a copy of the prompt lying
    # around on disk.
    assert not os.path.exists(options.system_prompt["path"])


@pytest.mark.asyncio
async def test_claude_sdk_survives_oversized_skill_content_without_e2big(monkeypatch):
    """Regression for the real bug: 44 skills on the Accounting Agent summed
    to ~235 KB, over Linux's 128 KB MAX_ARG_STRLEN single-argv-element cap.
    Reproduces that shape with a synthetic 200 KB skill body and asserts the
    resulting ClaudeAgentOptions.system_prompt is STILL the small file-path
    form, not a huge raw string -- i.e. this configuration can never trigger
    E2BIG via argv, no matter how large the manifest's skills/knowledge get."""
    from aiq_agent.runtime import executors as executors_module

    _StubClient.captured_options = []
    monkeypatch.setattr(executors_module, "ClaudeSDKClient", _StubClient)
    settings = Settings(embedding_enabled=False)
    huge_skill = Skill("accounting-vietnam", "", "X" * 200_000)
    registry = SkillRegistry((huge_skill,))
    manifest = ExecutionManifest.model_validate(
        manifest_data(provider="claude_sdk", skills=["accounting-vietnam"]))
    executor = build_agent_executor(manifest, settings, registry)

    await executor.execute(prompt="hello", token="t")

    [options] = _StubClient.captured_options
    assert isinstance(options.system_prompt, dict)
    # The argv-visible payload is just a temp file path -- nowhere near the
    # 128 KB single-argument cap the 200 KB skill body would have blown past.
    assert len(options.system_prompt["path"]) < 1024


@pytest.mark.asyncio
async def test_claude_sdk_e2big_maps_to_safe_configuration_too_large_error(monkeypatch):
    """If argv still overflows for some other reason (a future large
    --mcp-config, say), the raw CLIConnectionError/E2BIG must not leak to
    callers as an opaque failure -- it becomes ClaudeSdkConfigurationTooLargeError
    carrying only sizes/counts, never prompt/knowledge/skill/token content."""
    from aiq_agent.runtime import executors as executors_module
    from aiq_agent.runtime.executors import ClaudeSdkConfigurationTooLargeError

    class E2bigClient:
        def __init__(self, **kwargs):
            pass
        async def __aenter__(self):
            try:
                raise OSError(errno.E2BIG, "Argument list too long")
            except OSError as os_err:
                raise CLIConnectionError(f"Failed to start Claude Code: {os_err}") from os_err
        async def __aexit__(self, *exc):
            return False

    monkeypatch.setattr(executors_module, "ClaudeSDKClient", E2bigClient)
    settings = Settings(embedding_enabled=False)
    manifest = ExecutionManifest.model_validate(manifest_data(provider="claude_sdk"))
    executor = build_agent_executor(manifest, settings, SkillRegistry())

    with pytest.raises(ClaudeSdkConfigurationTooLargeError) as excinfo:
        await executor.execute(prompt="hello", token="secret-caller-token")

    diagnostics = excinfo.value.diagnostics
    assert set(diagnostics) == {
        "argv_bytes", "env_bytes", "tool_count", "skill_count",
        "knowledge_count", "delegate_count", "system_prompt_bytes", "mcp_config_bytes",
    }
    assert all(isinstance(v, int) for v in diagnostics.values())
    # No secret/content leakage into the diagnostics payload.
    serialized = json.dumps(diagnostics)
    assert "secret-caller-token" not in serialized
    assert "PROMPT-A" not in serialized  # manifest_data()'s systemPrompt


@pytest.mark.asyncio
async def test_claude_sdk_non_e2big_cli_errors_are_not_recategorized(monkeypatch):
    """A CLIConnectionError NOT caused by E2BIG (e.g. the CLI binary missing)
    must propagate as-is -- collapsing every provider failure into
    claude_sdk_configuration_too_large would hide the real cause."""
    from aiq_agent.runtime import executors as executors_module
    from aiq_agent.runtime.executors import ClaudeSdkConfigurationTooLargeError

    class NotFoundClient:
        def __init__(self, **kwargs):
            pass
        async def __aenter__(self):
            try:
                raise OSError(errno.ENOENT, "No such file or directory")
            except OSError as os_err:
                raise CLIConnectionError(f"Failed to start Claude Code: {os_err}") from os_err
        async def __aexit__(self, *exc):
            return False

    monkeypatch.setattr(executors_module, "ClaudeSDKClient", NotFoundClient)
    settings = Settings(embedding_enabled=False)
    manifest = ExecutionManifest.model_validate(manifest_data(provider="claude_sdk"))
    executor = build_agent_executor(manifest, settings, SkillRegistry())

    with pytest.raises(CLIConnectionError) as excinfo:
        await executor.execute(prompt="hello", token="t")
    assert not isinstance(excinfo.value, ClaudeSdkConfigurationTooLargeError)


def test_tool_execution_prefers_gateway_minted_token_over_caller_token(monkeypatch):
    from aiq_agent.runtime import executors as executors_module

    captured_tokens = []

    def fake_build_tools(*, token, gateway_base):
        captured_tokens.append(token)
        return []

    monkeypatch.setattr(executors_module, "build_tools", fake_build_tools)
    settings = Settings(embedding_enabled=False)

    with_minted = ExecutionManifest.model_validate(
        manifest_data(toolExecutionToken="gateway-minted-tool-token"))
    executors_module._resolve_tools(with_minted, "callers-own-session-token", settings.gateway_base_url)
    assert captured_tokens[-1] == "gateway-minted-tool-token"

    without_minted = ExecutionManifest.model_validate(manifest_data())
    executors_module._resolve_tools(without_minted, "callers-own-session-token", settings.gateway_base_url)
    assert captured_tokens[-1] == "callers-own-session-token"


def _workflow(steps, name="wf"):
    return {"id": str(uuid4()), "name": name, "definition": {"steps": steps, "requiresApproval": False}}


def test_no_workflow_is_a_no_op():
    from aiq_agent.runtime import executors as executors_module

    manifest = ExecutionManifest.model_validate(manifest_data())
    assert executors_module._resolve_workflow(manifest) == ""


def test_workflow_prompt_steps_are_injected_in_order():
    from aiq_agent.runtime import executors as executors_module

    manifest = ExecutionManifest.model_validate(manifest_data(workflow=_workflow(
        [{"type": "prompt", "text": "First, greet the user."}, {"type": "prompt", "text": "Then answer."}],
        name="Greeting Workflow",
    )))
    block = executors_module._resolve_workflow(manifest)
    assert "Greeting Workflow" in block
    assert block.index("First, greet the user.") < block.index("Then answer.")


def test_workflow_unknown_step_type_fails_closed():
    from aiq_agent.runtime import executors as executors_module

    manifest = ExecutionManifest.model_validate(manifest_data(workflow=_workflow(
        [{"type": "teleport", "text": "nope"}])))
    with pytest.raises(ProviderResolutionError, match="unknown type"):
        executors_module._resolve_workflow(manifest)


def test_workflow_require_knowledge_fails_closed_without_knowledge():
    from aiq_agent.runtime import executors as executors_module

    manifest = ExecutionManifest.model_validate(manifest_data(
        workflow=_workflow([{"type": "require_knowledge"}]), knowledge=[]))
    with pytest.raises(ProviderResolutionError, match="requires attached knowledge"):
        executors_module._resolve_workflow(manifest)


def test_workflow_require_tool_fails_closed_when_not_granted():
    from aiq_agent.runtime import executors as executors_module

    manifest = ExecutionManifest.model_validate(manifest_data(
        workflow=_workflow([{"type": "require_tool", "tool": "mcp__aiq__get_contract"}]), tools=[]))
    with pytest.raises(ProviderResolutionError, match="requires granted tool"):
        executors_module._resolve_workflow(manifest)


@pytest.mark.asyncio
async def test_two_manifests_with_different_workflows_produce_different_prompts_no_agent_name_branching(monkeypatch):
    # Proves "do not hard-code workflow routing by agent name": both agents
    # share the exact same code path (build_agent_executor -> execute), and
    # the ONLY thing that differs is manifest.workflow data from the DB.
    captured = []

    class FakeOllama:
        def __init__(self, **kwargs):
            self.model = kwargs["model"]
        async def chat(self, messages, **kwargs):
            captured.append(messages[0].content)
            return SimpleNamespace(content="ok")
        async def aclose(self):
            pass

    monkeypatch.setattr("aiq_agent.runtime.executors.OllamaLlmService", FakeOllama)
    settings = Settings(embedding_enabled=False)
    skills = SkillRegistry()

    agent_a = ExecutionManifest.model_validate(manifest_data(
        agent={"id": str(uuid4()), "slug": "agent-a", "name": "Agent A", "description": None, "iconMetadata": {}},
        workflow=_workflow([{"type": "prompt", "text": "STEP FOR AGENT A"}], name="Workflow A")))
    agent_b = ExecutionManifest.model_validate(manifest_data(
        agent={"id": str(uuid4()), "slug": "agent-b", "name": "Agent B", "description": None, "iconMetadata": {}},
        workflow=None))

    await build_agent_executor(agent_a, settings, skills).execute(prompt="x", token="t")
    await build_agent_executor(agent_b, settings, skills).execute(prompt="x", token="t")

    assert "STEP FOR AGENT A" in captured[0]
    assert "STEP FOR AGENT A" not in captured[1]
    assert "WORKFLOW" not in captured[1]


def _delegate(slug="specialist-a", name="Specialist A"):
    return {"id": str(uuid4()), "slug": slug, "name": name, "description": None}


def test_no_delegates_builds_no_tool():
    from aiq_agent.runtime.executors import _build_delegate_tool

    manifest = ExecutionManifest.model_validate(manifest_data())
    settings = Settings(embedding_enabled=False)
    assert _build_delegate_tool(manifest, settings, SkillRegistry(), "token") is None


def test_delegate_tool_lists_specialists_and_rejects_unknown_slug():
    from aiq_agent.runtime.executors import _build_delegate_tool

    manifest = ExecutionManifest.model_validate(manifest_data(delegates=[_delegate()]))
    settings = Settings(embedding_enabled=False)
    delegate_tool = _build_delegate_tool(manifest, settings, SkillRegistry(), "token")
    assert delegate_tool is not None
    assert delegate_tool.name == "delegate_to_agent"
    assert "specialist-a" in delegate_tool.description


@pytest.mark.asyncio
async def test_delegate_tool_rejects_unauthorized_specialist(monkeypatch):
    from aiq_agent.runtime import executors as executors_module

    manifest = ExecutionManifest.model_validate(manifest_data(delegates=[_delegate(slug="specialist-a")]))
    settings = Settings(embedding_enabled=False)
    delegate_tool = executors_module._build_delegate_tool(manifest, settings, SkillRegistry(), "token")
    result = await delegate_tool.handler({"specialist_slug": "not-a-real-specialist", "task": "do X"})
    assert "Unknown specialist" in result["content"][0]["text"]


@pytest.mark.asyncio
async def test_delegate_tool_executes_authorized_specialist_and_returns_its_answer(monkeypatch):
    from aiq_agent.runtime import executors as executors_module

    specialist_id = str(uuid4())
    manifest = ExecutionManifest.model_validate(manifest_data(
        delegates=[{"id": specialist_id, "slug": "specialist-a", "name": "Specialist A", "description": None}]))
    settings = Settings(embedding_enabled=False)

    specialist_manifest = ExecutionManifest.model_validate(manifest_data(
        agent={"id": specialist_id, "slug": "specialist-a", "name": "Specialist A", "description": None, "iconMetadata": {}},
        systemPrompt="SPECIALIST PROMPT", delegates=[]))

    captured_calls = []

    async def fake_fetch_delegate_manifest(*, gateway_base, token, router_agent_id, specialist_agent_id):
        captured_calls.append((router_agent_id, specialist_agent_id, token))
        return specialist_manifest

    class FakeOllama:
        def __init__(self, **kwargs): pass
        async def chat(self, messages, **kwargs):
            return SimpleNamespace(content="SPECIALIST ANSWER")
        async def aclose(self): pass

    monkeypatch.setattr(executors_module, "fetch_delegate_manifest", fake_fetch_delegate_manifest)
    monkeypatch.setattr(executors_module, "OllamaLlmService", FakeOllama)

    delegate_tool = executors_module._build_delegate_tool(manifest, settings, SkillRegistry(), "caller-token")
    result = await delegate_tool.handler({"specialist_slug": "specialist-a", "task": "please help"})

    assert captured_calls == [(str(manifest.agent.id), specialist_id, "caller-token")]
    assert result["content"][0]["text"] == "[Specialist A]: SPECIALIST ANSWER"


@pytest.mark.asyncio
async def test_ollama_executor_fails_closed_when_delegates_present():
    manifest = ExecutionManifest.model_validate(manifest_data(delegates=[_delegate()]))
    settings = Settings(embedding_enabled=False)
    executor = build_agent_executor(manifest, settings, SkillRegistry())
    with pytest.raises(ProviderResolutionError, match="does not yet support router delegation"):
        await executor.execute(prompt="x", token="t")


class FakePipe:
    def __init__(self): self.value = None
    def lpush(self, _key, value): self.value = value; return self
    def ltrim(self, *_args): return self
    async def execute(self): return []


class FakeRedis:
    def __init__(self): self.pipe = FakePipe()
    def pipeline(self): return self.pipe


@pytest.mark.asyncio
async def test_execution_audit_contains_manifest_identity_without_prompt():
    redis = FakeRedis()
    store = AuditStore(redis=redis)
    await store.write(event="agent_execution", name="dynamic", user_prefix="user", ms=1, ok=True,
        user_id="user-id", tenant_id="tenant-id", agent_id="agent-id", version=7,
        provider="ollama", model="db-model", detail=None)
    value = json.loads(redis.pipe.value)
    assert {key: value[key] for key in ("user_id", "tenant_id", "agent_id", "version", "provider", "model")} == {
        "user_id": "user-id", "tenant_id": "tenant-id", "agent_id": "agent-id",
        "version": 7, "provider": "ollama", "model": "db-model"}
    assert value["detail"] is None


class _FakeHitlResponse:
    def __init__(self, status_code, body):
        self.status_code = status_code
        self._body = body

    def json(self):
        return self._body


def _fake_hitl_client(outcome_body, status_code=200):
    class _Client:
        def __init__(self, **kwargs): pass
        async def __aenter__(self): return self
        async def __aexit__(self, *exc): return False
        async def post(self, url, headers=None, json=None):
            _Client.last_call = (url, headers, json)
            return _FakeHitlResponse(status_code, outcome_body)
    return _Client


@pytest.mark.asyncio
async def test_hitl_allowed_outcome_runs_the_real_tool_handler(monkeypatch):
    from aiq_agent.runtime import executors as executors_module

    async def real_handler(args):
        return {"content": [{"type": "text", "text": "REAL RESULT"}]}

    from claude_agent_sdk import tool as sdk_tool

    @sdk_tool("get_contract", "d", {"contract_id": str})
    async def fake_tool(args):
        return await real_handler(args)

    monkeypatch.setattr(executors_module.httpx, "AsyncClient", _fake_hitl_client({"outcome": "allowed"}))
    gated = executors_module._wrap_tool_with_hitl_gate(fake_tool, agent_id="a1", token="t", gateway_base="http://gw")
    result = await gated.handler({"contract_id": "x"})
    assert result["content"][0]["text"] == "REAL RESULT"


@pytest.mark.asyncio
async def test_hitl_pending_outcome_blocks_the_real_tool_handler(monkeypatch):
    from aiq_agent.runtime import executors as executors_module
    from claude_agent_sdk import tool as sdk_tool

    called = []

    @sdk_tool("save_contract", "d", {"fields": str})
    async def fake_tool(args):
        called.append(args)
        return {"content": [{"type": "text", "text": "SHOULD NOT RUN"}]}

    monkeypatch.setattr(executors_module.httpx, "AsyncClient",
        _fake_hitl_client({"outcome": "pending", "actionId": "abc", "gateName": "Big Payment"}))
    gated = executors_module._wrap_tool_with_hitl_gate(fake_tool, agent_id="a1", token="t", gateway_base="http://gw")
    result = await gated.handler({"fields": "x"})
    assert called == []
    assert "requires human approval" in result["content"][0]["text"]
    assert "Big Payment" in result["content"][0]["text"]


@pytest.mark.asyncio
async def test_hitl_rejected_outcome_blocks_the_real_tool_handler(monkeypatch):
    from aiq_agent.runtime import executors as executors_module
    from claude_agent_sdk import tool as sdk_tool

    called = []

    @sdk_tool("save_contract", "d", {"fields": str})
    async def fake_tool(args):
        called.append(args)
        return {"content": [{"type": "text", "text": "SHOULD NOT RUN"}]}

    monkeypatch.setattr(executors_module.httpx, "AsyncClient",
        _fake_hitl_client({"outcome": "rejected", "actionId": "abc", "gateName": "Big Payment"}))
    gated = executors_module._wrap_tool_with_hitl_gate(fake_tool, agent_id="a1", token="t", gateway_base="http://gw")
    result = await gated.handler({"fields": "x"})
    assert called == []
    assert "REJECTED" in result["content"][0]["text"]


@pytest.mark.asyncio
async def test_hitl_gateway_failure_fails_closed(monkeypatch):
    from aiq_agent.runtime import executors as executors_module
    from claude_agent_sdk import tool as sdk_tool

    called = []

    @sdk_tool("save_contract", "d", {"fields": str})
    async def fake_tool(args):
        called.append(args)
        return {"content": [{"type": "text", "text": "SHOULD NOT RUN"}]}

    monkeypatch.setattr(executors_module.httpx, "AsyncClient", _fake_hitl_client({}, status_code=500))
    gated = executors_module._wrap_tool_with_hitl_gate(fake_tool, agent_id="a1", token="t", gateway_base="http://gw")
    result = await gated.handler({"fields": "x"})
    assert called == []
    assert "blocked" in result["content"][0]["text"].lower()


@pytest.mark.asyncio
async def test_audit_store_persists_usage_cost_tools_and_delegations():
    redis = FakeRedis()
    store = AuditStore(redis=redis)
    await store.write(
        event="agent_execution", name="dynamic", user_prefix="user", ms=1, ok=True,
        user_id="u", tenant_id="t", agent_id="a", version=1, provider="claude_sdk", model="m",
        input_tokens=100, output_tokens=20, estimated_cost_usd=0.0034,
        tools_used=["mcp__aiq__get_contract"], delegations=["specialist-a"], workflow_id="wf-1",
    )
    value = json.loads(redis.pipe.value)
    assert value["input_tokens"] == 100
    assert value["output_tokens"] == 20
    assert value["estimated_cost_usd"] == 0.0034
    assert value["tools_used"] == ["mcp__aiq__get_contract"]
    assert value["delegations"] == ["specialist-a"]
    assert value["workflow_id"] == "wf-1"
