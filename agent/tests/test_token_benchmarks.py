"""Benchmark scripts stay runnable and honest (offline: no LLM, no network)."""

from __future__ import annotations

import json
import sys
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest
from claude_agent_sdk import AssistantMessage, ResultMessage, TextBlock

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import token_benchmark  # noqa: E402
import token_benchmark_live  # noqa: E402


def test_offline_benchmark_reports_smaller_prompts_for_every_scenario(tmp_path, monkeypatch) -> None:
    out = tmp_path / "bench.json"
    monkeypatch.setattr(sys, "argv", ["token_benchmark.py", "--json", str(out)])
    assert token_benchmark.main() == 0
    data = json.loads(out.read_text())
    assert len(data["rows"]) == 7
    for row in data["rows"]:
        assert row["optimized"]["skills_sent"] < row["baseline"]["skills_sent"]
        assert 0 < row["char_reduction"] < 1
        assert row["est_tokens_low"] <= row["est_tokens_high"] < row["measured_baseline_tokens"]
    assert data["ocr_calls_5_pages"] == {"per_page": 5, "batch_8000": 1}


def test_accuracy_scoring_normalises_amounts_and_dates() -> None:
    golden = {"document_number": "0001234", "total_amount": 3575000, "document_date": "2026-09-20"}
    assert token_benchmark_live._accuracy(
        {"document_number": "0001234", "total_amount": "3.575.000", "document_date": "2026-09-20"}, golden) == 1.0
    assert token_benchmark_live._accuracy({"document_number": "1234", "total_amount": 3575000.0}, golden) == pytest.approx(1 / 3)
    assert token_benchmark_live._first_json("```json\n{\"a\": 1}\n```\n```json\n[]\n```") == {"a": 1}


class _FakeCli:
    """Answers every session with the golden JSON of the document in the prompt."""

    def __init__(self, **_kwargs: Any) -> None:
        self.prompt = ""

    async def __aenter__(self) -> "_FakeCli":
        return self

    async def __aexit__(self, *exc: Any) -> bool:
        return False

    async def query(self, text: str, *_a: Any, **_k: Any) -> None:
        self.prompt = text

    async def receive_response(self):
        golden = next((d["golden"] for d in token_benchmark_live.DOCUMENTS if d["text"][:40] in self.prompt), {})
        yield AssistantMessage(content=[TextBlock(text=f"```json\n{json.dumps(golden)}\n```\n```json\n[]\n```")],
                               model="m", message_id="msg")
        yield ResultMessage(subtype="success", duration_ms=1, duration_api_ms=1, is_error=False, num_turns=1,
                            session_id="s", total_cost_usd=0.001,
                            usage={"input_tokens": 1, "output_tokens": 2, "cache_creation_input_tokens": 3,
                                   "cache_read_input_tokens": 4})


@pytest.mark.asyncio
async def test_live_benchmark_logic_against_a_fake_cli(monkeypatch) -> None:
    from aiq_agent.llm import claude_cli
    from aiq_agent.runtime import executors

    monkeypatch.setattr(executors, "ClaudeSDKClient", _FakeCli)
    monkeypatch.setattr(claude_cli, "ClaudeSDKClient", _FakeCli)
    result = await token_benchmark_live.run(SimpleNamespace(max_usd=1.0, ocr_model="m"))

    assert {r["accuracy"] for r in result["extraction"]} == {1.0}
    assert all(r["cache_read"] == 4 and r["llm_calls"] == 1 for r in result["extraction"])
    assert len(result["chat"]) == 10 and len(result["ocr"]) == 2
    assert result["total_cost_usd"] > 0


def test_live_benchmark_budget_stops_the_run() -> None:
    budget = token_benchmark_live.Budget(0.002)
    budget.add(0.001)
    with pytest.raises(SystemExit):
        budget.add(0.002)
