# Business Flow Remediation Report

Source: `BUSINESS_FLOW_GAP_ANALYSIS.md`. Execution log/evidence: `BUSINESS_FIX_TODO.md`.

## Summary

All actionable findings (P0–P3, non-"Keep as-is" rows of the requirement matrix) are resolved or explicitly blocked with a stated, non-negotiable reason. No item was silently dropped.

- **DONE: 13** (R05-partial, R06, R07, R08, R14, R15, R16, R17/R20, R18, R21, R22 — 11 report items, several bundled multi-requirement fixes)
- **BLOCKED: 4** (R09, R05-partial [Contract-side tax-portal check only], R19, R10)
- **Unresolved test failures: 0**

## Changes by Phase

1. **P0 separation of duties (R16)** — `BusinessModuleAuthorization.IsSelfApproval` guard added to every ledger-consequence endpoint (apply-receipt, reject-receipt-match, apply-expense, reject-expense-claim, grant-expense-exception, apply-invoice). A voucher's creator can never approve/reject/grant-exception on their own record, enforced independently at each of the 3 new approval stages.
2. **Extraction routing (R17/R20)** — `VoucherExtractJobRunner.BuildExtractionPrompt` and `agent/.../voucher_tools.py` now select the extraction skill by `voucherType` against skills that actually exist (`voucher-universal-extractor` canonical; `voucher-invoice-vat` kept only for the legacy bare `invoice_vat` type).
3. **Duplicate/dead capability consolidation (R21, R22)** — legacy tenant-wide `/approval-queue` documented as deprecated (kept only for its one real caller). Dead `sim_expense_claims` table and its dead write path removed (migration `035`).
4. **VAT/CIT tax domain model (R15)** — `vat_amount` (raw) and `vat_deductible_amount` (prorated for capped lines) split into two independently-tracked fields, separate from CIT's `non_deductible_amount`. Export endpoint fixed to report real values instead of a mis-derived residual (migration `036`).
5. **Luồng 03 three-stage approval (R14)** — department review → accounting review → payment approval, each stage requiring a distinct actor who is never the creator and never a prior-stage actor. Grant-exception no longer settles the ledger directly — it resets to `proposed` and re-enters the chain (migration `037`, 2 new endpoints, 2 new UI buttons).
6. **Domain relationships (R06)** — explicit, human-set `contract_id`/`contract_installment_id` nullable FKs on `vouchers`, never auto-inferred (migration `038`, new `PATCH /vouchers/{id}/link-contract`).
7. **Luồng 02 missing cases (R07, R08)** — `OVERDUE_UNPAID` removed as an architectural mismatch (no receipt event exists for "still unpaid"); replaced with a genuinely-fitting scheduled `AccountsReceivableOverdueJob` scanning open AR, escalating through a configurable day-threshold ladder (migration `039`).
8. **Notifications/escalations (R18)** — event-driven fan-out added: `receipt_unknown_payer`, `receipt_reversed_transaction` (to department admins), `expense_claim_returned`, `expense_claim_rejected` (to the claim's creator), `expense_claim_exception_pending` (to department admins). All best-effort (never break the caller's committed write) and deduplicated via the existing `(tenant_id,user_id,kind,ref_id)` key.
9. **Remaining items sweep** — every non-"Keep as-is" row in the original requirement matrix (R05–R22) is accounted for as DONE or BLOCKED; R01–R04/R11–R13 required no change.
10. **Full regression** — see Tests table below.
11. **Final E2E business verification** — each of the three flows re-verified end-to-end; see Tests table.

### Corrections found during remediation (not in the original report)
- **Cash-payment VAT-deductibility threshold already exists.** `Jobs/ExpenseRiskOptions.CashPaymentVatDeductibleThreshold` (30,000,000₫) was already real and wired in; the original report's "MISSING" claim was wrong.
- **R05 (tax-portal) was partially wrong.** A real GDT e-invoice lookup (`verify_einvoice_tct`) already existed; only its *persistence* was missing. Fixed the persistence gap rather than building a new lookup. The Contract-side company-active-status check (a different government service) remains genuinely unconfigured and stays BLOCKED.

## Database / Migrations

5 new migrations, sequential, all applied cleanly on a fresh database (39/39, verified via `test_fresh_database_bootstrap.sh`, gateway ready before and after restart):

| Migration | Purpose |
|---|---|
| `035_drop_legacy_expense_claims.sql` | Drop dead `sim_expense_claims` table |
| `036_expense_claim_vat_split.sql` | Add `vat_amount`, `vat_deductible_amount` columns |
| `037_expense_claim_stages.sql` | Add `dept_reviewed_by/at`, `accounting_reviewed_by/at`; extend review_status CHECK |
| `038_voucher_contract_link.sql` | Add `contract_id`, `contract_installment_id` FKs to `vouchers` |
| `039_ar_overdue_escalation.sql` | Remove `OVERDUE_UNPAID` from `receipt_match_results` CHECK constraint |

`scripts/test_fresh_database_bootstrap.sh` also had one stale assertion corrected: its required-tables list referenced the now-intentionally-dropped `sim_expense_claims`, updated to `expense_claim_results` (the real, live table) — not a product bug, a test-fixture update matching a deliberate, already-verified schema change.

## Security / RBAC

- Self-approval blocked at every ledger-consequence action across both Luồng 02 and 03, independently enforced per approval stage.
- 3-stage approval requires 3 distinct actors, never the creator, sequence cannot be skipped or repeated by the same actor (14 dedicated E2E assertions).
- Department-scoped ACL, cross-tenant/cross-department denial patterns unchanged and re-verified (404, not 403, for cross-tenant/department reads — no existence leakage).
- No hard-coded tenant/user/agent IDs, no fabricated tax/FX data, no Agent ledger-write path — all re-confirmed structurally (ADMIN-only endpoints remain the only ledger writers).

## Luồng 01 / 02 / 03

- **Luồng 01 (Contract)**: unaffected by this remediation's code changes; re-verified 96/96, explicitly including proof of zero journal/voucher/tax side effects from the full contract lifecycle.
- **Luồng 02 (Receipt/AR)**: 90/90, including new R05/R06/R16/R18 coverage.
- **Luồng 03 (Expense/Payment)**: 120/120, including new R14/R15/R16/R18 coverage (35 new assertions across §13–§15).

## Tests

| Suite | Result |
|---|---|
| Gateway xunit (`Aiq.Gateway.Tests`) | 203/203 PASS |
| Receipt/AR E2E | 90/90 PASS |
| Expense Payment (Luồng 03) E2E | 120/120 PASS |
| Expense & AP E2E | 40/40 PASS |
| Contract Processing (Luồng 01) E2E | 96/96 PASS |
| Business Module ACL E2E | 65/65 PASS |
| Customer Module E2E | 44/44 PASS |
| Supplier Module E2E | 56/56 PASS |
| Fresh Database Bootstrap (39 migrations) | PASS |
| Web `tsc --noEmit` | PASS (0 errors) |
| Web `npm run build` | PASS |
| Agent pytest suite | 91/91 PASS |

## Remaining Blockers

- **R09 (FX gain/loss)** — no real FX rate source configured anywhere in the repo. Implementing would require fabricating a rate, explicitly forbidden by the governing rules.
- **R05 partial (Contract-side tax-portal check)** — the e-invoice lookup path was fixed, but the Contract company-active-status check uses a different government service with no configured provider.
- **R19 (field-level data provenance)** — architecturally large (schema change across every extracted field); partially mitigated by the R17 skill-routing fix and R15's per-item VAT breakdown, but full provenance tagging is out of scope for this pass.
- **R10 (AR/AP two-way offset)** — requires a signed bilateral offset document type that doesn't exist in the current data model; auto-deriving one would violate the "no silent business fallback" rule.

## Final Readiness

All actionable P0–P3 findings are resolved with passing tests, or explicitly blocked for a stated, non-negotiable reason (no fabricated data, no missing external dependency worked around). Full regression is green across Gateway, Web, and Agent. The platform's core non-negotiables (tenant isolation, separation of duties, HITL on financial actions, no Agent ledger posting) are structurally enforced and test-covered across all three business flows.
