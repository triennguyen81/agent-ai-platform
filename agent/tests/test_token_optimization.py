"""Token optimisation Phases 2-6 (docs/audits/TOKEN_OPTIMIZATION_PLAN.md)."""

from __future__ import annotations

from pathlib import Path
from typing import Any
from uuid import uuid4

import pytest

from aiq_agent.config import Settings
from aiq_agent.runtime.executors import (
    _EXTRACTION_INSTRUCTIONS,
    _resolve_skills,
    build_agent_executor,
    extraction_manifest,
)
from aiq_agent.runtime.manifest import ExecutionManifest
from aiq_agent.skills import SkillRegistry
from aiq_agent.skills.loader import build_registry

from .test_dynamic_runtime import manifest_data

SKILLS_ROOT = Path(__file__).resolve().parents[1] / "skills"
# The production "Trợ lý kế toán" skill set (read-only audit, 2026-10-11).
LIVE_SKILLS = [
    "accounting-vietnam", "accounting-vn-style", "compliance-citation", "contract-checklist-builder",
    "contract-extractor", "tax-calculation", "url-ingest", "vietnamese-style", "voucher-invoice-vat",
]
# Verbatim skill references of the Gateway extraction prompts
# (DocumentExtraction.BuildManifestPrompt / VoucherExtractJobRunner.BuildExtractionPrompt).
MANIFEST_PROMPT = (
    "Bạn là chuyên viên kế toán VN. Xử lý raw OCR text của 1 chứng từ bên dưới.\n"
    "**BLOCK 1** — Trích xuất fields theo đúng SCHEMA ...\n"
    "**BLOCK 2** — Áp dụng skill `journal-entry-formatter` + `accounting-vietnam` để đề xuất bút toán N/C theo TT200\n"
    "===== RAW OCR TEXT =====\nHOA DON GTGT ... hợp đồng số 12 ... thuế VAT 10%\n===== END =====\n"
)
LEGACY_PROMPT = MANIFEST_PROMPT.replace(
    "**BLOCK 1** — Trích xuất fields theo đúng SCHEMA ...",
    "**BLOCK 1** — Áp dụng skill `voucher-invoice-vat`, đúng phần mô tả cho voucher_type đã xác định")


def _snapshot(code: str, content: str, kind: str = "declarative") -> dict[str, Any]:
    return {"code": code, "sourceKind": kind, "contentSha256": "a" * 64, "content": content}


def _manifest(**overrides: Any) -> ExecutionManifest:
    data = manifest_data(provider="claude_sdk", skills=list(LIVE_SKILLS), maxTurns=8)
    data.update(overrides)
    return ExecutionManifest.model_validate(data)


# ------------------------------------------------------------------ Phase 2

def test_lean_extraction_keeps_only_skills_the_prompt_names() -> None:
    lean = extraction_manifest(_manifest(), MANIFEST_PROMPT, lean=True)
    # Named and assigned → kept. Named but not assigned (journal-entry-formatter) → not added.
    assert lean.skills == ["accounting-vietnam"]
    # Mentioning "hợp đồng"/"thuế" in the DOCUMENT text does not pull contract/tax skills:
    # only an explicit skill code counts.
    assert "contract-extractor" not in lean.skills and "tax-calculation" not in lean.skills

    legacy = extraction_manifest(_manifest(), LEGACY_PROMPT, lean=True)
    assert legacy.skills == ["accounting-vietnam", "voucher-invoice-vat"]


def test_lean_extraction_is_a_subset_and_stays_tool_less() -> None:
    m = _manifest(tools=[{"id": str(uuid4()), "code": "mcp__aiq__save_voucher", "name": "save", "description": None,
                          "category": None, "riskTier": "write"}])
    for lean in (True, False):
        out = extraction_manifest(m, MANIFEST_PROMPT, lean=lean)
        assert set(out.skills) <= set(m.skills)
        assert out.tools == [] and out.delegates == [] and out.workflow is None and out.maxTurns == 2
        assert out.tenantId == m.tenantId and out.agent.id == m.agent.id and out.departmentId == m.departmentId


def test_rollback_flag_keeps_every_skill_and_the_original_prompt() -> None:
    m = _manifest()
    out = extraction_manifest(m, MANIFEST_PROMPT, lean=False)
    assert out.skills == m.skills and out.systemPrompt == m.systemPrompt


def test_declarative_tenant_skills_always_reach_extraction_but_never_twice() -> None:
    m = _manifest(skills=[*LIVE_SKILLS, "kt-quy-tac", "kt-inline"],
                  skillSnapshots=[_snapshot("kt-quy-tac", "QUY TẮC CÔNG TY"), _snapshot("kt-inline", "ĐÃ CHÈN")])
    prompt = MANIFEST_PROMPT + "===== SKILL kt-inline =====\nĐÃ CHÈN\n===== END SKILL =====\n"
    out = extraction_manifest(m, prompt, lean=True)
    assert "kt-quy-tac" in out.skills          # unreferenced, but a tenant rule → kept
    assert "kt-inline" not in out.skills       # already inlined by the Gateway → not duplicated
    assert [s.code for s in out.skillSnapshots] == ["kt-quy-tac"]


def test_skill_reference_needs_an_exact_code_boundary() -> None:
    m = _manifest(skills=["accounting-vietnam"])
    assert extraction_manifest(m, "dùng accounting-vietnamese-v2", lean=True).skills == []
    assert extraction_manifest(m, "dùng `accounting-vietnam`.", lean=True).skills == ["accounting-vietnam"]


def test_lean_extraction_keeps_agent_rules_and_adds_extraction_instructions() -> None:
    m = _manifest(systemPrompt="QUY TẮC: xác định công ty mình là bên nào.")
    out = extraction_manifest(m, MANIFEST_PROMPT, lean=True)
    assert out.systemPrompt.startswith("QUY TẮC: xác định công ty mình là bên nào.")
    assert _EXTRACTION_INSTRUCTIONS in out.systemPrompt


def test_lean_extraction_shrinks_the_real_skill_block() -> None:
    registry = build_registry(SKILLS_ROOT)
    m = _manifest()
    full = _resolve_skills(extraction_manifest(m, MANIFEST_PROMPT, lean=False), registry).block
    lean = _resolve_skills(extraction_manifest(m, MANIFEST_PROMPT, lean=True), registry).block
    assert len(lean) < 0.25 * len(full)


class _OptionsClient:
    captured: list[Any] = []

    def __init__(self, **kwargs: Any) -> None:
        self.captured.append(kwargs.get("options"))

    async def __aenter__(self) -> "_OptionsClient":
        return self

    async def __aexit__(self, *exc: Any) -> bool:
        return False

    async def query(self, *_a: Any, **_k: Any) -> None:
        return None

    async def receive_response(self):
        from claude_agent_sdk import AssistantMessage, TextBlock

        yield AssistantMessage(content=[TextBlock(text="```json\n{}\n```")], model="m")


@pytest.mark.asyncio
async def test_cli_skill_discovery_can_be_turned_off(monkeypatch) -> None:
    from aiq_agent.runtime import executors as executors_module

    monkeypatch.setattr(executors_module, "ClaudeSDKClient", _OptionsClient)
    registry = build_registry(SKILLS_ROOT)
    m = extraction_manifest(_manifest(), MANIFEST_PROMPT, lean=True)

    _OptionsClient.captured = []
    executor = build_agent_executor(m, Settings(embedding_enabled=False), registry)
    executor.cli_skill_discovery = False
    await executor.execute(prompt=MANIFEST_PROMPT, token="t")
    executor = build_agent_executor(m, Settings(embedding_enabled=False), registry)
    await executor.execute(prompt=MANIFEST_PROMPT, token="t")

    off, on = _OptionsClient.captured
    assert off.skills is None and off.tools == []
    assert on.skills == ["accounting-vietnam"]


def test_unused_registry_is_harmless() -> None:
    assert _resolve_skills(extraction_manifest(_manifest(skills=[]), "x", lean=True), SkillRegistry()).block == ""


# ------------------------------------------------------------------ Phase 3

class _ScriptedLlm:
    """LlmService fake: replies from a queue, records each request."""

    provider = "fake"
    model = "fake"

    def __init__(self, replies: list[str]) -> None:
        self.replies = list(replies)
        self.requests: list[list[Any]] = []

    async def chat(self, messages, *, temperature: float = 0.2, max_tokens: int | None = None):
        from aiq_agent.llm.base import ChatResult, LlmUsage

        self.requests.append(list(messages))
        return ChatResult(self.replies.pop(0), "fake", "fake", LlmUsage(1, 10, 100, 1000, 1, 0.01))

    async def aclose(self) -> None:
        return None


@pytest.mark.asyncio
async def test_claude_cli_service_disables_builtin_tools(monkeypatch) -> None:
    from aiq_agent.llm import claude_cli
    from aiq_agent.llm.base import ChatMessage

    _OptionsClient.captured = []
    monkeypatch.setattr(claude_cli, "ClaudeSDKClient", _OptionsClient)
    await claude_cli.ClaudeCliLlmService(model="m").chat([ChatMessage("system", "s"), ChatMessage("user", "u")])

    [options] = _OptionsClient.captured
    assert options.tools == []          # → `--tools ""`: no Claude Code built-ins
    assert options.skills is None and options.setting_sources is None


def test_correction_batches_respect_the_character_budget() -> None:
    from aiq_agent.ocr.pipeline import _batches

    assert _batches(["aaaa", "bb", "cccccc", "d"], 6) == [["aaaa", "bb"], ["cccccc"], ["d"]]
    assert _batches(["x" * 50, "y"], 10) == [["x" * 50], ["y"]]
    assert _batches([], 10) == []


@pytest.mark.asyncio
async def test_batched_correction_splits_pages_by_marker_and_skips_empty_pages() -> None:
    from aiq_agent.ocr.corrector import DiacriticCorrector

    llm = _ScriptedLlm(["<<<PAGE 1>>>\nCông ty A\n<<<PAGE 3>>>\nTổng tiền"])
    texts, usage = await DiacriticCorrector(llm).correct_many_with_usage(["Cong ty A", "   ", "Tong tien"])

    assert texts == ["Công ty A", "   ", "Tổng tiền"]
    assert len(llm.requests) == 1 and usage.llm_call_count == 1
    assert "<<<PAGE 2>>>" not in llm.requests[0][1].content


@pytest.mark.asyncio
@pytest.mark.parametrize("bad_reply", [
    "Công ty A\nTổng tiền",                                  # markers dropped
    "<<<PAGE 3>>>\nTổng tiền\n<<<PAGE 1>>>\nCông ty A",       # reordered
    "Đây là bản sửa:\n<<<PAGE 1>>>\nA\n<<<PAGE 3>>>\nB",     # preamble
    "<<<PAGE 1>>>\n\n<<<PAGE 3>>>\nTổng tiền",               # a page emptied
])
async def test_malformed_batch_reply_falls_back_to_one_call_per_page(bad_reply: str) -> None:
    from aiq_agent.ocr.corrector import DiacriticCorrector

    llm = _ScriptedLlm([bad_reply, "Công ty A", "Tổng tiền"])
    texts, usage = await DiacriticCorrector(llm).correct_many_with_usage(["Cong ty A", "", "Tong tien"])

    assert texts == ["Công ty A", "", "Tổng tiền"]
    assert len(llm.requests) == 3 and usage.llm_call_count == 3  # the failed batch call is still counted


@pytest.mark.asyncio
async def test_pipeline_batch_mode_off_by_default_and_identical_output_when_on() -> None:
    from aiq_agent.ocr import OcrPipeline, PassthroughValidator, StubOcrExtractor
    from aiq_agent.ocr.corrector import DiacriticCorrector

    pages = ["trang mot", "trang hai"]
    per_page = _ScriptedLlm(["trang một", "trang hai"])
    default = OcrPipeline(extractor=StubOcrExtractor(pages), corrector=DiacriticCorrector(per_page),
                          validator=PassthroughValidator())
    batched_llm = _ScriptedLlm(["<<<PAGE 1>>>\ntrang một\n<<<PAGE 2>>>\ntrang hai"])
    batched = OcrPipeline(extractor=StubOcrExtractor(pages), corrector=DiacriticCorrector(batched_llm),
                          validator=PassthroughValidator(), correction_batch_chars=8000)

    a = await default.run_pages([b"1", b"2"])
    b = await batched.run_pages([b"1", b"2"])

    assert a.corrected_text_by_page == b.corrected_text_by_page == ["trang một", "trang hai"]
    assert (len(per_page.requests), len(batched_llm.requests)) == (2, 1)
    assert Settings(embedding_enabled=False).ocr_correction_batch_chars == 0


# ------------------------------------------------------------------ Phase 4

class _PromptCapturingClient(_OptionsClient):
    """Also captures the system prompt file content and the query."""

    systems: list[str] = []
    queries: list[str] = []

    def __init__(self, **kwargs: Any) -> None:
        super().__init__(**kwargs)
        options = kwargs.get("options")
        with open(options.system_prompt["path"], encoding="utf-8") as fh:
            self.systems.append(fh.read())

    async def query(self, text: str, *_a: Any, **_k: Any) -> None:
        self.queries.append(text)


async def _run_chat(monkeypatch, prompt: str, *, dynamic: bool, manifest: ExecutionManifest | None = None):
    from aiq_agent.runtime import executors as executors_module

    monkeypatch.setattr(executors_module, "ClaudeSDKClient", _PromptCapturingClient)
    settings = Settings(embedding_enabled=False, dynamic_skill_loading=dynamic)
    executor = build_agent_executor(manifest or _manifest(), settings, build_registry(SKILLS_ROOT))
    return await executor.execute(prompt=prompt, token="t")


def _reset_capture() -> None:
    _PromptCapturingClient.captured = []
    _PromptCapturingClient.systems = []
    _PromptCapturingClient.queries = []


def test_skill_plan_inlines_rules_preselects_by_keyword_and_lists_the_rest() -> None:
    from aiq_agent.runtime.executors import _plan_skills

    m = _manifest(skills=[*LIVE_SKILLS, "kt-quy-tac"], skillSnapshots=[_snapshot("kt-quy-tac", "QUY TẮC")])
    plan = _plan_skills(m, build_registry(SKILLS_ROOT), "Tính thuế GTGT phải nộp tháng này")

    assert set(plan.inline.names) == {"vietnamese-style", "kt-quy-tac"}   # always_on + declarative
    assert "tax-calculation" in plan.preselected_names
    assert "contract-extractor" not in plan.preselected_names
    assert "contract-extractor" in plan.loadable and "contract-extractor" in plan.catalog
    assert "tax-calculation" in plan.catalog and "tax-calculation" not in plan.loadable
    # Nothing appears twice and nothing outside the version's skills appears at all.
    every = set(plan.inline.names) | set(plan.preselected_names) | set(plan.loadable)
    assert every == set(m.skills)
    assert not set(plan.preselected_names) & set(plan.loadable)


@pytest.mark.asyncio
async def test_load_skill_returns_only_listed_skills() -> None:
    from aiq_agent.runtime.executors import _build_load_skill_tool, _plan_skills

    plan = _plan_skills(_manifest(), build_registry(SKILLS_ROOT), "xin chào")
    sink: list[int] = []
    load = _build_load_skill_tool(plan, sink)

    ok = await load.handler({"name": "contract-extractor"})
    assert "===== SKILL: contract-extractor =====" in ok["content"][0]["text"] and sink and sink[0] > 0
    refused = await load.handler({"name": "voucher-universal-extractor"})   # not assigned to this agent
    assert "Unknown" in refused["content"][0]["text"]
    refused = await load.handler({"name": "vietnamese-style"})               # already inline
    assert "Unknown" in refused["content"][0]["text"]


@pytest.mark.asyncio
async def test_dynamic_mode_keeps_system_prompt_stable_and_smaller(monkeypatch) -> None:
    _reset_capture()
    await _run_chat(monkeypatch, "Tính thuế GTGT tháng 9", dynamic=True)
    await _run_chat(monkeypatch, "Tạo checklist theo dõi hợp đồng", dynamic=True)
    await _run_chat(monkeypatch, "Tính thuế GTGT tháng 9", dynamic=False)

    dyn_a, dyn_b, static = _PromptCapturingClient.systems
    assert dyn_a == dyn_b                                # request-independent → cacheable prefix
    assert len(dyn_a) < 0.35 * len(static)
    q_tax, q_contract, q_static = _PromptCapturingClient.queries
    assert "===== SKILL: tax-calculation =====" in q_tax
    assert "===== SKILL: contract-checklist-builder =====" in q_contract
    assert "===== SKILL:" not in q_static                 # static mode unchanged: skills only in system
    opts_dyn, _, opts_static = _PromptCapturingClient.captured
    assert "mcp__aiq__load_skill" in opts_dyn.allowed_tools and opts_dyn.skills is None
    assert "mcp__aiq__load_skill" not in opts_static.allowed_tools
    assert opts_static.skills == LIVE_SKILLS


@pytest.mark.asyncio
async def test_dynamic_mode_never_adds_tools_beyond_load_skill(monkeypatch) -> None:
    _reset_capture()
    await _run_chat(monkeypatch, "lưu chứng từ vào hệ thống", dynamic=True, manifest=_manifest(tools=[]))
    [opts] = _PromptCapturingClient.captured
    assert opts.allowed_tools == ["mcp__aiq__load_skill"] and opts.tools == []


@pytest.mark.asyncio
async def test_dynamic_mode_without_dynamic_skills_adds_no_tool(monkeypatch) -> None:
    _reset_capture()
    m = _manifest(skills=["vietnamese-style"], tools=[])
    await _run_chat(monkeypatch, "xin chào", dynamic=True, manifest=m)
    [opts] = _PromptCapturingClient.captured
    assert opts.allowed_tools == []


def test_dynamic_skill_loading_is_off_by_default() -> None:
    assert Settings(embedding_enabled=False).dynamic_skill_loading is False


# ------------------------------------------------------------------ Phase 5

@pytest.mark.asyncio
async def test_no_agent_run_is_refused_unless_legacy_is_enabled(monkeypatch) -> None:
    from fastapi import HTTPException

    from aiq_agent.agentic.runner import AgenticRunResult
    from aiq_agent.api import routes
    from aiq_agent.api.schemas import AgentRunRequest
    from aiq_agent.api.security import RuntimePrincipal

    calls: list[str] = []

    async def fake_run_task(**kwargs: Any) -> AgenticRunResult:
        calls.append(kwargs["task"])
        return AgenticRunResult(final_answer="ok")

    async def no_report(**_kwargs: Any) -> None:
        return None

    monkeypatch.setattr(routes, "run_task", fake_run_task)
    monkeypatch.setattr(routes, "report_unscoped_usage", no_report)
    admin = RuntimePrincipal("t", "admin-user", frozenset({"admin"}), "tenant", "dept")
    body = AgentRunRequest(task="liệt kê công nợ")

    monkeypatch.setattr(routes, "get_settings", lambda: Settings(embedding_enabled=False))
    with pytest.raises(HTTPException) as refused:
        await routes.agentic_run(body, admin, None, SkillRegistry(), None)
    assert refused.value.status_code == 403 and calls == []

    monkeypatch.setattr(routes, "get_settings",
                        lambda: Settings(embedding_enabled=False, legacy_agent_run_enabled=True))
    response = await routes.agentic_run(body, admin, None, SkillRegistry(), None)
    assert response.final_answer == "ok" and calls == ["liệt kê công nợ"]


# ------------------------------------------------------------------ Phase 6

def test_history_window_keeps_newest_whole_messages_within_both_budgets() -> None:
    from aiq_agent.llm.base import ChatMessage
    from aiq_agent.memory.base import trim_history

    history = [ChatMessage("user" if i % 2 == 0 else "assistant", f"m{i}-" + "x" * 96) for i in range(40)]
    by_count = trim_history(history, max_messages=6, max_chars=10_000)
    assert by_count == history[-6:]
    by_chars = trim_history(history, max_messages=40, max_chars=450)   # 4 whole messages fit
    assert by_chars == history[-4:]
    assert all(len(m.content) == 100 for m in by_chars)                 # never cut mid-message
    odd = trim_history(history, max_messages=5, max_chars=10_000)       # would start with an answer
    assert odd[0].role == "user" and odd == history[-4:]
    assert trim_history(history, max_messages=0, max_chars=10_000) == []


def test_route_history_window_applies_the_configured_caps(monkeypatch) -> None:
    from aiq_agent.api import routes
    from aiq_agent.llm.base import ChatMessage

    history = [ChatMessage("user" if i % 2 == 0 else "assistant", "y" * 1000) for i in range(100)]
    monkeypatch.setattr(routes, "get_settings", lambda: Settings(embedding_enabled=False))
    window = routes._history_window(history)
    assert len(window) == 20 and sum(len(m.content) for m in window) <= 24_000
    assert len(routes._history_window(history, max_messages=6)) == 6


class _ToolRoundClient(_OptionsClient):
    async def receive_response(self):
        from claude_agent_sdk import AssistantMessage, TextBlock, ToolUseBlock

        yield AssistantMessage(content=[TextBlock(text="Tôi sẽ tra cứu công nợ khách hàng A.")], model="m")
        yield AssistantMessage(content=[ToolUseBlock(id="t1", name="mcp__aiq__get_customer_receivables",
                                                     input={"q": "A"})], model="m")
        yield AssistantMessage(content=[TextBlock(text="Công nợ của khách hàng A là 12.000.000 đ.")], model="m")


@pytest.mark.asyncio
async def test_history_keeps_the_final_answer_not_tool_round_narration(monkeypatch) -> None:
    from aiq_agent.runtime import executors as executors_module

    monkeypatch.setattr(executors_module, "ClaudeSDKClient", _ToolRoundClient)
    result = await build_agent_executor(_manifest(skills=[]), Settings(embedding_enabled=False),
                                        SkillRegistry()).execute(prompt="công nợ A?", token="t")

    assert "Tôi sẽ tra cứu" in result.final_answer          # the user still sees everything
    assert result.history_text == "Công nợ của khách hàng A là 12.000.000 đ."


@pytest.mark.asyncio
@pytest.mark.parametrize(("configured", "expected"), [(8, 8), (12, 12), (20, 12)])
async def test_turn_cap_bounds_configured_max_turns(monkeypatch, configured: int, expected: int) -> None:
    from aiq_agent.runtime import executors as executors_module

    _OptionsClient.captured = []
    monkeypatch.setattr(executors_module, "ClaudeSDKClient", _OptionsClient)
    await build_agent_executor(_manifest(skills=[], maxTurns=configured), Settings(embedding_enabled=False),
                               SkillRegistry()).execute(prompt="x", token="t")
    assert _OptionsClient.captured[0].max_turns == expected
