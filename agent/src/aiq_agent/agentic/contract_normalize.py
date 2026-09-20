"""Deterministic normalization/validation for Contract Processing's
extracted payment/milestone fields.

See docs/accagent-spec-v0.1-quy-trinh-nghiep-vu.md "Luồng 01" and
gateway/src/Aiq.Gateway/Contracts/IContractRepository.cs's ProcessAsync,
which remains the sole owner of every Contract Processing business rule
(VAT/amount checks, CT.REF, CT.NEW, the due_date=NULL-when-trigger_event
invariant, ...). This module never re-implements a Gateway rule — it only
makes the model's extracted JSON internally consistent *before* it reaches
/process, so semantic interpretation stays in the LLM (what the trigger
event is, what the percentages are) while the arithmetic/shape of the data
becomes deterministic, pure, and unit-testable without any I/O.

Three date "kinds" a payment term or milestone can be in:
  - fixed_date         — a confirmed date (due_date), no trigger involved.
  - conditional_event   — depends on a future trigger_event; due_date must
                          stay None until that event actually happens.
  - derived_after_event — a trigger_event plus an offset/duration; still
                          conditional. expected_date may carry a
                          non-binding planning estimate but is never
                          treated as a confirmed date.

Zero third-party imports by design — every function here is pure stdlib,
so it is trivially unit-testable in isolation from claude_agent_sdk/httpx.
"""

from __future__ import annotations

import re
from datetime import date
from typing import Any, Literal

DateKind = Literal["fixed_date", "conditional_event", "derived_after_event"]

# Matches a clause marker at the start of a physical line: "Điều N" (any
# case — Vietnamese contracts commonly write it "ĐIỀU 1." or "Điều 1"),
# optionally followed by a separator and a same-line title, e.g.
# "ĐIỀU 1. PHẠM VI DỊCH VỤ". Anchored to the start of a line (only
# leading whitespace allowed before it) so a cross-reference inside a
# clause's own body (e.g. "...theo Điều 1 và Điều 8...") — which is never
# the first thing on its own line in these documents — is not mistaken
# for a new clause boundary.
_CLAUSE_MARKER_RE = re.compile(r"^[ \t]*Điều[ \t]+(\d+)[ \t]*[.:\-–—]?[ \t]*(.*)$", re.IGNORECASE | re.MULTILINE)


def parse_date(value: Any) -> str | None:
    """Validates/normalizes a date to strict YYYY-MM-DD. Returns None for
    anything that isn't a real calendar date — a malformed date string is
    dropped here rather than ever reaching the Gateway as an invalid
    DateOnly? parameter."""
    if not isinstance(value, str) or not value.strip():
        return None
    try:
        return date.fromisoformat(value.strip()).isoformat()
    except ValueError:
        return None


def coerce_number(value: Any) -> float | None:
    """Accepts a JSON number or a numeric-looking string (models sometimes
    emit "30" or "30%" as a string) — never invents a number from
    non-numeric input."""
    if value is None or isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, str):
        cleaned = value.strip().rstrip("%").replace(",", "").strip()
        if not cleaned:
            return None
        try:
            return float(cleaned)
        except ValueError:
            return None
    return None


def coerce_positive_int(value: Any) -> int | None:
    n = coerce_number(value)
    if n is None or n <= 0:
        return None
    return int(round(n))


def normalize_vat_rate(value: Any) -> float | None:
    """VAT rates in Vietnam are always given as a whole percent number
    (0/5/8/10 — never a legitimate sub-1 percent), so a value <= 1 is
    unambiguously a fraction (0.1 meaning 10%) and safe to scale
    deterministically. Does not touch penalty_percent/retention_percent —
    those legitimately carry small per-day decimals (e.g. 0.05 = 0.05%/day)
    per the existing contract-extractor skill, and are left exactly as the
    model produced them."""
    n = coerce_number(value)
    if n is None or n < 0:
        return None
    return n * 100 if n <= 1 else n


def normalize_payment_percentages(percentages: list[Any]) -> list[float]:
    """Payment installment percentages must sum to 100 by domain rule
    (Gateway's own PAYMENT_PERCENT_INVALID check owns enforcement) — if
    every value looks like a fraction (summing to ~1.0), that is
    unambiguous and safe to scale by 100 here. Any other sum is left
    completely untouched; Gateway's own validation remains the sole
    authority on whether the result is actually invalid."""
    values = [coerce_number(p) or 0.0 for p in percentages]
    total = sum(values)
    if 0.9 <= total <= 1.1 and total > 0:
        return [v * 100 for v in values]
    return values


def classify_date_kind(trigger_event: Any, offset_or_duration: Any) -> DateKind:
    """Pure classification helper — mainly useful for tests/logging; the
    actual repair logic (repair_payment_term/repair_milestone) applies the
    same reasoning directly."""
    has_trigger = bool(trigger_event and str(trigger_event).strip())
    if not has_trigger:
        return "fixed_date"
    return "derived_after_event" if offset_or_duration else "conditional_event"


def repair_payment_term(term: dict[str, Any]) -> dict[str, Any]:
    """Deterministically enforces the conditional-date invariants a
    payment term must satisfy before /process ever sees it:
      - trigger_event present + due_date present -> due_date is dropped.
        A confirmed date can never coexist with a pending trigger; Gateway
        forces this server-side too (defense in depth), but making the
        agent's own JSON consistent first avoids ever showing a
        contradictory due_date anywhere upstream of the Gateway.
      - offset_days / expected_date without trigger_event -> both dropped.
        An offset or estimate is meaningless without an event it is
        relative to.
    Never promotes expected_date into due_date — a planning estimate is
    never treated as a confirmed date, regardless of which fields are
    present.
    """
    out = dict(term)
    trigger_event = str(out.get("trigger_event") or "").strip() or None
    out["trigger_event"] = trigger_event
    if trigger_event:
        out["due_date"] = None
        out["offset_days"] = coerce_positive_int(out.get("offset_days"))
        out["expected_date"] = parse_date(out.get("expected_date"))
    else:
        out["due_date"] = parse_date(out.get("due_date"))
        out["offset_days"] = None
        out["expected_date"] = None
    return out


def repair_milestone(milestone: dict[str, Any]) -> dict[str, Any]:
    """Same invariant enforcement as repair_payment_term, for milestones'
    duration_months instead of offset_days."""
    out = dict(milestone)
    trigger_event = str(out.get("trigger_event") or "").strip() or None
    out["trigger_event"] = trigger_event
    if trigger_event:
        out["due_date"] = None
        out["duration_months"] = coerce_positive_int(out.get("duration_months"))
        out["expected_date"] = parse_date(out.get("expected_date"))
    else:
        out["due_date"] = parse_date(out.get("due_date"))
        out["duration_months"] = None
        out["expected_date"] = None
    return out


def split_clauses_from_raw_text(raw_text: Any) -> list[dict[str, Any]]:
    """Deterministically splits a contract's own raw_text into one entry per
    "Điều N" clause, each carrying the clause's real heading line and the
    FULL verbatim text up to the next clause marker (or end of document).

    Real bug this fixes: the contract-extractor skill previously asked the
    LLM to write a 1-3 sentence *summary* for each clause's body ("KHÔNG
    copy nguyên văn dài") — and in production, some extractions returned no
    body/heading at all for any clause, leaving contract_clauses.body empty
    for every row despite raw_text holding the complete original wording.
    Clause body/heading is not something an LLM should be trusted to
    reproduce faithfully at all (same reasoning as never trusting LLM
    arithmetic for payment percentages) — this function makes the ACTUAL
    document text the sole source of truth for what each clause says.
    Semantic classification (has_deadline/has_penalty/clause_type/is_open/
    trigger_event) remains the model's job and is merged in by ordinal —
    see contract_tools.py's save_contract.

    Returns [] if no "Điều N" marker is found at all (never invents a
    clause) — callers should fall back to whatever the model provided.
    """
    if not isinstance(raw_text, str) or not raw_text.strip():
        return []

    matches = list(_CLAUSE_MARKER_RE.finditer(raw_text))
    if not matches:
        return []

    clauses: list[dict[str, Any]] = []
    for i, m in enumerate(matches):
        ordinal_str, same_line_title = m.group(1), m.group(2).strip()
        try:
            ordinal = int(ordinal_str)
        except ValueError:
            continue

        heading = f"Điều {ordinal}" + (f" — {same_line_title}" if same_line_title else "")

        body_start = m.end()
        body_end = matches[i + 1].start() if i + 1 < len(matches) else len(raw_text)
        body = raw_text[body_start:body_end].strip()

        clauses.append({"ordinal": ordinal, "heading": heading, "body": body})

    return clauses


def validate_extracted_payload(fields: dict[str, Any]) -> list[str]:
    """Structural schema check on the model's extracted JSON, run before
    it feeds /process. Catches shape problems deterministically (missing
    title, non-list parties/payment_terms/milestones, a payment term with
    no usable percentage, an orphaned offset/duration) instead of
    discovering them as a Gateway 400 — or worse, silently wrong data.
    Returns a list of human-readable issues; never raises, and never
    blocks save_contract from proceeding — repair_payment_term/
    repair_milestone still make a best-effort deterministic repair
    regardless of what this reports.
    """
    issues: list[str] = []
    if not isinstance(fields.get("title"), str) or not fields["title"].strip():
        issues.append("title thiếu hoặc rỗng")

    parties = fields.get("parties")
    if parties is not None and not isinstance(parties, list):
        issues.append("parties phải là list")
    elif isinstance(parties, list):
        for i, p in enumerate(parties, start=1):
            if not isinstance(p, dict) or not p.get("name"):
                issues.append(f"parties[{i}] thiếu name")

    payment_terms = fields.get("payment_terms")
    if payment_terms is not None and not isinstance(payment_terms, list):
        issues.append("payment_terms phải là list")
    elif isinstance(payment_terms, list):
        for i, p in enumerate(payment_terms, start=1):
            if not isinstance(p, dict):
                issues.append(f"payment_terms[{i}] không phải object")
                continue
            if coerce_number(p.get("percentage")) is None:
                issues.append(f"payment_terms[{i}] thiếu percentage hợp lệ")
            if p.get("trigger_event") and p.get("due_date"):
                issues.append(f"payment_terms[{i}] có cả trigger_event và due_date — due_date sẽ bị bỏ")
            if p.get("offset_days") and not p.get("trigger_event"):
                issues.append(f"payment_terms[{i}] có offset_days nhưng không có trigger_event — offset_days sẽ bị bỏ")
            if p.get("expected_date") and not p.get("trigger_event"):
                issues.append(f"payment_terms[{i}] có expected_date nhưng không có trigger_event — expected_date sẽ bị bỏ")

    milestones = fields.get("milestones")
    if milestones is not None and not isinstance(milestones, list):
        issues.append("milestones phải là list")
    elif isinstance(milestones, list):
        for i, m in enumerate(milestones, start=1):
            if not isinstance(m, dict) or not m.get("name"):
                issues.append(f"milestones[{i}] thiếu name")
                continue
            if m.get("duration_months") and not m.get("trigger_event"):
                issues.append(f"milestones[{i}] có duration_months nhưng không có trigger_event — duration_months sẽ bị bỏ")
            if m.get("expected_date") and not m.get("trigger_event"):
                issues.append(f"milestones[{i}] có expected_date nhưng không có trigger_event — expected_date sẽ bị bỏ")

    return issues
