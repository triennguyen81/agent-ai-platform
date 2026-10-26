"""Token audit Phase 1: real per-request usage, cache tokens, call counts."""

from __future__ import annotations

from typing import Any

import pytest
from claude_agent_sdk import AssistantMessage, ResultMessage, TextBlock

from aiq_agent.config import Settings
from aiq_agent.llm.base import LlmUsage
from aiq_agent.observability.llm_usage import UsageCollector, merge_usage
from aiq_agent.runtime.executors import build_agent_executor
from aiq_agent.runtime.manifest import ExecutionManifest
from aiq_agent.skills import Skill, SkillRegistry

from .test_dynamic_runtime import manifest_data

_SECRET = "SỐ HOÁ ĐƠN 0000999 — NỘI DUNG CHỨNG TỪ"


def _result(**usage: Any) -> ResultMessage:
    return ResultMessage(
        subtype="success", duration_ms=10, duration_api_ms=8, is_error=False, num_turns=2,
        session_id="s", total_cost_usd=0.0421,
        usage={"input_tokens": 12, "output_tokens": 300, "cache_creation_input_tokens": 9000,
               "cache_read_input_tokens": 21000, **usage},
    )


def _assistant(text: str, message_id: str) -> AssistantMessage:
    return AssistantMessage(
        content=[TextBlock(text=text)],
        model="claude-sonnet-x",
        message_id=message_id,
        usage={"input_tokens": 1},
    )


class _ScriptedClient:
    """Two API calls: call A streamed as two blocks (same message id), call B once."""

    messages: list[Any] = []

    def __init__(self, **_kwargs: Any) -> None:
        pass

    async def __aenter__(self) -> _ScriptedClient:
        return self

    async def __aexit__(self, *exc: Any) -> bool:
        return False

    async def query(self, *_args: Any, **_kwargs: Any) -> None:
        return None

    async def receive_response(self):
        for message in self.messages:
            yield message


def test_collector_counts_distinct_api_calls_and_keeps_token_classes_separate() -> None:
    collector = UsageCollector()
    collector.add_assistant(_assistant("a1", "msg_A"))
    collector.add_assistant(_assistant("a2", "msg_A"))
    collector.add_assistant(_assistant("b", "msg_B"))
    collector.add_result(_result())

    assert collector.llm_call_count == 2
    usage = collector.to_llm_usage()
    assert usage is not None
    assert (
        usage.input_tokens,
        usage.cache_creation_input_tokens,
        usage.cache_read_input_tokens,
    ) == (12, 9000, 21000)
    # Disjoint classes: the full prompt is their plain sum, nothing counted twice.
    assert usage.prompt_tokens == 12 + 9000 + 21000
    assert usage.cost_usd == pytest.approx(0.0421)
    assert collector.num_turns == 2


def test_collector_without_result_reports_unknown_not_zero() -> None:
    collector = UsageCollector()
    collector.add_assistant(_assistant("x", "msg_A"))
    assert collector.to_llm_usage() is None
    assert collector.input_tokens is None


def test_merge_usage_sums_calls_and_keeps_unknown_cost_unknown() -> None:
    merged = merge_usage(
        [
            LlmUsage(1, 2, 3, 4, 1, 0.5),
            None,
            LlmUsage(10, 20, 30, 40, 2, None),
        ]
    )
    assert merged == LlmUsage(11, 22, 33, 44, 3, 0.5)
    assert merge_usage([None, None]) is None
    assert merge_usage([LlmUsage(1, 1)]).cost_usd is None


@pytest.mark.asyncio
async def test_claude_executor_records_real_usage_and_size_only_metrics(monkeypatch) -> None:
    from aiq_agent.runtime import executors as executors_module

    _ScriptedClient.messages = [
        _assistant("bước 1", "msg_A"),
        _assistant("trả lời", "msg_B"),
        _result(),
    ]
    monkeypatch.setattr(executors_module, "ClaudeSDKClient", _ScriptedClient)
    registry = SkillRegistry((Skill("accounting-vietnam", "", "HƯỚNG DẪN " * 50),))
    manifest = ExecutionManifest.model_validate(
        manifest_data(provider="claude_sdk", skills=["accounting-vietnam"])
    )

    result = await build_agent_executor(
        manifest, Settings(embedding_enabled=False), registry
    ).execute(prompt=_SECRET, token="t")

    assert result.llm_call_count == 2
    assert result.input_tokens == 12
    assert result.cache_creation_input_tokens == 9000
    assert result.cache_read_input_tokens == 21000
    assert result.output_tokens == 300
    assert result.estimated_cost_usd == pytest.approx(0.0421)
    assert result.cost_source == "claude_cli_total_cost_usd"
    assert result.num_turns == 2
    metrics = result.prompt_metrics
    assert metrics["user_prompt_chars"] == len(_SECRET)
    assert metrics["skills_inline_count"] == 1 and metrics["skills_inline_chars"] > 0
    assert all(isinstance(v, int) for v in metrics.values())
    assert _SECRET not in repr(metrics)


@pytest.mark.asyncio
async def test_claude_cli_service_returns_real_usage(monkeypatch) -> None:
    from aiq_agent.llm import claude_cli
    from aiq_agent.llm.base import ChatMessage

    _ScriptedClient.messages = [_assistant("đã sửa", "msg_A"), _result()]
    monkeypatch.setattr(claude_cli, "ClaudeSDKClient", _ScriptedClient)

    result = await claude_cli.ClaudeCliLlmService(model="m").chat([ChatMessage("user", "da sua")])

    assert result.content == "đã sửa"
    assert result.usage == LlmUsage(12, 300, 9000, 21000, 1, pytest.approx(0.0421))


@pytest.mark.asyncio
async def test_ocr_pipeline_sums_correction_usage_over_pages() -> None:
    from aiq_agent.ocr import OcrPipeline, PassthroughValidator, StubOcrExtractor
    from aiq_agent.ocr.corrector import TextCorrector

    class MeteredCorrector(TextCorrector):
        async def correct(self, text: str) -> str:
            return text

        async def correct_with_usage(self, text: str) -> tuple[str, LlmUsage | None]:
            return text, (LlmUsage(1, 10, 100, 1000, 1, 0.01) if text.strip() else None)

    pipeline = OcrPipeline(
        extractor=StubOcrExtractor(["trang 1", "  ", "trang 3"]),
        corrector=MeteredCorrector(),
        validator=PassthroughValidator(),
    )
    result = await pipeline.run_pages([b"1", b"2", b"3"])

    assert result.llm_usage == LlmUsage(2, 20, 200, 2000, 2, pytest.approx(0.02))


class _CapturingHttp:
    posts: list[tuple[str, dict[str, Any]]] = []

    def __init__(self, **_kwargs: Any) -> None:
        pass

    async def __aenter__(self) -> _CapturingHttp:
        return self

    async def __aexit__(self, *exc: Any) -> bool:
        return False

    async def post(self, url: str, *, headers: dict[str, str], json: dict[str, Any]):
        self.posts.append((url, json))

        class _Res:
            status_code = 204
            text = ""

        return _Res()


@pytest.mark.asyncio
async def test_report_usage_sends_cache_tokens_calls_and_numeric_metrics(monkeypatch) -> None:
    from aiq_agent.observability import usage_reporter
    from aiq_agent.runtime.executors import ExecutionResult

    _CapturingHttp.posts = []
    monkeypatch.setattr(usage_reporter.httpx, "AsyncClient", _CapturingHttp)
    data = manifest_data(provider="claude_sdk")
    data["toolExecutionToken"] = "tool-token"
    manifest = ExecutionManifest.model_validate(data)
    result = ExecutionResult(
        "trả lời",
        "claude_sdk",
        "m",
        input_tokens=12,
        output_tokens=300,
        estimated_cost_usd=0.04,
        cache_creation_input_tokens=9000,
        cache_read_input_tokens=21000,
        llm_call_count=2,
        num_turns=2,
        cost_source="claude_cli_total_cost_usd",
        prompt_metrics={"user_prompt_chars": 40},
    )

    await usage_reporter.report_usage(
        manifest=manifest,
        result=result,
        event_type="chat",
        total_latency_ms=100,
        gateway_base="http://gw",
        request_id=None,
    )

    [(url, payload)] = _CapturingHttp.posts
    assert url.endswith(f"/runtime/agents/{manifest.agent.id}/usage")
    assert payload["llmCallCount"] == 2
    assert payload["cacheCreationInputTokens"] == 9000 and payload["cacheReadInputTokens"] == 21000
    assert payload["metrics"] == {"user_prompt_chars": 40}
    assert "trả lời" not in repr(payload)


@pytest.mark.asyncio
async def test_unscoped_usage_goes_to_agentless_route_and_skips_unknown(monkeypatch) -> None:
    from aiq_agent.observability import usage_reporter

    _CapturingHttp.posts = []
    monkeypatch.setattr(usage_reporter.httpx, "AsyncClient", _CapturingHttp)

    await usage_reporter.report_unscoped_usage(
        token="t",
        event_type="plain_chat",
        usage=None,
        provider="p",
        model="m",
        total_latency_ms=1,
        gateway_base="http://gw",
    )
    assert _CapturingHttp.posts == []

    await usage_reporter.report_unscoped_usage(
        token="t",
        event_type="plain_chat",
        usage=LlmUsage(1, 2, 3, 4, 1, 0.1),
        provider="claude_cli",
        model="m",
        total_latency_ms=1,
        gateway_base="http://gw",
    )
    [(url, payload)] = _CapturingHttp.posts
    assert url == "http://gw/runtime/usage"
    assert payload["eventType"] == "plain_chat"
    assert (
        payload["inputTokens"],
        payload["cacheCreationInputTokens"],
        payload["cacheReadInputTokens"],
    ) == (1, 3, 4)
