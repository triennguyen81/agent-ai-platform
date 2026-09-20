# AccAgent Audit Remediation Report

Source: `ACCAGENT_PROCESS_AUDIT_REPORT.md`. Execution log/evidence: `ACCAGENT_AUDIT_FIX_TODO.md`.

## Summary

- **DONE: 10** — J1 (journal routing), T1 (tax-verification gate), C1 (contract progress %), C2 (milestone activation), L1 (late-payment penalty), U1 (Luồng 01 UI parity), U2 (Luồng 03 UI parity), P1x (rank-based expense policy), A1 (audit-log request-body capture), N1 (weekly Ban giám đốc report).
- **BLOCKED: 6** — P2x (role-granular/value-escalating approval matrix — system-wide RBAC redesign, out of scope for a minimal fix), CF1 (20M vs 30M cash threshold — needs a business/legal decision, not a code call), BR09 (FX rate — no live source), BR05 (Contract-side tax-status — no provider), BR19 (field-level provenance), BR10 (AR/AP bilateral offset — no document type/domain support).
- **FAILED: 0** — every test that failed during development (schema drift in two hand-rolled xunit fixtures, one stale-employee-state E2E bug) was fixed and rerun to a clean PASS before moving on; nothing was left red.

## Final closure pass (this update)

Re-examined the single remaining P2 (P2x) for a genuinely-implementable minimal subset, and re-classified all 6 BLOCKED items against the standard taxonomy (EXTERNAL_DEPENDENCY / BUSINESS_DECISION_REQUIRED / ARCHITECTURE_FUTURE_WORK / ACTUALLY_IMPLEMENTABLE_NOW). None reclassified to ACTUALLY_IMPLEMENTABLE_NOW — see the per-item table in `ACCAGENT_AUDIT_FIX_TODO.md` for full reasoning, missing dependency, affected flow, runtime behavior, UI-reporting status, and production risk for each.

**P2x** — considered a minimal, non-fake subset (value-based escalation to `tenant_owner` above a configured claim amount, using only existing roles). Ruled out: `apply-expense`/`apply-receipt` are gated by the `department_admin` authorization policy at the route level, and `tenant_owner` structurally cannot satisfy it (`IsTenantGovernor` requires `DepartmentId: null`; `IsDepartmentAdmin` requires `DepartmentId: not null` — mutually exclusive by design). Enabling this would mean changing the route-level authorization contract, a real API-surface change, not a minimal fix. Classified **ARCHITECTURE_FUTURE_WORK**, remains BLOCKED.

**Safe-failure audit finding and fix**: while verifying "BLOCKED capabilities must fail safely / never silently guess," found that `vouchers/[id]/page.tsx`'s journal-suggestions panel showed the same generic "AI will suggest soon, check back in a few seconds" message whether extraction genuinely hadn't run yet OR the outcome could **never** auto-populate an entry (FX with no rate source; an outcome the business document says needs a human decision; an unknown payment method the router won't guess between cash/bank). That generic message was actively misleading — implying a transient wait for cases that will never resolve on their own. Fixed: the panel now shows the specific, honest reason (derived entirely from data already fetched client-side — no new backend field) for these three cases, falling back to the original message only when extraction genuinely just hasn't run yet. No backend/API change; verified with `tsc --noEmit` (clean), `npm run build` (PASS), and a fresh Luồng 02 E2E run (117/117, unaffected as expected since this is UI-only).

Re-verified, all unchanged since the prior pass and confirmed with fresh evidence this pass: zero ledger-write endpoints reachable from Agent tools (`grep` count 0), 12 `RequireAuthorization("department_admin")` gates across `VoucherEndpoints.cs`, 8 `IsSelfApproval` guard call sites — tenant isolation, RBAC, and HITL posture are unchanged.

## Changes by Phase

### Phase 1 — P1 Journal routing
New `Aiq.Gateway.Accounting.JournalRoutingRules` — a pure, deterministic module that routes an already-classified Luồng 02 (receipt-match) or Luồng 03 (expense-claim) outcome to the business process document's closed situational journal-entry allow-list. No LLM involved.
- Receipt outcomes: `FULL_MATCH`/`PARTIAL_PAYMENT` → Nợ 1111 (TM) hoặc 1121 (CK) / Có 131; `OVERPAYMENT` → splits into the matched leg (131) plus a separate excess leg (3388); `ADVANCE_PAYMENT` → Có 131 (trả trước — never auto-selects 3387, which the data has no signal for); `UNKNOWN_PAYER`/`CUSTOMER_NO_OPEN_RECEIVABLE`/`REVERSED_TRANSACTION` → no entry, review only; `FOREIGN_CURRENCY` → Blocked (no FX rate source); unknown payment method → NeedsReview (never guesses cash vs. bank).
- Expense settlement: VAT (1331), advance-offset (141), and payable/recovery (334) legs are fully deterministic; the expense-recognition debit leg (641/642/627 per category) is only proposed when an admin has actually configured `department_expense_policies.debit_account` (new, migration `040`) for that category — otherwise NeedsReview, never an invented mapping.
- `POST /vouchers/{id}/journal-suggestions` now validates every submitted `(debit, credit)` pair against the same allow-list for receipt/expense_claim vouchers — rejects (400) anything outside it, closing the gap for the Agent's free-form `suggest_journal_entry`/`save_journal_suggestions` path too.
- Tests: xunit `JournalRoutingRulesTests` (14 cases, including a hand-verified worked-example replay), receipt E2E §11 (14 assertions), expense E2E §16 (6 assertions).

### Phase 2 — P1 Tax-verification gate
`vouchers.tax_verification_status` (tri-state: verified/invalid/unavailable/null, migration `041`) alongside the existing one-way `tct_verified`. New `POST /vouchers/{id}/tax-verification/reject` endpoint + `reject_tax_verified` Agent tool records a genuine negative or provider-unavailable outcome — never silently left as "unverified" with no trace of why. `expense_claim_results.requires_tax_verification` computed at propose time (true iff any item carries a real `invoice_no`). `ApplyExpenseAsync`'s `expense_claim` case — the only path that settles the ledger — blocks (409 `tax_verification_required`) unless status is exactly `'verified'`. Tests: expense E2E §17 (13 assertions: unverified/invalid/unavailable all block; bypass attempt blocked; verified clears the gate; retry consistent).

### Phase 3 — Contract progress % + milestone activation
`contracts.progress_percent` (migration `042`), recomputed as a pure `SUM(percentage)` over `status='paid'` installments inside `ConfirmMatchAsync`'s existing transaction — never an increment, so retries can't double-count, and a `'partial'` installment contributes nothing (never marks false full progress). Milestone activation uses the one unambiguous correspondence the schema supports — ordinal sequencing (paying installment K activates milestone K+1, matching the business document's own worked example), only ever touching a still-`pending`, trigger-event-based milestone with no fixed `due_date` yet. Tests: receipt E2E new assertions (progress recomputed to 100%, next milestone activated with a computed `expected_date`, idempotent on retry).

### Phase 4 — Late-payment penalty
New `ContractInstallmentPenaltyJob` (daily 07:00 VN), migration `043` adds `penalty_overdue_amount/days`, `penalty_rate`, `penalty_amount`, `penalty_calculated_at` to `contract_payment_schedules`. Scoped strictly to the one case where every input is unambiguous: `status='pending'` only (never `'partial'` — the remaining unpaid balance isn't tracked anywhere, so it can't be computed without guessing), a real past `due_date`, and the contract's own `penalty_percent` actually set. Pure recompute every run; clears stale values once no longer overdue or once paid. Tests: xunit `ContractInstallmentPenaltyJobTests` (5 cases: not-overdue, overdue-computes-correctly, partial-never-guessed, ambiguous-terms-no-rate-configured, retry-idempotent-and-clears-on-paid).

### Phase 5 — UI parity
Luồng 01 (`contracts/[id]/page.tsx`): `parties[]` (fetched but never even destructured before) now renders with MST per party; subtotal/VAT breakdown, phạt chậm %, tiền giữ lại %, and tiến độ % all surfaced; payments section shows the computed penalty (days/rate/amount/calculated-at) when present.
Luồng 03 (`vouchers/[id]/page.tsx`): parses the already-computed `duplicateFlagsJson`/`policyFlagsJson` and renders per-line Cắt (policy-capped) / Chặn (duplicate) reasons. Per-line VAT/CIT reason was not added — no such per-item field exists in the backend, only claim-level aggregates (already shown); adding one would mean computing new data, outside this phase's "expose existing data" scope.
Tests: `tsc --noEmit` clean, `npm run build` PASS, contract-processing E2E 96/96, expense-payment E2E 138/138 (both unaffected by the UI-only change, confirming the API contract didn't shift).

### Phase 6 — Remaining P2/P3 items
- **P1x (rank-based expense policy)** — `department_expense_policies.position` (migration `044`, nullable, same wildcard convention as `category`) matched against `sim_employees.position` (already existed, unused). Precedence favors the row matching more of (category, position). Test: expense E2E §18.
- **A1 (audit trail lacks what-changed)** — `AuditMiddleware` now captures the JSON request body for state-changing requests (8KB cap, JSON-only), redacting known-sensitive fields (password/secret/token/apiKey/signingKey) before it reaches `audit_log`. True before/after diffing would need per-entity snapshotting — out of scope as a minimal fix; this closes the concrete "sửa gì" gap the audit named. Test: receipt E2E new assertions (body recorded correctly; password redacted).
- **N1 (weekly Ban giám đốc report)** — new `GET /tenant/reports/weekly-summary` (`tenant_governance`-gated), computed entirely from existing data: cash collected in 7 days, open/overdue receivables + overdue-customer count, approved-expense totals by department in 7 days. The notification-digest half of N1 was reconsidered and not built: every notification kind actually implemented (contract deadlines, AR overdue, receipt unknown-payer/reversed, expense returned/rejected/exception-pending) is already actionable per the PDF's own principle — there's currently no FYI-only kind to batch, so digest infrastructure would sit unused. Test: receipt E2E new section (owner reads it, plain admin denied, cross-tenant isolation confirmed).
- **P2x, CF1** — explicitly BLOCKED, see below.
- **BR09/BR05/BR19/BR10** — reconfirmed still genuinely blocked (repo-wide greps for FX rate code, contract-side HTTP tax lookups, provenance fields, and offset-document support all still return zero hits beyond what was already known).

## Migrations (all applied cleanly against a fresh database, 44/44, confirmed via `test_fresh_database_bootstrap.sh`)

| Migration | Purpose |
|---|---|
| `040_expense_policy_debit_account.sql` | `department_expense_policies.debit_account` — category→GL-account mapping for deterministic journal routing |
| `041_tax_verification_gate.sql` | `vouchers.tax_verification_status/reason/checked_at`; `expense_claim_results.requires_tax_verification` |
| `042_contract_progress.sql` | `contracts.progress_percent` |
| `043_contract_installment_penalty.sql` | `contract_payment_schedules.penalty_overdue_amount/days`, `penalty_rate`, `penalty_amount`, `penalty_calculated_at` |
| `044_expense_policy_position.sql` | `department_expense_policies.position` — rank-based policy dimension |

## Files changed

**New**: `gateway/src/Aiq.Gateway/Accounting/JournalRoutingRules.cs`, `gateway/src/Aiq.Gateway/Jobs/ContractInstallmentPenaltyJob.cs`, `gateway/src/Aiq.Gateway/Reports/WeeklySummaryReport.cs`, `gateway/src/Aiq.Gateway/Endpoints/ReportsEndpoints.cs`, 5 migrations, `gateway/tests/Aiq.Gateway.Tests/Accounting/JournalRoutingRulesTests.cs`, `gateway/tests/Aiq.Gateway.Tests/Jobs/ContractInstallmentPenaltyJobTests.cs`.

**Modified**: `IExpenseClaimRepository.cs` (journal routing wiring, tax-verification gate, rank-based policy, per-category accumulation), `IReceiptMatchRepository.cs` (journal routing wiring, progress recompute, milestone activation), `IVoucherRepository.cs` + `VoucherRecords.cs` (tax-verification tri-state, select columns), `IContractRepository.cs` + `ContractRecords.cs` (progress_percent, penalty columns exposed), `VoucherEndpoints.cs` (journal-routing call sites, allow-list validation, tax-verification reject endpoint + approval gate), `AuditMiddleware.cs` (request-body capture + redaction), `Program.cs` (new job + report registration), `voucher_tools.py` (`reject_tax_verified` tool), `contracts/[id]/page.tsx` + `vouchers/[id]/page.tsx` (UI parity), `test_receipt_ar_e2e.sh` + `test_expense_payment_e2e.sh` (all new assertions; 2 pre-existing xunit test fixtures updated for new columns; 1 stale-employee-state bug fixed in a new test section).

## Test results

| Suite | Result |
|---|---|
| Gateway xunit (`Aiq.Gateway.Tests`) | 222/222 PASS |
| Receipt/AR E2E (Luồng 02) | 117/117 PASS |
| Expense Payment E2E (Luồng 03) | 140/140 PASS |
| Contract Processing E2E (Luồng 01) | 96/96 PASS |
| Business Module ACL E2E (auth/tenant/HITL) | 65/65 PASS |
| Expense & AP E2E | 40/40 PASS |
| Customer Module E2E | 44/44 PASS |
| Supplier Module E2E | 56/56 PASS |
| Fresh Database Bootstrap (44 migrations) | PASS |
| Agent pytest | 91/91 PASS |
| Web `tsc --noEmit` | PASS |
| Web production build | PASS |

## Updated readiness

**Luồng 01: 92%** (unchanged this pass) — progress %/milestone-activation cross-flow gap closed, Kết quả-1 UI complete, reconfirmed with a fresh E2E run (96/96). Remaining gap: role-granular approval-matrix escalation (P2x, ARCHITECTURE_FUTURE_WORK) and R05 Contract-side tax-status (EXTERNAL_DEPENDENCY).

**Luồng 02: 91%** (was 90%) — journal-entry-per-outcome gap closed, late-payment penalty computed, weekly report added, reconfirmed with a fresh E2E run (117/117); +1% this pass for closing the safe-failure UI gap (FX/no-rate and unknown-payment-method cases now clearly reported instead of implying a transient AI delay). Remaining gap: FX rate application (BR09, EXTERNAL_DEPENDENCY) and AR/AP two-way offset (BR10, ARCHITECTURE_FUTURE_WORK) — both structurally blocked, correctly failing closed with no guessed values.

**Luồng 03: 91%** (unchanged this pass) — journal-routing gap closed, tax-verification is an enforced gate, per-line UI detail restored, rank-based policy added, reconfirmed with a fresh E2E run (140/140). Remaining gap: document-splitting for bundled multi-invoice photo uploads (not addressed — a genuinely separate, sizable feature) and role-granular approval matrix (P2x, ARCHITECTURE_FUTURE_WORK).

## Blocked capabilities — final classification

| ID | Classification | Missing dependency | Affected flow | Fails safely? |
|---|---|---|---|---|
| P2x | ARCHITECTURE_FUTURE_WORK | N distinct role types + per-action thresholds; would require changing the route-level `department_admin` authorization contract | 01/02/03 (approval steps) | Yes — real separation-of-duties still enforced, just role-coarse, nothing misrepresented |
| CF1 | BUSINESS_DECISION_REQUIRED | Confirmation of which threshold (20M PDF vs 30M current law) governs | 03 (cash-payment risk flag) | Yes — deterministic configured constant, never guessed per-transaction |
| BR09 | EXTERNAL_DEPENDENCY | Live/configured FX rate source | 02 (FOREIGN_CURRENCY receipts) | Yes — zero entries proposed; UI now explicitly says why (fixed this pass) |
| BR05 | EXTERNAL_DEPENDENCY | Government company-active-status lookup | 01 (counterparty validation) | Yes — no status field shown at all, nothing falsely claimed |
| BR19 | ARCHITECTURE_FUTURE_WORK | Per-field provenance schema across all extraction types | 01/02/03 (extraction) | Yes — silently absent, never silently wrong |
| BR10 | ARCHITECTURE_FUTURE_WORK | Signed bilateral offset-document type/flow | 02 (AR/AP two-way offset) | Yes — no auto-derivation attempted without the required evidence |

None of the 6 were reclassified to ACTUALLY_IMPLEMENTABLE_NOW after this pass's re-examination — see `ACCAGENT_AUDIT_FIX_TODO.md` for full per-item reasoning, runtime behavior, and production-risk detail.

- **Document splitting** (Luồng 03 step 1, "tách chứng từ"): still 1 upload = 1 voucher; not addressed this pass — not one of the 6 tracked BLOCKED items, a separate, sizable feature.

## Production readiness

**CONDITIONAL.** Both P1 findings from the audit (journal-routing correctness, tax-verification enforcement) are fixed, tested, and reconfirmed clean this pass across all three flows with fresh E2E evidence (96/96, 117/117, 140/140). All 6 remaining open items are correctly classified and fail safely — none guesses, fabricates, or silently falls back; the one UI gap found during this pass's safe-failure audit (a misleading "still processing" message for cases that can never auto-resolve) has been fixed. No regression was introduced: `tsc`/build clean, and every affected suite reconfirmed green. Recommend: READY for the three flows as they now stand; CF1 and P2x remain open items for the business/product owner to schedule (a threshold confirmation and a future RBAC project respectively), neither of which blocks correct, safe operation today.
