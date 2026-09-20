"""Unit tests for the mapping layer in aiq_agent.agentic.contract_tools that
sits between raw LLM-extracted fields and Gateway's ProcessContractRequest
shape (_normalize_payment_terms / _normalize_milestones / _normalize_clauses).
contract_normalize itself (repair_payment_term/repair_milestone/
normalize_payment_percentages/split_clauses_from_raw_text) already has its
own dedicated unit tests in test_contract_normalize.py — this file covers
the mapping/ordinal/fallback behavior specific to these functions,
including two real bugs found in production:
  - a milestone with no `name` was silently dropped instead of being
    normalized;
  - EVERY clause's heading/body was empty because the model's own
    (summarized, or entirely missing) text was trusted directly instead of
    the actual contract raw_text.
"""

from __future__ import annotations

from aiq_agent.agentic.contract_tools import (
    _normalize_clauses,
    _normalize_milestones,
    _normalize_payment_terms,
)


# ---------------------------------------------------------------------
# _normalize_payment_terms — real-contract regression: 30/40/30 of 330M
# ---------------------------------------------------------------------

def test_30_40_30_payment_split_maps_through_unchanged_and_ordered():
    # This is the exact split from contract b314adbe-d9fb-4789-a70d-
    # 635dbc736194 (pre-VAT 300M, VAT 30M, total 330M) — Gateway itself does
    # the percentage x subtotal arithmetic (ProcessAsync), so this only
    # needs to prove the percentages/ordinals/descriptions survive mapping
    # unchanged and in order.
    raw = [
        {"percentage": 30, "description": "Đợt 1 — tạm ứng"},
        {"percentage": 40, "description": "Đợt 2 — giao hàng"},
        {"percentage": 30, "description": "Đợt 3 — nghiệm thu"},
    ]
    out = _normalize_payment_terms(raw)
    assert [p["percentage"] for p in out] == [30.0, 40.0, 30.0]
    assert [p["ordinal"] for p in out] == [1, 2, 3]
    assert sum(p["percentage"] for p in out) == 100.0


def test_30_40_30_payment_split_as_fractions_is_scaled_to_percent():
    raw = [{"percentage": 0.3}, {"percentage": 0.4}, {"percentage": 0.3}]
    out = _normalize_payment_terms(raw)
    assert [p["percentage"] for p in out] == [30.0, 40.0, 30.0]


def test_30_40_30_payment_split_as_percent_strings_is_coerced():
    raw = [{"percentage": "30%"}, {"percentage": "40%"}, {"percentage": "30%"}]
    out = _normalize_payment_terms(raw)
    assert [p["percentage"] for p in out] == [30.0, 40.0, 30.0]


def test_payment_term_missing_description_gets_generic_ordinal_fallback():
    out = _normalize_payment_terms([{"percentage": 100}])
    assert out[0]["description"] == "Đợt 1"


def test_malformed_but_recoverable_payment_term_trigger_and_due_date_both_present():
    # The exact invalid combination this task calls out: LLM extraction
    # sends BOTH a confirmed due_date and a trigger_event for one
    # installment. repair_payment_term must win — due_date dropped,
    # trigger_event kept — before this ever reaches /process.
    raw = [{
        "percentage": 100,
        "description": "Đợt 1",
        "due_date": "2026-03-01",
        "trigger_event": "Ký biên bản giao nhận",
        "offset_days": 10,
        "expected_date": "2026-04-01",
    }]
    out = _normalize_payment_terms(raw)
    assert out[0]["dueDate"] is None
    assert out[0]["triggerEvent"] == "Ký biên bản giao nhận"
    assert out[0]["offsetDays"] == 10
    assert out[0]["expectedDate"] == "2026-04-01"


def test_non_dict_payment_term_entries_are_dropped_not_crashed_on():
    out = _normalize_payment_terms(["not a dict", 42, None, {"percentage": 100}])
    assert len(out) == 1
    assert out[0]["percentage"] == 100.0


def test_non_list_payment_terms_returns_empty():
    assert _normalize_payment_terms(None) == []
    assert _normalize_payment_terms("not a list") == []


# ---------------------------------------------------------------------
# _normalize_milestones — the actual production bug: missing name silently
# dropped a milestone instead of normalizing it, losing real contract data
# (M2-M5 in the reported contract).
# ---------------------------------------------------------------------

def test_milestone_missing_name_gets_generic_ordinal_fallback_not_dropped():
    raw = [
        {"name": "Khảo sát yêu cầu"},
        {"due_date": "2026-12-31"},  # no name at all — the actual bug case
        {"name": ""},  # blank name — same bug case, different shape
        {"name": "   "},  # whitespace-only name — same bug case
    ]
    out = _normalize_milestones(raw)

    # All four must survive — none silently dropped.
    assert len(out) == 4
    assert out[0]["name"] == "Khảo sát yêu cầu"
    assert out[1]["name"] == "Mốc 2"
    assert out[1]["dueDate"] == "2026-12-31"
    assert out[2]["name"] == "Mốc 3"
    assert out[3]["name"] == "Mốc 4"


def test_milestone_fallback_name_uses_explicit_ordinal_when_present():
    out = _normalize_milestones([{"ordinal": 7}])
    assert out[0]["ordinal"] == 7
    assert out[0]["name"] == "Mốc 7"


def test_five_milestones_all_preserved_in_order_matching_real_contract_shape():
    # Mirrors the reported contract: 5 milestones, some named, some not —
    # all five must reach the canonical shape, never fewer.
    raw = [
        {"name": "Khảo sát yêu cầu"},
        {},  # M2 — no name in the source extraction
        {},  # M3
        {"due_date": "2026-12-31"},  # M4 — has a date but no name
        {"name": "Kết thúc hỗ trợ", "trigger_event": "Hết hạn hỗ trợ", "duration_months": 6},
    ]
    out = _normalize_milestones(raw)
    assert len(out) == 5
    assert [m["ordinal"] for m in out] == [1, 2, 3, 4, 5]
    assert out[4]["name"] == "Kết thúc hỗ trợ"
    assert out[4]["triggerEvent"] == "Hết hạn hỗ trợ"
    assert out[4]["durationMonths"] == 6


def test_non_dict_milestone_entries_are_dropped_not_crashed_on():
    out = _normalize_milestones(["not a dict", 42, None, {"name": "Bàn giao"}])
    assert len(out) == 1
    assert out[0]["name"] == "Bàn giao"


def test_non_list_milestones_returns_empty():
    assert _normalize_milestones(None) == []
    assert _normalize_milestones("not a list") == []


def test_malformed_milestone_trigger_and_due_date_both_present_is_repaired():
    out = _normalize_milestones([{
        "name": "Bảo hành",
        "due_date": "2026-01-01",
        "trigger_event": "Ký biên bản giao nhận",
    }])
    assert out[0]["dueDate"] is None
    assert out[0]["triggerEvent"] == "Ký biên bản giao nhận"


# ---------------------------------------------------------------------
# _normalize_clauses — the actual production bug: contract_clauses.body
# was empty in the DB for every one of a real contract's 8 clauses
# (370f0098-a77a-410a-9318-ddb850bb86f2), despite raw_text holding the
# complete original wording. Body/heading must always come from the real
# document (split_clauses_from_raw_text) when it uses the "Điều N"
# convention; the model's own clauses[] should only ever contribute
# per-ordinal classification flags, never override real text with a
# summary or with nothing at all.
# ---------------------------------------------------------------------

_REAL_CONTRACT_RAW_TEXT = """HỢP ĐỒNG CUNG CẤP DỊCH VỤ
Số: 016/2026/HĐDV-AI
ĐIỀU 1. PHẠM VI DỊCH VỤ
Bên B cung cấp cho Bên A dịch vụ tư vấn, cấu hình và triển khai hệ thống trợ lý AI hỗ trợ xử lý chứng
từ, hợp đồng, công nợ và báo cáo quản trị.
ĐIỀU 2. GIÁ TRỊ HỢP ĐỒNG VÀ THUẾ
Nội dung Giá trị (VND)
Giá trị trước VAT 300.000.000
ĐIỀU 4. PHẠT VI PHẠM
Trường hợp thanh toán chậm, mức phạt chậm thanh toán theo Hợp đồng là 0,03%/ngày."""


def test_clauses_use_real_document_text_even_when_model_returns_nothing():
    # Exact reproduction of the production bug: the model's clauses[] has
    # ordinal only — no heading, no body — for every clause.
    llm_clauses = [{"ordinal": 1}, {"ordinal": 2}, {"ordinal": 4}]
    out = _normalize_clauses(llm_clauses, _REAL_CONTRACT_RAW_TEXT)

    by_ordinal = {c["ordinal"]: c for c in out}
    assert by_ordinal[1]["body"].startswith(
        "Bên B cung cấp cho Bên A dịch vụ tư vấn, cấu hình và triển khai"
    )
    assert by_ordinal[1]["heading"] == "Điều 1 — PHẠM VI DỊCH VỤ"
    assert "300.000.000" in by_ordinal[2]["body"]
    assert "0,03%/ngày" in by_ordinal[4]["body"]


def test_clauses_use_real_document_text_even_when_model_omits_clauses_entirely():
    # fields.get("clauses") can legitimately be missing/None/[] — body must
    # still come from raw_text, not an empty result.
    out = _normalize_clauses(None, _REAL_CONTRACT_RAW_TEXT)
    assert len(out) == 3
    assert out[0]["body"].startswith("Bên B cung cấp cho Bên A")


def test_clauses_merge_model_classification_flags_by_ordinal():
    llm_clauses = [
        {"ordinal": 1, "has_deadline": False, "has_penalty": False},
        {"ordinal": 4, "has_deadline": True, "has_penalty": True, "clause_type": "penalty"},
    ]
    out = _normalize_clauses(llm_clauses, _REAL_CONTRACT_RAW_TEXT)
    by_ordinal = {c["ordinal"]: c for c in out}

    # Real text still wins for body/heading...
    assert "0,03%/ngày" in by_ordinal[4]["body"]
    # ...but the model's classification for that same ordinal is preserved.
    assert by_ordinal[4]["hasPenalty"] is True
    assert by_ordinal[4]["clauseType"] == "penalty"
    assert by_ordinal[1]["hasPenalty"] is False


def test_clause_with_no_matching_model_ordinal_gets_default_classification():
    # Ordinal 2 exists in raw_text but the model never mentioned it —
    # clause must still be preserved with safe classification defaults,
    # never dropped.
    out = _normalize_clauses([{"ordinal": 1, "has_penalty": True}], _REAL_CONTRACT_RAW_TEXT)
    by_ordinal = {c["ordinal"]: c for c in out}
    assert 2 in by_ordinal
    assert by_ordinal[2]["hasPenalty"] is False
    assert by_ordinal[2]["hasDeadline"] is False
    assert by_ordinal[2]["clauseType"] is None


def test_clauses_fall_back_to_model_output_when_document_has_no_dieu_markers():
    # A contract template that doesn't use "Điều N" at all — never return
    # zero clauses just because the deterministic splitter found nothing.
    llm_clauses = [
        {"ordinal": 1, "heading": "Section 1", "body": "Some clause text.", "has_penalty": True},
    ]
    out = _normalize_clauses(llm_clauses, "A contract with no Vietnamese clause markers.")
    assert len(out) == 1
    assert out[0]["heading"] == "Section 1"
    assert out[0]["body"] == "Some clause text."
    assert out[0]["hasPenalty"] is True


def test_clauses_returns_empty_when_both_raw_text_and_model_output_are_empty():
    assert _normalize_clauses(None, "") == []
    assert _normalize_clauses([], "") == []
