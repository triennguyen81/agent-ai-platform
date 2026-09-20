# Luồng 02 — Phiếu thu công nợ (Receipt/AR Matching) TODO

Overall: DONE (2026-09-11) — implementation was already complete when this
session picked the task back up; the gaps were the E2E script (RCP-15) not
actually existing on disk despite being marked DONE, and one real bug in the
upsert-guard idempotency logic that the E2E now catches as a regression test.
Both are fixed. OVERDUE_UNPAID remains a declared-but-unclassified outcome —
see Bugs.

## Architecture decisions

**Which voucher types participate**: `receipt` (phiếu thu) is the primary
type. `bank_statement`/credit-advice-style inputs are not yet a distinct
`voucher_type` in this codebase (only `receipt`, `invoice_vat*`, `payment`,
`bank_transfer`, `expense_claim` exist) — Luồng 02 is implemented for
`voucher_type='receipt'`, the type that already carries `receipt_matching`
in `VoucherExtractJobRunner`. No new voucher type is added; this is a
processing enrichment on an existing type, not a new intake surface.

**No new processing subtype/flow marker on `vouchers` itself.** A new,
minimal join table (`receipt_match_results`, one row per receipt voucher)
carries the flow's own state (outcome, confidence, contract/installment
linkage, review status) — `vouchers.status` keeps its existing meaning
(extracted/verified/rejected), unaffected.

**Canonical receivable/invoice source**: `sim_ar_ledger` open-invoice rows
(`document_type='invoice' AND credit=0`... — actually debit>0/credit=0 per
existing `GetReceivablesAsync`), exactly as already used by
`BuildArContextAsync`/`BuildReceiptMatching`. Unchanged.

**Canonical AR balance source**: `sim_customers.current_ar`, updated by the
existing, already-idempotent `ICustomerRepository.ApplyReceiptAsync`.
Unchanged — this task extends what happens *around* that call, not the
ledger-posting logic itself.

**Contract installment linkage (new)**: no FK from `sim_customers` to
`contracts` exists today. Linked via tax code:
`sim_customers.mst = contract_parties.tax_code` (same join Contract's own
CT.NEW counterparty check already uses in the other direction), then to
`contract_payment_schedules` for that contract where `status <> 'paid'`.
This is a deterministic, documented heuristic — not invented data, just a
join on the tax code both sides already store.

**`sim_ar_ledger`/`sim_customers` are real business state**, not
demo/derived data — same conclusion as the Customer module task; unchanged
here.

## Requirement | Status | action (pre-implementation matrix)

| Requirement | Status | Action |
|---|---|---|
| Voucher/Customer/AR read path | DONE (pre-existing) | none |
| Contract installment matching | MISSING | add tax_code join + FIFO-by-due-date pick of first unpaid installment |
| Outcome classification (9 canonical outcomes) | MISSING | new deterministic classifier |
| Persisted match candidate/confidence/evidence | MISSING | new `receipt_match_results` table |
| ADMIN confirm → settle receivable + installment status | PARTIAL (ledger posting exists; installment status update missing) | extend `apply-receipt` |
| ADMIN reject | MISSING | new endpoint |
| Idempotent retry | PARTIAL (ledger side already idempotent) | extend to installment status + match-result upsert |
| UI: show match/outcome/confidence/review | MISSING | extend voucher detail page |
| ACL | DONE (pre-existing `voucher.processing` + `department_admin`, already fixed for cross-creator ADMIN actions in the Expense/AP task) | reuse, no change |

## Matrix

| ID | Requirement | Status | Files | Verification |
|---|---|---|---|---|
| RCP-01 | Existing architecture analysis | DONE | this file | — |
| RCP-02 | Intake/upload reuse | DONE (no change needed) | existing `/vouchers/upload` | unchanged |
| RCP-03 | Transaction extraction | DONE (pre-existing) | VoucherExtractJobRunner | unchanged |
| RCP-04 | Customer matching | DONE (exact MST only — see decision) | ReceiptMatchRepository.ProposeMatchAsync | E2E |
| RCP-05 | AR/invoice matching | DONE (pre-existing, reused) | BuildReceiptMatching | unchanged |
| RCP-06 | Contract installment matching | DONE (new) | ReceiptMatchRepository | E2E |
| RCP-07 | Outcome engine | DONE (new) | ReceiptMatchRepository.ClassifyOutcome | E2E |
| RCP-08 | State updates | DONE (new) | ApplyReceiptAsync extension | E2E |
| RCP-09 | Exception handling | PARTIAL (8 of 9 outcomes now E2E-verified: FULL_MATCH/PARTIAL_PAYMENT/OVERPAYMENT/ADVANCE_PAYMENT/CUSTOMER_NO_OPEN_RECEIVABLE/UNKNOWN_PAYER/REVERSED_TRANSACTION/FOREIGN_CURRENCY. OVERDUE_UNPAID is declared in the `chk_receipt_match_outcome` CHECK constraint but `ProposeMatchAsync` never assigns it — no branch in the classifier produces it. Corrected from a prior version of this doc that incorrectly claimed it was implemented) | ReceiptMatchRepository.ProposeMatchAsync | scripts/test_receipt_ar_e2e.sh §2 |
| RCP-10 | Journal suggestion | DONE (unchanged — proposing a match never auto-creates one) | — | E2E negative check |
| RCP-11 | HITL/review | DONE (proposed/confirmed/rejected review_status; ADMIN-only confirm/reject) | VoucherEndpoints.cs | E2E |
| RCP-12 | ACL | DONE (reused, unchanged) | — | E2E |
| RCP-13 | UI | DONE | web/vouchers/[id]/page.tsx | build |
| RCP-14 | Audit/idempotency | DONE | receipt_match_results (upsert by voucher_id, unique) | E2E retry scenario |
| RCP-15 | Tests/E2E | DONE | scripts/test_receipt_ar_e2e.sh (new) | run |
| RCP-16 | Documentation | DONE | this file | — |

## Current

Done. All RCP-* items closed except the OVERDUE_UNPAID gap noted in RCP-09
and the Bugs section below.

## Next

Nothing required to ship. If picked up again:
1. Decide OVERDUE_UNPAID's actual trigger condition (see Bugs) and either
   implement the branch or drop it from the CHECK constraint.
2. Optionally cover `ConfirmMatchAsync`'s own "already confirmed" no-op
   branch — currently unreachable through the HTTP API (apply-receipt's own
   `status == "verified"` short-circuit means a second call never reaches
   it), so it's defensive code with no E2E path today. Low priority.

## Bugs

1. **OVERDUE_UNPAID never classified** (found 2026-09-11). The migration's
   `chk_receipt_match_outcome` CHECK constraint lists `OVERDUE_UNPAID` as a
   valid value, and a prior version of this doc's RCP-09 row claimed it was
   "implemented+documented" — it is not. `ReceiptMatchRepository.ProposeMatchAsync`
   has no branch that ever assigns it (grep confirms zero occurrences outside
   the migration file). The original spec (`docs/accagent-spec-v0.1-dac-ta-ky-thuat.md`
   §06) lists 8 outcomes, not 9, and has no direct equivalent — the closest is
   `stale_unresolved` ("Treo quá ngưỡng ngày cấu hình" — a proposal left
   un-reviewed past a configured day threshold), which is a time-based
   escalation concern (needs a background sweep + a configurable threshold),
   not a same-request classification branch like the other 8. Left
   unimplemented rather than guessed at, since inventing the trigger
   condition (e.g. "matched invoice already past due_date at receipt time"
   vs. "proposal sat un-reviewed too long") is a product decision, not an
   engineering gap. FIXED: n/a — documented only, not implemented.

2. **`ProposeMatchAsync` re-propose after reject threw** (found and fixed
   2026-09-11). The upsert's `WHERE receipt_match_results.review_status =
   'proposed'` guard meant re-proposing (e.g. a manual "Trích xuất lại", or
   the standalone `/receipt-match/propose` endpoint) on a voucher whose match
   had been ADMIN-rejected matched zero rows on both the INSERT-conflict path
   and its own UPDATE, so `QuerySingleAsync` threw `InvalidOperationException`
   ("sequence contains no elements") — an unhandled 500 through the manual
   endpoint (the background job path merely swallowed it into a log warning
   and silently dropped the outcome). FIXED: guard changed to `review_status
   <> 'confirmed'` (only a settled/confirmed row is protected from being
   overwritten), the row-not-updated case switched from `QuerySingleAsync` to
   `QueryFirstOrDefaultAsync` + fallback SELECT so a confirmed row is returned
   unchanged instead of throwing, and re-proposing a rejected row now resets
   it to `review_status='proposed'` (reviewer/reviewed_at cleared) so it goes
   back through review rather than staying silently stuck as rejected forever.
   Regression-covered by `scripts/test_receipt_ar_e2e.sh` §5.

## Verification

`gateway/src/Aiq.Gateway/Aiq.Gateway.csproj` builds clean via
`docker build -f gateway/src/Aiq.Gateway/Dockerfile gateway` (2026-09-11,
confirms the fix above compiles — `dotnet` CLI isn't installed in this
environment, so the Docker build is the compile check).

`scripts/test_receipt_ar_e2e.sh` — 70/70 passed (2026-09-11), covering: 8 of
9 outcome classifications against real `sim_ar_ledger`/`contract_*` fixtures,
idempotent re-propose (same row, no duplicate), ACL (creator-or-admin
propose, ADMIN-only reject, Department-scoped read, cross-Tenant denied),
reject → re-propose recovery (the bug-2 regression), ADMIN confirm via
`apply-receipt` settling both `sim_ar_ledger`/`current_ar` (pre-existing) and
the linked `contract_payment_schedules` row (`FULL_MATCH`→`paid`,
`PARTIAL_PAYMENT`→`partial`), and apply-receipt retry idempotency.

`npx tsc --noEmit` in `web/` — clean, no errors (2026-09-11).

No xunit coverage exists for the Receipt module (`gateway/tests/Aiq.Gateway.Tests`
has none referencing Receipt/receipt_match — that project's only new test
file, `ContractProcessingIntegrationTests.cs`, belongs to the separate
Contract Processing task and was left untouched).

## Resume

Nothing pending — see Next for the one open, deliberately-deferred item
(OVERDUE_UNPAID).
