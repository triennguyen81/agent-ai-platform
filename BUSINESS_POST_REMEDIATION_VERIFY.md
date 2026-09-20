# Post-Remediation Independent Verification

Scope: verify that `BUSINESS_FIX_TODO.md`/`BUSINESS_FIX_REPORT.md`'s claims are actually reflected in the implementation, against `BUSINESS_FLOW_GAP_ANALYSIS.md`. Read-only: no code was modified. Evidence below is either a direct code citation or a freshly re-executed test run (re-run independently in this pass, not just cited from the prior remediation session).

## 1. Self-approval is server-side blocked

**PASS**

- `BusinessModuleAuthorization.IsSelfApproval(creatorUserId, reviewerUserId)` — `gateway/src/Aiq.Gateway/Security/BusinessModuleAuthorization.cs:28`.
- Called at all 8 ledger/decision-consequence sites in `VoucherEndpoints.cs` (lines 191, 360, 457, 473, 494, 515, 584, 730), covering `ApplyReceiptAsync`, `RejectReceiptMatchAsync`, `RejectExpenseClaimAsync`, `DepartmentReviewExpenseClaimAsync`, `AccountingReviewExpenseClaimAsync`, `GrantExpenseExceptionAsync`, `ApplyInvoiceAsync`, `ApplyExpenseAsync`.
- Reviewer identity comes from `BusinessModuleAuthorization.TryScope(ctx, out var principal, ...)` — derived server-side from the JWT/`HttpContext`, never from request body (`VoucherEndpoints.cs:182`).
- Test: `test_receipt_ar_e2e.sh` §8 (fresh re-run, 90/90 PASS) and `test_expense_payment_e2e.sh` §13 (fresh re-run, 120/120 PASS) both assert 403 on self-approval attempts.

## 2. Extraction routing is document-type aware

**PARTIAL**

- Implementation is correct on both sides:
  - Gateway: `VoucherExtractJobRunner.BuildExtractionPrompt` selects skill by `voucherType` (`LegacyInvoiceOnlyTypes` → `voucher-invoice-vat`, else `voucher-universal-extractor`) and the prompt text is generic per type, explicitly instructing the model not to force invoice-schema fields onto non-invoice types — `gateway/src/Aiq.Gateway/Jobs/VoucherExtractJobRunner.cs:478-513`.
  - Agent: `_SKILL_BY_TYPE`/`_DEFAULT_SKILL` mirror the same routing — `agent/src/aiq_agent/agentic/voucher_tools.py:35-57`.
- **Gap found**: neither side has any automated test exercising this routing. Both E2E scripts run the Gateway with `Agent__BaseUrl=http://agent.invalid:8000` (`test_receipt_ar_e2e.sh:91`, `test_expense_payment_e2e.sh:110`) — an intentionally-unreachable host — so the real extraction job path (`BuildExtractionPrompt`) is never invoked; `extracted_fields` is always injected directly via `apply-extraction`. No xunit test references `VoucherExtractJobRunner`/`BuildExtractionPrompt` (`grep` returned zero hits in `gateway/tests/`). No Python test references `voucher_tools.py`'s skill-selection tables either. The routing logic is correct by code inspection and compiles cleanly, but is currently untestable-by-CI dead weight from a regression-safety standpoint — a future edit could silently break it and nothing would fail.

## 3. Nonexistent skill references are gone

**PASS**

- `agent/skills/` contains exactly `voucher-invoice-vat` and `voucher-universal-extractor`. Both names used in `_SKILL_BY_TYPE`/`_DEFAULT_SKILL` and in the Gateway prompt exist on disk.
- The old nonexistent names (`voucher-receipt`, `voucher-bank-transfer`, `voucher-bank-statement`) appear nowhere as live references — the only remaining occurrence is a one-line explanatory comment in `voucher_tools.py:47` documenting the fix, not a reference.

## 4. expense_claim has a valid processing path

**PASS**

- `CreateAsync` (`VoucherEndpoints.cs:1096`) accepts any `voucherType` string (no whitelist blocking `expense_claim`).
- Full path confirmed wired: `apply-extraction` → `POST /{id}/expense-claim-result/propose` (`ProposeExpenseClaimAsync`, `VoucherEndpoints.cs:385`, type-checked against `"expense_claim"`) → `IExpenseClaimRepository.ProposeAsync` → `department-review` → `accounting-review` → `apply-expense` (`ConfirmAsync`).
- Test: `test_expense_payment_e2e.sh` exercises this path end-to-end for 9+ distinct claims (V1–V8, V_VAT, V_STAGE); fresh re-run 120/120 PASS.

## 5. VAT deductibility and CIT deductibility are separate

**PASS**

- Three distinct, independently-tracked columns on `expense_claim_results`: `vat_amount`, `vat_deductible_amount` (migration `036`), `non_deductible_amount` (pre-existing, CIT-specific) — `IExpenseClaimRepository.cs:59-60,51`.
- Proration logic separates raw vs. deductible correctly: `itemVat` accumulates into `vatTotal` unconditionally (raw, never prorated); only the invoice-bearing branch prorates into `vatDeductibleTotal`; the no-invoice branch instead adds to `nonDeductibleTotal` (CIT) — `IExpenseClaimRepository.cs:336-358`.
- Independently recomputed by hand against the `V_VAT` fixture in `test_expense_payment_e2e.sh` §14 (3 items: meal 1,100,000/vat 100,000/invoice; taxi 550,000/vat 50,000/no invoice; hotel 3,000,000/vat 300,000/invoice, capped to 2,400,000 by an existing per-item policy at `test_expense_payment_e2e.sh:274`):
  - `vatAmount` = 100,000+50,000+300,000 = 450,000 ✓ matches asserted value.
  - `vatDeductibleAmount` = 100,000 (full) + 0 (no-invoice, excluded) + 300,000×(2,400,000/3,000,000)=240,000 (capped, prorated) = 340,000 ✓ matches asserted value.
  - `nonDeductibleAmount` = 550,000 (only the no-invoice taxi line) ✓ matches asserted value.
  - This confirms the test's expected numbers are independently derivable from the business rule, not just echoing whatever the code outputs.
- Test: fresh re-run, `test_expense_payment_e2e.sh` §14, 120/120 PASS overall.

## 6. vatDeductibleAmount is now derived correctly

**PASS**

- `GetExpenseClaimExportAsync` (`VoucherEndpoints.cs:551-558`) returns `vatAmount = result.VatAmount`, `vatDeductibleAmount = result.VatDeductibleAmount`, `citNonDeductibleAmount = result.NonDeductibleAmount` — direct fields, not a derived residual.
- Confirmed the old mis-derived formula (`RequestedAmount - NonDeductibleAmount - BlockedAmount - CappedAmount`) no longer exists anywhere in the codebase (`grep` zero hits in `gateway/`, `web/`).
- Test: `test_expense_payment_e2e.sh` §14 explicitly asserts "export reflects the real vatDeductibleAmount" and "export exposes citNonDeductibleAmount distinctly" — both OK in the fresh re-run.

## 7. Luồng 03 has explicit Department → Accounting → Payment approval

**PASS**

- Three sequential stages implemented as `IExpenseClaimRepository.DepartmentReviewAsync` (`:532`), `AccountingReviewAsync` (`:550`), `ConfirmAsync` (`:569`), gated by `review_status`: `proposed → dept_reviewed → accounting_reviewed → confirmed`, added by migration `037_expense_claim_stages.sql`.
- Endpoints: `POST /{id}/expense-claim/department-review`, `POST /{id}/expense-claim/accounting-review`, both `department_admin`-gated (`VoucherEndpoints.cs:117-118`); final stage reuses existing `apply-expense`.
- **Minor doc inconsistency found** (not functional): the comment above `GrantExpenseExceptionAsync` in `VoucherEndpoints.cs:503` still says grant "applies it immediately (same one-step confirm-and-apply posture as ApplyExpenseAsync's plain confirm)" — this is stale. The actual `GrantExceptionAsync` implementation (`IExpenseClaimRepository.cs:605-634`) correctly does NOT touch the ledger and resets `review_status='proposed'`, re-entering the 3-stage chain, matching the report's claim. Recommend fixing the comment in a future pass; it does not affect behavior or test results.
- Test: fresh re-run, `test_expense_payment_e2e.sh` §14 (V_VAT) and §15 (V_STAGE) drive a claim through all 3 stages with 3 distinct actors; 120/120 PASS.

## 8. Stage skipping / self-approval / cross-tenant approval is blocked

**PASS**

- Stage-skip: each stage method checks the exact required prior `ReviewStatus` (`DepartmentReviewAsync` requires `"proposed"`; `AccountingReviewAsync` requires `"dept_reviewed"`; `ConfirmAsync` requires `"accounting_reviewed"`) — `IExpenseClaimRepository.cs:540,558,580`. Cannot skip from `proposed` straight to stage 3.
- Same-actor-twice: `AccountingReviewAsync` rejects if `existing.DeptReviewedBy == reviewerId` (`:559`); `ConfirmAsync` rejects if the payment-approver matches either prior reviewer (`:583-587`).
- Self-approval: endpoint-layer `IsSelfApproval` guard on both new review endpoints (`VoucherEndpoints.cs:473,494`) in addition to the existing guard on the final `apply-expense`.
- Cross-tenant/department: every repository method's `WHERE` clause filters `tenant_id=@tenantId AND department_id=@departmentId` (`IExpenseClaimRepository.cs:536,554,575`), backed by `TryScope` deriving `tenantId`/`departmentId` server-side. A cross-tenant call for a nonexistent-in-scope row returns `null` → endpoint returns 404/409, never proceeds.
- Test: `test_expense_payment_e2e.sh` §15 (14 dedicated assertions: skip-to-stage-2, skip-to-stage-3, same-actor repeat, creator self-approval at every stage) — fresh re-run, all OK. Cross-tenant is directly tested for `apply-expense`/`expense-claim-result` (§12) using the identical `TryScope`+scoped-query pattern that also gates the two new stage endpoints; there is no dedicated cross-tenant assertion naming `department-review`/`accounting-review` specifically, but the guard mechanism is identical code, not a different implementation.

## 9. Contract → Invoice has persistent linkage where implemented

**PASS**

- `vouchers.contract_id` / `vouchers.contract_installment_id`, real nullable FK columns with `REFERENCES contracts(id)` / `REFERENCES contract_payment_schedules(id)` — migration `038_voucher_contract_link.sql`.
- `LinkContractAsync` (`IVoucherRepository.cs:794-818`) validates both the contract and (if given) the installment belong to the caller's own `tenant_id`+`department_id` via `EXISTS` subqueries before writing — never auto-inferred.
- Endpoint: `PATCH /{id}/link-contract` (`VoucherEndpoints.cs:136,154`).
- Test: fresh re-run, `test_receipt_ar_e2e.sh` §10 (7 assertions: persists, contract-only link, cross-tenant denied 404, nonexistent-contract rejected 404) — 90/90 PASS overall.

## 10. Luồng 02 exception cases are deterministic/review-gated

**PASS**

- `ProposeMatchAsync`'s classifier (`IReceiptMatchRepository.cs:130-221`) is pure rule-based branching on currency/sign/MST-lookup/amount comparison — no LLM, no randomness. Order is explicit and documented (currency and sign checked before amount comparisons).
- "Open invoice" convention (`credit=0`) is consistent with the pre-existing definition already used in `ICustomerRepository.GetReceivablesAsync` (`"Only open invoices (unpaid, credit = 0)"`) — not a new/inconsistent rule introduced by this remediation.
- Review-gated: `ProposeMatchAsync` only sets `review_status='proposed'`; the ledger is only touched by the `department_admin`-gated `apply-receipt` (`ApplyReceiptAsync`), which also carries the self-approval guard (item 1).
- Test: fresh re-run, `test_receipt_ar_e2e.sh` §2 (all 8 outcomes: FULL_MATCH, PARTIAL_PAYMENT, OVERPAYMENT, ADVANCE_PAYMENT, CUSTOMER_NO_OPEN_RECEIVABLE, UNKNOWN_PAYER, REVERSED_TRANSACTION, FOREIGN_CURRENCY) — 90/90 PASS.

## 11. Notification retries are idempotent

**PASS**

- `InsertManyAsync` uses `ON CONFLICT (tenant_id, user_id, kind, ref_id) WHERE ref_id IS NOT NULL DO NOTHING` (`INotificationRepository.cs:143-147`), backed by a real partial unique index `uq_notifications_tenant_dedupe` on the same 4 columns (migration `015_business_tenant_hardening.sql:31-33`) — DB-enforced, not just app-level best-effort.
- `NotifyDepartmentAdminsAsync` and the receipt/expense event-driven call sites all funnel through this same `InsertManyAsync`.
- `AccountsReceivableOverdueJob.StageKind` is a pure function of `daysOverdue` (highest-stage-wins), so a re-run with unchanged data always resolves to the same `kind` and gets deduplicated; when days-overdue crosses a threshold, `kind` changes, producing a genuinely new (not duplicate) notification — this is correct escalation, not a dedup failure.
- Test: fresh re-run, `Aiq.Gateway.Tests` xunit suite (203/203 PASS) includes `AccountsReceivableOverdueJobTests.ScanNotifiesDepartmentAdminsOnlyOnceAndEscalatesAsDaysPass`, which explicitly re-runs the job twice same-day (no duplicate) and then again after advancing `due_date` (escalates to a new `kind`). `test_receipt_ar_e2e.sh` §2 also re-proposes `V_UNKNOWN` and asserts the notification count stays at 1.

## 12. Agent still cannot post official accounting entries

**PASS**

- `agent/src/aiq_agent/agentic/voucher_tools.py` exposes exactly 6 tools: `extract_voucher_fields`, `save_voucher`, `verify_einvoice_tct`, `confirm_tax_verified`, `suggest_journal_entry`, `save_journal_suggestions`. None call `apply-receipt`, `apply-expense`, `apply-invoice`, `grant-expense-exception`, `reject-*`, or the new `department-review`/`accounting-review` endpoints.
- Repo-wide `grep` for those endpoint path fragments across `agent/src/` returns zero hits.
- `save_journal_suggestions` persists to the `journal_suggestions` table via `POST /{id}/journal-suggestions` — a proposal table, distinct from the real ledger tables (`sim_ar_ledger`, `sim_ap_ledger`, `sim_employees`); a human must separately call `DecideJournalAsync` (`department_admin`-gated) to act on a suggestion.
- All ledger-writing endpoints require `RequireAuthorization("department_admin")`, a real ASP.NET Core policy asserting `PrincipalAuthorizationPolicy.IsDepartmentAdmin` (`Program.cs:299`) evaluated server-side from the JWT — the Agent's tools run bound to whichever human's JWT initiated the request (`build_voucher_tools(*, token, gateway_base)`, "Per-request factory bound to caller's JWT"), so the Agent has no identity or privilege of its own beyond that human's.

## 13. No duplicate active Invoice/OCR/accounting engines remain

**PARTIAL**

- The specific duplication this remediation targeted (R17/R20 — voucher-extraction skills) is genuinely fixed and verified: exactly 2 skill directories remain (`voucher-invoice-vat`, `voucher-universal-extractor`), non-overlapping by design (legacy-only vs. everything-else), and only one Gateway extraction job class exists (`VoucherExtractJobRunner`).
- **Separate, out-of-scope finding**: `agent/skills/` also contains both `accounting-vietnam` and `accounting-vn-style` — two keyword-activated skills whose descriptions overlap substantially (both keyed on "kế toán", "định khoản", "hạch toán", VAS/TT200). `accounting-vietnam`'s own description says it covers "mọi yêu cầu kế toán doanh nghiệp" except pure-tax and pure-style questions, suggesting `accounting-vn-style` may be intentionally scoped narrower (terminology/style only) rather than a true duplicate — but this was never evaluated in the original gap analysis (R20 only covered voucher-extraction skills) and was not touched by this remediation. Flagging it rather than silently passing it: it needs its own investigation to confirm genuine non-duplication.

## 14. All migrations are safe for an existing DB

**PASS**

- `036`: `ADD COLUMN ... NOT NULL DEFAULT 0` — safe, backfills existing rows, no table rewrite in PG 16 for a constant default.
- `037`: nullable `ADD COLUMN`s (no default needed); `DROP CONSTRAINT IF EXISTS` + re-`ADD CONSTRAINT` wrapped in `EXCEPTION WHEN duplicate_object THEN NULL` (idempotent); new CHECK is a superset of the old allowed values — existing rows in any of the original 3 states remain valid.
- `038`: nullable `ADD COLUMN`s with FK references, `CREATE INDEX IF NOT EXISTS` — safe, no data migration needed.
- `039`: narrows `receipt_match_results`'s outcome CHECK by removing `'OVERDUE_UNPAID'`. Verified via repo-wide `grep` that no INSERT/UPDATE anywhere (application code or migration history) ever wrote that value — it was declared in the original `033` constraint but structurally unreachable. Confirmed against the live dev database (`aiq-postgres`, read-only `SELECT count(*) FROM receipt_match_results WHERE outcome='OVERDUE_UNPAID'` → 0) that no existing row would violate the narrower constraint.
- `035`: `DROP TABLE IF EXISTS sim_expense_claims` — mechanically safe (no other table has an FK to it, confirmed by grep; live dev DB has 0 rows in it), but genuinely irreversible on any DB that does hold data in that table. A rename-instead-of-drop would have been more defensive, though the drop is consistent with R22's explicit, already-tested finding that the table is dead.
- End-to-end proof: `test_fresh_database_bootstrap.sh` applies all 39 migrations against a brand-new DB and confirms the Gateway starts, passes its own internal invariant checks, and the migration count is stable across a restart — re-run in the prior session (not re-run again in this pass since it's unaffected by anything checked here), PASS.

## 15. No test was weakened merely to obtain PASS

**PASS**

- Only one test file was modified during remediation: `scripts/test_fresh_database_bootstrap.sh`, changing one required-table name from `sim_expense_claims` to `expense_claim_results` (`git diff` shows a 1-line substitution, not a removal or loosening) — required because migration `035` intentionally dropped the legacy table; the check still requires exactly the same count of named tables to exist and fails loudly otherwise.
- All other E2E scripts touched by this remediation (`test_receipt_ar_e2e.sh`, `test_expense_payment_e2e.sh`) are untracked/new-to-this-body-of-work files with no prior git history to have weakened against.
- Spot-verified by independent hand recomputation (see item 5) that the R15 VAT/CIT test's expected numeric values are derivable from the stated business rule and fixture inputs, not just mirroring whatever the implementation currently outputs.
- No assertion in any reviewed test uses a tautological or vacuous pattern (e.g., asserting a value equals itself, or asserting only HTTP 200 without checking the response body's substance) in the sections reviewed.

---

## BLOCKED items — independently re-confirmed as genuinely unimplemented

### R09 — FX gain/loss
**BLOCKED, confirmed.** `grep` for `fx_rate`/`exchange_rate`/`FxRate`/`ExchangeRate` across `gateway/src/Aiq.Gateway/` found only an unrelated `exchange_rate` column on the Supplier/PO line-item table (a static manual-entry field defaulting to 1, `ISupplierRepository.cs:122,497`, not a live rate source). `IReceiptMatchRepository.cs` computes zero gain/loss — `FOREIGN_CURRENCY` is a terminal classification with no 515/635 posting logic anywhere.

### R05 (Contract-side tax-portal check)
**BLOCKED, confirmed** (the e-invoice-lookup half of R05 is genuinely DONE — see item 1's persistence fix — but the Contract-side check remains blocked). `IContractRepository.cs:348` performs only a regex format check (`^\d{10}(?:\d{3})?$`) on `tax_code`; `grep` for `HttpClient`/`http://`/`https://` in that file returns zero hits — no external call of any kind.

### R19 — field-level provenance
**BLOCKED, confirmed.** Repo-wide `grep` for `provenance`/`source_field`/`field_source`/`extracted_by`/`confidence_per_field` across `gateway/`, `agent/`, `web/` returns zero hits.

### R10 — AR/AP two-way offset
**BLOCKED, confirmed.** Repo-wide `grep` for `offset_document`/`bien_ban_bu_tru`/`ar_ap_offset`/`OffsetDocument` returns zero hits. The one `offset` match found in migrations (`028_contract_conditional_dates.sql:offset_days`) is an unrelated conditional-date field for contract deadlines, not AR/AP debt offsetting.

None of the 4 BLOCKED items were silently turned into DONE — all remain exactly as unimplemented as the remediation report claims.

## Fresh test evidence (re-executed independently in this verification pass, not reused from the remediation session)

| Suite | Result |
|---|---|
| Gateway xunit (`Aiq.Gateway.Tests`) | 203/203 PASS |
| Receipt/AR E2E (Luồng 02) | 90/90 PASS |
| Expense Payment E2E (Luồng 03) | 120/120 PASS |
| Contract Processing E2E (Luồng 01) | 96/96 PASS |

## Findings summary

- 13 PASS, 2 PARTIAL, 0 FAIL.
- The 2 PARTIALs are not regressions or false claims — they are honest gaps the remediation's own report did not surface: (a) extraction-routing correctness (item 2) rests on code review alone with zero automated test coverage on either the Gateway or Agent side, and (b) a pre-existing, out-of-scope accounting-skill overlap (item 13) that was never evaluated by the original gap analysis.
- One stale code comment was found (item 7, `VoucherEndpoints.cs:503`) that describes superseded behavior; it has no functional effect since the actual logic was correctly rewritten, but should be corrected for future readers.
