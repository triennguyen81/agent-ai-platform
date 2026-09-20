"""Unit tests for aiq_agent.agentic.contract_normalize — the deterministic
normalization/validation layer that sits between the LLM's raw Contract
Processing extraction and Gateway's /process endpoint (see
docs/accagent-spec-v0.1-quy-trinh-nghiep-vu.md "Luồng 01" and
gateway/src/Aiq.Gateway/Contracts/IContractRepository.cs's ProcessAsync,
which remains the sole owner of every actual business rule). Every function
under test is pure stdlib — no httpx/claude_agent_sdk/network involved.
"""

from __future__ import annotations

from aiq_agent.agentic.contract_normalize import (
    classify_date_kind,
    coerce_number,
    coerce_positive_int,
    normalize_payment_percentages,
    normalize_vat_rate,
    parse_date,
    repair_milestone,
    repair_payment_term,
    split_clauses_from_raw_text,
    validate_extracted_payload,
)


# ---------------------------------------------------------------------
# coerce_number / coerce_positive_int
# ---------------------------------------------------------------------

def test_coerce_number_accepts_int_and_float():
    assert coerce_number(30) == 30.0
    assert coerce_number(30.5) == 30.5


def test_coerce_number_accepts_numeric_string_with_percent_and_commas():
    assert coerce_number("30") == 30.0
    assert coerce_number(" 30% ") == 30.0
    assert coerce_number("1,250") == 1250.0


def test_coerce_number_rejects_non_numeric_and_bool_and_none():
    assert coerce_number(None) is None
    assert coerce_number(True) is None
    assert coerce_number("khong phai so") is None
    assert coerce_number("") is None


def test_coerce_positive_int_rejects_zero_and_negative():
    assert coerce_positive_int(10) == 10
    assert coerce_positive_int(0) is None
    assert coerce_positive_int(-5) is None
    assert coerce_positive_int(None) is None
    assert coerce_positive_int("7") == 7


# ---------------------------------------------------------------------
# parse_date — absolute date normalization
# ---------------------------------------------------------------------

def test_parse_date_normalizes_valid_iso_date():
    assert parse_date("2026-09-25") == "2026-09-25"


def test_parse_date_rejects_malformed_or_impossible_dates():
    assert parse_date("25/09/2026") is None  # wrong format, never guessed
    assert parse_date("2026-13-40") is None  # not a real calendar date
    assert parse_date("") is None
    assert parse_date(None) is None
    assert parse_date(12345) is None


# ---------------------------------------------------------------------
# normalize_vat_rate — fraction vs percent
# ---------------------------------------------------------------------

def test_normalize_vat_rate_scales_a_fraction_to_percent():
    assert normalize_vat_rate(0.1) == 10.0
    assert normalize_vat_rate(0.08) == 8.0


def test_normalize_vat_rate_leaves_an_already_percent_value_untouched():
    assert normalize_vat_rate(10) == 10.0
    assert normalize_vat_rate(8) == 8.0


def test_normalize_vat_rate_rejects_negative_and_non_numeric():
    assert normalize_vat_rate(-1) is None
    assert normalize_vat_rate("not a number") is None
    assert normalize_vat_rate(None) is None


# ---------------------------------------------------------------------
# normalize_payment_percentages — sum-based fraction detection
# ---------------------------------------------------------------------

def test_normalize_payment_percentages_scales_fractions_summing_to_one():
    assert normalize_payment_percentages([0.3, 0.7]) == [30.0, 70.0]


def test_normalize_payment_percentages_leaves_already_percent_values_untouched():
    assert normalize_payment_percentages([30, 70]) == [30.0, 70.0]


def test_normalize_payment_percentages_does_not_guess_for_an_unclear_sum():
    # Sums to 60 — neither ~1.0 nor ~100; Gateway's own PAYMENT_PERCENT_INVALID
    # rule is the authority on rejecting this, not this normalizer.
    assert normalize_payment_percentages([20, 40]) == [20.0, 40.0]


def test_normalize_payment_percentages_handles_single_full_installment():
    assert normalize_payment_percentages([100]) == [100.0]
    assert normalize_payment_percentages([1.0]) == [100.0]


# ---------------------------------------------------------------------
# classify_date_kind
# ---------------------------------------------------------------------

def test_classify_date_kind():
    assert classify_date_kind(None, None) == "fixed_date"
    assert classify_date_kind("", None) == "fixed_date"
    assert classify_date_kind("Ký biên bản giao nhận", None) == "conditional_event"
    assert classify_date_kind("Ký biên bản giao nhận", 10) == "derived_after_event"


# ---------------------------------------------------------------------
# repair_payment_term — the core "reject/repair invalid combinations" rule
# ---------------------------------------------------------------------

def test_repair_payment_term_fixed_date_is_preserved():
    out = repair_payment_term({"due_date": "2026-03-01"})
    assert out["due_date"] == "2026-03-01"
    assert out["trigger_event"] is None
    assert out["offset_days"] is None
    assert out["expected_date"] is None


def test_repair_payment_term_trigger_event_forces_due_date_none_even_if_both_sent():
    # This is the exact invalid combination called out in the task: a
    # payment term with BOTH trigger_event and due_date present.
    out = repair_payment_term({
        "due_date": "2026-03-01",
        "trigger_event": "Ký biên bản giao nhận",
        "offset_days": 10,
        "expected_date": "2026-04-01",
    })
    assert out["due_date"] is None
    assert out["trigger_event"] == "Ký biên bản giao nhận"
    assert out["offset_days"] == 10
    assert out["expected_date"] == "2026-04-01"


def test_repair_payment_term_drops_offset_days_without_trigger_event():
    out = repair_payment_term({"due_date": None, "offset_days": 10})
    assert out["offset_days"] is None
    assert out["trigger_event"] is None


def test_repair_payment_term_drops_expected_date_without_trigger_event():
    out = repair_payment_term({"expected_date": "2026-04-01"})
    assert out["expected_date"] is None


def test_repair_payment_term_never_promotes_expected_date_into_due_date():
    out = repair_payment_term({
        "trigger_event": "Ký biên bản giao nhận",
        "expected_date": "2026-04-01",
    })
    assert out["due_date"] is None
    assert out["expected_date"] == "2026-04-01"


def test_repair_payment_term_rejects_malformed_dates():
    out = repair_payment_term({"due_date": "not-a-date"})
    assert out["due_date"] is None


def test_repair_payment_term_treats_blank_trigger_event_as_absent():
    out = repair_payment_term({"due_date": "2026-03-01", "trigger_event": "   "})
    assert out["trigger_event"] is None
    assert out["due_date"] == "2026-03-01"


# ---------------------------------------------------------------------
# repair_milestone — same invariant, duration_months instead of offset_days
# ---------------------------------------------------------------------

def test_repair_milestone_conditional_with_duration_is_preserved():
    out = repair_milestone({
        "trigger_event": "Ký biên bản giao nhận",
        "duration_months": 24,
        "expected_date": "2028-09-25",
    })
    assert out["due_date"] is None
    assert out["duration_months"] == 24
    assert out["expected_date"] == "2028-09-25"


def test_repair_milestone_drops_duration_months_without_trigger_event():
    out = repair_milestone({"duration_months": 24})
    assert out["duration_months"] is None


def test_repair_milestone_fully_open_ended_has_no_dates_at_all():
    # "Hết hiệu lực hợp đồng ... đến khi hoàn thành nghĩa vụ" — a genuinely
    # open-ended milestone: trigger_event present, nothing else resolvable.
    out = repair_milestone({"trigger_event": "Hoàn thành toàn bộ nghĩa vụ hợp đồng"})
    assert out["due_date"] is None
    assert out["duration_months"] is None
    assert out["expected_date"] is None
    assert out["trigger_event"] == "Hoàn thành toàn bộ nghĩa vụ hợp đồng"


def test_repair_milestone_fixed_date_is_preserved():
    out = repair_milestone({"due_date": "2026-09-25"})
    assert out["due_date"] == "2026-09-25"


# ---------------------------------------------------------------------
# validate_extracted_payload — structural schema check
# ---------------------------------------------------------------------

def test_validate_extracted_payload_flags_missing_title():
    issues = validate_extracted_payload({})
    assert any("title" in i for i in issues)


def test_validate_extracted_payload_flags_non_list_parties():
    issues = validate_extracted_payload({"title": "T", "parties": "not a list"})
    assert any("parties" in i for i in issues)


def test_validate_extracted_payload_flags_party_missing_name():
    issues = validate_extracted_payload({"title": "T", "parties": [{"tax_code": "0101"}]})
    assert any("parties[1]" in i for i in issues)


def test_validate_extracted_payload_flags_payment_term_missing_percentage():
    issues = validate_extracted_payload({"title": "T", "payment_terms": [{"description": "Đợt 1"}]})
    assert any("percentage" in i for i in issues)


def test_validate_extracted_payload_flags_trigger_event_with_due_date():
    issues = validate_extracted_payload({
        "title": "T",
        "payment_terms": [{"percentage": 100, "due_date": "2026-03-01", "trigger_event": "Ký biên bản"}],
    })
    assert any("trigger_event và due_date" in i for i in issues)


def test_validate_extracted_payload_flags_offset_days_without_trigger_event():
    issues = validate_extracted_payload({
        "title": "T",
        "payment_terms": [{"percentage": 100, "offset_days": 10}],
    })
    assert any("offset_days" in i for i in issues)


def test_validate_extracted_payload_flags_duration_months_without_trigger_event():
    issues = validate_extracted_payload({
        "title": "T",
        "milestones": [{"name": "Bảo hành", "duration_months": 24}],
    })
    assert any("duration_months" in i for i in issues)


def test_validate_extracted_payload_valid_minimal_payload_has_no_issues():
    issues = validate_extracted_payload({
        "title": "Hợp đồng mua bán",
        "parties": [{"name": "A"}, {"name": "B"}],
        "payment_terms": [{"percentage": 100, "due_date": "2026-03-01"}],
        "milestones": [{"name": "Giao hàng", "due_date": "2026-09-25"}],
    })
    assert issues == []


def test_validate_extracted_payload_valid_conditional_payload_has_no_issues():
    issues = validate_extracted_payload({
        "title": "Hợp đồng mua bán",
        "payment_terms": [
            {"percentage": 70, "trigger_event": "Ký biên bản giao nhận", "offset_days": 10, "expected_date": "2026-10-05"},
        ],
        "milestones": [
            {"name": "Bảo hành", "trigger_event": "Ký biên bản giao nhận", "duration_months": 24},
        ],
    })
    assert issues == []


# ---------------------------------------------------------------------
# split_clauses_from_raw_text — the actual production bug: clause body
# was empty in the DB for every clause of a real contract despite raw_text
# holding the complete original wording (contract 370f0098-a77a-410a-
# 9318-ddb850bb86f2). Body/heading must come from the document itself,
# never from an LLM summary.
# ---------------------------------------------------------------------

_REAL_CONTRACT_EXCERPT = """HỢP ĐỒNG CUNG CẤP DỊCH VỤ
Số: 016/2026/HĐDV-AI
Hôm nay, ngày 16/09/2026, tại TP. Hồ Chí Minh, các bên gồm:
ĐIỀU 1. PHẠM VI DỊCH VỤ
Bên B cung cấp cho Bên A dịch vụ tư vấn, cấu hình và triển khai hệ thống trợ lý AI hỗ trợ xử lý chứng
từ, hợp đồng, công nợ và báo cáo quản trị. Phạm vi chi tiết được mô tả tại Phụ lục 01 - Phạm vi công
việc, là bộ phận không tách rời của Hợp đồng.
ĐIỀU 2. GIÁ TRỊ HỢP ĐỒNG VÀ THUẾ
Nội dung Giá trị (VND)
Giá trị trước VAT 300.000.000
ĐIỀU 3. TIẾN ĐỘ VÀ MỐC NGHIỆM THU
Mốc Nội dung Ngày dự kiến
ĐIỀU 8. ĐIỀU KHOẢN CHUNG
Mọi sửa đổi, bổ sung phải được lập thành văn bản hoặc phụ lục có xác nhận của hai bên."""


def test_split_clauses_extracts_real_verbatim_body_not_a_summary():
    # This is the exact regression: Điều 1 must contain the actual text
    # beginning "Bên B cung cấp cho Bên A dịch vụ tư vấn, cấu hình và
    # triển khai...", not a shortened paraphrase.
    clauses = split_clauses_from_raw_text(_REAL_CONTRACT_EXCERPT)
    assert len(clauses) == 4
    assert clauses[0]["ordinal"] == 1
    assert clauses[0]["heading"] == "Điều 1 — PHẠM VI DỊCH VỤ"
    assert clauses[0]["body"].startswith(
        "Bên B cung cấp cho Bên A dịch vụ tư vấn, cấu hình và triển khai"
    )
    assert "Phụ lục 01" in clauses[0]["body"]


def test_split_clauses_captures_full_body_up_to_next_marker():
    clauses = split_clauses_from_raw_text(_REAL_CONTRACT_EXCERPT)
    by_ordinal = {c["ordinal"]: c for c in clauses}
    assert "Nội dung Giá trị (VND)" in by_ordinal[2]["body"]
    assert "Giá trị trước VAT 300.000.000" in by_ordinal[2]["body"]
    # Must not bleed into the next clause's own text.
    assert "TIẾN ĐỘ VÀ MỐC NGHIỆM THU" not in by_ordinal[2]["body"]


def test_split_clauses_last_clause_captures_to_end_of_document():
    clauses = split_clauses_from_raw_text(_REAL_CONTRACT_EXCERPT)
    last = clauses[-1]
    assert last["ordinal"] == 8
    assert "Mọi sửa đổi, bổ sung" in last["body"]


def test_split_clauses_preserves_document_order_not_sorted():
    # Ordinals 1,2,3,8 in this excerpt — not renumbered/compacted to 1-4.
    clauses = split_clauses_from_raw_text(_REAL_CONTRACT_EXCERPT)
    assert [c["ordinal"] for c in clauses] == [1, 2, 3, 8]


def test_split_clauses_handles_lowercase_and_no_title_marker_variants():
    text = "Điều 1\nNội dung điều một.\nĐiều 2 - Nội dung điều hai."
    clauses = split_clauses_from_raw_text(text)
    assert len(clauses) == 2
    assert clauses[0]["heading"] == "Điều 1"
    assert clauses[0]["body"] == "Nội dung điều một."
    assert clauses[1]["heading"] == "Điều 2 — Nội dung điều hai."


def test_split_clauses_returns_empty_list_when_no_marker_found():
    # Never invents a clause for a document that doesn't use "Điều N".
    assert split_clauses_from_raw_text("Some contract with no clause markers at all.") == []


def test_split_clauses_returns_empty_for_blank_or_non_string_input():
    assert split_clauses_from_raw_text("") == []
    assert split_clauses_from_raw_text("   ") == []
    assert split_clauses_from_raw_text(None) == []


def test_split_clauses_ignores_clause_reference_mid_sentence():
    # A cross-reference to another clause inside body text (not at the
    # start of a physical line) must never be mistaken for a new clause
    # boundary — same document line-wrapping convention as the real
    # contracts this parses.
    text = (
        "Điều 1. Phạm vi\n"
        "Nội dung chi tiết Điều 1 nằm ở đây, không liên quan Điều 5.\n"
        "Điều 2. Giá trị\n"
        "Nội dung điều hai."
    )
    clauses = split_clauses_from_raw_text(text)
    assert len(clauses) == 2
    assert "không liên quan Điều 5" in clauses[0]["body"]
