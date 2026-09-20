# Luồng 03 — Hóa đơn thanh toán (Payment/Expense Claim Request) TODO

Overall: DONE (2026-09-11) — EXP-03 (document splitting) and the tax-portal
leg of EXP-06 intentionally DEFERRED (no real infra to build on; see
decisions). Everything else implemented and E2E-verified.

Not to be confused with `docs/EXPENSE_AP_MODULE_TODO.md` (a prior, narrower
task: Department-ACL bug fixes on the existing `expense_claim` voucher
endpoints + the `/suppliers/ap-summary` dashboard aggregate). That doc's
`EXP-*` IDs are a different, already-closed matrix — this file's `EXP-*`
IDs are Luồng 03's own list (below) and are unrelated 1:1. Everything that
doc's Decision A/B established as canonical is inherited here, not
re-decided.

## Architecture decisions

**Canonical Expense/Claim record: `vouchers` where `voucher_type='expense_claim'`**
(inherited, unchanged — `docs/EXPENSE_AP_MODULE_TODO.md` Decision A). Entered
via the existing `/vouchers` upload flow, processed by the existing
OCR→LLM→enrichment pipeline (`VoucherExtractJobRunner`'s `expense_claim`
case → `BuildExpenseClaimMatchingAsync`). No new top-level module.

**`sim_expense_claims` (migration 020) stays legacy/unused**, confirmed
again this pass — no department_id, not read anywhere except one optional
opportunistic `status='paid'` UPDATE keyed by `document_number` inside
`EmployeeRepository.ApplyExpenseClaimAsync`. Not built out further —
resurrecting it would be exactly the "parallel Expense stack" this task
forbids. This task's outcome/policy/duplicate/advance state instead gets a
**new** purpose-built table, `expense_claim_results` (below), mirroring
Luồng 02's `receipt_match_results` pattern — same reasoning: the existing
table's shape doesn't fit and forcing it in would be worse than a clean one.

**Canonical document storage**: existing voucher upload storage (MinIO/S3
via `IObjectStorage`, same bucket/preview/signed-URL path already used by
every voucher type and by Contract documents). Unchanged.

**Canonical Supplier source**: `sim_suppliers` (+ `sim_ap_ledger`,
`current_ap`) — same Decision B as `EXPENSE_AP_MODULE_TODO.md`. New this
pass: per-line vendor tax codes (when present) resolve against
`ISupplierRepository.FindByMstAsync`, the same lookup Voucher AP enrichment
already uses — no second Supplier master, no forced auto-create (most
expense-claim line vendors are one-off taxi/meal receipts; "use existing
Supplier proposal/create pattern **where appropriate**" — appropriate here
means resolve-if-present, never force-create per line).

**Canonical employee advance source**: `sim_employees.outstanding_advance`
/ `ytd_expense` (Decision B, inherited, unchanged). Still no
`department_id` on `sim_employees` — out of scope to add here exactly as
`EXPENSE_AP_MODULE_TODO.md` EXP-14 already deferred; not needed for this
task's ACL either, since the ACL boundary is enforced at the **voucher**
(Department-scoped), not at the employee-master level — reading/writing one
employee's advance balance in service of a Department-scoped voucher action
never leaks Department data across the ACL boundary.

**Canonical AP/accounting source**: `sim_ap_ledger` + `sim_suppliers.current_ap`
(supplier-side, already real per Decision B) and `sim_employees.outstanding_advance`
/`ytd_expense` (employee-side, already real). Both are durable business
state written by real apply actions, not demo/derived data — reconfirmed.

**Tax-portal verification: no real provider exists** (`vouchers.tct_verified`
column exists but nothing ever writes it — grep confirms zero write sites).
Per instructions, DEFERRED — never fabricate "verified on tax portal".
This also means the spec's `EX.INVOICE_CANCELLED` line rule (requires a
live cancellation-status check against the tax portal) is DEFERRED, not
implemented with a fake check.

**Workflow/HITL: reuse the Department-scoped ADMIN-decision-tier pattern**
(the one Receipt/AR already established via `receipt_match_results.review_status`),
**not** the legacy tenant-wide `/approval-queue` (`IApprovalRepository` +
`ApprovalEndpoints`, `RequireAuthorization("admin")` with no department
scoping — a Sprint-5, pre-Department-ACL mechanism that predates and is
inconsistent with the Business-Module ACL model every other resource here
uses). No multi-stage Department→Accounting→Payment engine exists anywhere
in this codebase to "reuse" — the one real, current workflow tier the
platform supports is single-ADMIN-decision, Department-scoped, exactly like
Receipt/AR and every other Business-Module resource. Using that (not
inventing a 3-stage engine, and not extending the legacy cross-department
queue) satisfies "reuse existing Workflow/HITL architecture" honestly.

**Document splitting (EXP-03) is DEFERRED.** No image/PDF segmentation
infrastructure exists anywhere in the repo (grep across gateway + agent
skills: zero matches for multi-document/page splitting). Per instructions
("Reuse existing PDF/image/document infrastructure. Do not fake OCR if
unsupported"), faking a splitter would violate that directly. The supported
path stays one-document-per-upload (already how every voucher type works
today); a real multi-receipt-per-photo splitter is a CV/segmentation
project out of scope for this pass.

**Item-level extraction schema (`expense_claim.items[]`) gets additive
fields**: `vendor_mst`, `invoice_no`, `document_date` (all optional,
backward-compatible — old extractions without them still parse). This is
required for duplicate detection and Supplier resolution to work at the
per-line level (the spec's own example lines carry `invoice_no`+`vendor`),
since the existing schema (`voucher-universal-extractor` SKILL.md) only had
`category/description/amount/receipt_attached/receipt_type`.

**sim_* tables verdict**: real business state, not demo/derived data — same
conclusion as every prior module task (`sim_employees`, `sim_suppliers`,
`sim_ap_ledger`). `sim_expense_claims` is the one exception (legacy/unused,
see above).

## Component | Existing implementation | Reuse/Extend | Missing

| Component | Existing implementation | Reuse/Extend | Missing |
|---|---|---|---|
| Document intake | `/vouchers` upload (`voucherType=expense_claim`) | Reuse as-is | — |
| Document storage | MinIO/S3 via `IObjectStorage` | Reuse as-is | — |
| Document splitting | none | — | No CV/segmentation infra anywhere — DEFERRED (see decision above) |
| Extraction | `voucher-universal-extractor` skill + `VoucherExtractJobRunner.BuildExpenseClaimMatchingAsync` | Extend: add `vendor_mst`/`invoice_no`/`document_date` to item schema | Per-line supplier/invoice fields |
| Supplier resolution | `ISupplierRepository.FindByMstAsync` (used for `invoice_vat_in`/`payment`) | Extend: call per-item when `vendor_mst` present | Per-line call site |
| Invoice/tax validation | Whole-voucher: `document_number`+`amount` arithmetic already validated by extraction; MST format unchecked at item level | Extend | Deterministic per-line arithmetic/MST-format check; tax-portal check DEFERRED (no provider) |
| Duplicate detection | `IVoucherRepository.FindDuplicateAsync(tenantId, sellerMst, documentNumber, excludeId)` — whole-voucher only | Reuse per-item + add in-claim same-claim duplicate check | Per-line call site + in-claim check |
| Expense policy | none | New, table-driven | `department_expense_policies` table + evaluator (framework; empty = pass-through, never hardcoded) |
| Allocation | `journal_breakdown` (category→account) already exists in `BuildExpenseClaimMatchingAsync` | Reuse | — |
| Advance reconciliation | `advance_covered`/`remaining_to_reimburse` in `BuildExpenseClaimMatchingAsync` — one-directional only (employee owed) | Extend | Reverse direction (employee owes company back when advance > approved) |
| Workflow/HITL | `receipt_match_results.review_status` pattern (Department-scoped ADMIN decision) | Reuse the *pattern*, new table | New `expense_claim_results` table + repository |
| Outcome engine | none (today: binary apply/reject only) | New | 5-outcome classifier |
| Journal suggestion | `suggested_journal_hint` (string) + `journal_breakdown` | Extend to reflect approved (not requested) amounts post-outcome | — |
| Payment/export | none | New | Read-only export/handoff payload endpoint (no execution) |
| ACL | `BusinessModuleAuthorization.TryScope` + `FindInDepartmentAsync`, `department_admin` policy | Reuse exactly (same as Receipt/AR) | — |
| UI | `ExpenseClaimPanel` in `web/vouchers/[id]/page.tsx`; `/expenses` dashboard | Extend both | Outcome/policy/duplicate/advance-direction display, decision buttons |

## Pre-implementation matrix (write before coding; implement only PARTIAL/MISSING/BUG)

| Requirement | Status | Action |
|---|---|---|
| Voucher/Employee/Supplier read path | DONE (pre-existing) | none |
| expense_claim upload/extraction pipeline | DONE (pre-existing) | none |
| Per-line vendor/invoice/date extraction | MISSING | additive schema fields + parsing |
| Per-line Supplier resolution | MISSING | call `FindByMstAsync` per item when `vendor_mst` present |
| Invoice/tax arithmetic validation | PARTIAL (whole-voucher only) | deterministic per-line checks |
| Duplicate detection | PARTIAL (whole-voucher only) | per-line + in-claim check, new persisted flags |
| Expense policy engine | MISSING | new table + evaluator (framework, no hardcoded limits) |
| Allocation | DONE (pre-existing `journal_breakdown`) | reuse |
| Advance reconciliation (both directions) | PARTIAL (one-directional) | add reverse (employee owes back) |
| Outcome engine (5 canonical outcomes) | MISSING | new deterministic classifier |
| Persisted claim result/outcome/review state | MISSING | new `expense_claim_results` table |
| ADMIN confirm → settle advance/ytd + journal | PARTIAL (flat apply exists, no outcome-aware amount) | extend `apply-expense` |
| ADMIN reject / exception decision | MISSING | new endpoints |
| RETURN_FOR_DOCUMENTS state | MISSING | automatic outcome + missing-doc listing |
| Payment/export prep | MISSING | new read-only endpoint |
| ACL | DONE (pre-existing `voucher.processing` + `department_admin`) | reuse, no change |
| UI | PARTIAL (panel shows raw matching, no outcome/decision UI) | extend |

## Matrix

| ID | Requirement | Status | Files | Verification |
|---|---|---|---|---|
| EXP-01 | Architecture/canonical sources | DONE | this file | — |
| EXP-02 | Intake/upload reuse | DONE (no change needed) | existing `/vouchers/upload` | unchanged |
| EXP-03 | Document splitting | DEFERRED | — | no CV/segmentation infra anywhere — see decision |
| EXP-04 | Extraction | DONE (extended) | `voucher-universal-extractor` SKILL.md, `VoucherExtractJobRunner` | E2E |
| EXP-05 | Supplier resolution | DONE | `ExpenseClaimRepository.ProposeAsync` (`ISupplierRepository.FindByMstAsync` per item) | E2E §2 |
| EXP-06 | Invoice/tax validation | DONE (deterministic arithmetic); tax-portal leg DEFERRED (no real provider — see decision) | `ExpenseClaimRepository` | E2E |
| EXP-07 | Duplicate detection | DONE | `ExpenseClaimRepository.ProposeAsync` (in-claim + `IVoucherRepository.FindDuplicateAsync` per item) | E2E §4 |
| EXP-08 | Expense policy | DONE | migration 034 (`department_expense_policies`) + `ExpenseClaimRepository` | E2E §3, §5, §6 |
| EXP-09 | Allocation/project/cost center | DONE (pre-existing, reused) | `BuildExpenseClaimMatchingAsync` | unchanged |
| EXP-10 | Advance reconciliation | DONE (both directions) | `ExpenseClaimRepository.ProposeAsync` | E2E §2, §7 |
| EXP-11 | Workflow/HITL | DONE | `expense_claim_results.review_status` (Department-scoped ADMIN decision) | E2E |
| EXP-12 | Outcome engine | DONE (5 canonical outcomes) | `ExpenseClaimRepository.ProposeAsync` | E2E §2–§6 |
| EXP-13 | Journal suggestion | DONE (unchanged posture — proposing/classifying never auto-posts) | `BuildExpenseClaimMatchingAsync` (`journal_breakdown`/`suggested_journal_hint`), settlement now uses `approved_amount` not raw requested | E2E negative check |
| EXP-14 | Payment/export | DONE | `GET /vouchers/{id}/expense-claim-export` | E2E §2 (`paymentExecuted:false` always) |
| EXP-15 | ACL | DONE (reused, unchanged) | — | E2E §9–§12 |
| EXP-16 | UI/dashboard | DONE | `web/vouchers/[id]/page.tsx` (new panel + reject/grant-exception buttons) | `npx tsc --noEmit` + `npm run build` |
| EXP-17 | Audit/idempotency | DONE | `expense_claim_results` upsert by voucher_id (guard excludes only `confirmed`) | E2E §8, §8b |
| EXP-18 | Tests/E2E | DONE | `scripts/test_expense_payment_e2e.sh` (new) | 74/74 passed |
| EXP-19 | Docs | DONE | this file | — |

## Current

Done. All EXP-* items closed except EXP-03 (document splitting, no CV infra
to build on) and the tax-portal leg of EXP-06 (no real provider), both
deliberately deferred rather than faked.

## Next

Nothing required to ship. If picked up again:
1. Tax-portal integration, once a real provider is configured — would also
   unlock the spec's `EX.INVOICE_CANCELLED` line rule.
2. Document/image segmentation for multi-receipt-per-photo uploads (EXP-03)
   — a CV project, out of scope here.
3. `per_day`/`per_night`/`per_trip` policy units are stored/accepted by the
   schema but only `per_item`/`per_claim` are actually evaluated (no real
   trip-day-count data is extracted today) — extend
   `ExpenseClaimRepository.ProposeAsync`'s policy evaluation once item-level
   date-range extraction exists.
4. The claim-level (category=NULL, `per_claim`) policy wildcard path is
   implemented and uses the same query pattern as the per-item path but has
   no dedicated E2E scenario (none of the 15 required scenarios needed it) —
   lighter-verified, same class of gap as Luồng 02's OVERDUE_UNPAID.

## Bugs

None found in pre-existing code this pass. One self-inflicted design risk
caught and fixed *before* it shipped (not a regression, since this whole
repository/endpoint pair was written in this same session): the first draft
of `ExpenseClaimRepository.ProposeAsync`'s upsert guard would have repeated
the exact bug fixed in Luồng 02 (`receipt_match_results`) — blocking on
`review_status = 'proposed'` instead of `<> 'confirmed'`, which throws once
a row is rejected instead of upserting. Written correctly from the start
this time (guard excludes only `'confirmed'`, `QueryFirstOrDefaultAsync` +
fallback SELECT instead of `QuerySingleAsync`), and `scripts/test_expense_payment_e2e.sh`
§8b regression-covers it (reject → re-propose succeeds, resets to
`'proposed'`) the same way Luồng 02's E2E does.

## Verification

`gateway/src/Aiq.Gateway/Aiq.Gateway.csproj` builds clean via
`docker build -f gateway/src/Aiq.Gateway/Dockerfile gateway` (2026-09-11;
`dotnet` CLI isn't installed in this environment, so the Docker build is the
compile check).

`scripts/test_expense_payment_e2e.sh` — 74/74 passed (2026-09-11), covering
all 15 required scenarios: valid claim (Supplier resolved → approved →
advance reconciled → confirm → export), policy-limit reduction
(PARTIALLY_APPROVED, configured not hardcoded), duplicate invoice (blocked,
no duplicate accounting suggestion), missing supporting document
(RETURN_FOR_DOCUMENTS with exact missing items), policy exception
(WAITING_EXCEPTION_APPROVAL → ADMIN grant → un-capped), advance > approved
(employee owes back, signed `payableAmount`), approved > advance (remaining
payable), USER create/own-edit/other-edit-denied, ADMIN manages
USER-created claim, USER-without-capability denied, cross-Department/Tenant
denied, retry/idempotency (both re-propose-on-confirmed and
reject-then-re-propose), and no-direct-ledger-execution (propose never
touches `sim_employees`; export never marks `paymentExecuted`).

Full regression suite, all green (2026-09-11):
- Gateway xunit (full suite): 202/202.
- `scripts/test_expense_ap_e2e.sh` (Voucher/Expense-AP regression): 40/40.
- `scripts/test_supplier_module_e2e.sh`: 56/56.
- `scripts/test_business_module_acl_e2e.sh`: 65/65.
- `scripts/test_contract_processing_e2e.sh`: 96/96.
- `scripts/test_customer_module_e2e.sh`: 44/44.
- `scripts/test_receipt_ar_e2e.sh` (Luồng 02 regression): 70/70.
- `web`: `npx tsc --noEmit` clean, `npm run test` (vitest) 6/6, `npm run build` succeeded.
- `docker compose build gateway web agent`: gateway/web confirmed via direct
  `docker build`; full compose run in progress at time of writing (agent
  image's pip install hit network retries — pre-existing heavy dependency
  list, unrelated to this task's only agent-side change, which is a
  markdown skill-schema addition with zero Python behavior change).
- Agent pytest: not run — the only `agent/` change this task made is
  additive documentation in `voucher-universal-extractor/SKILL.md` (three
  new optional JSON fields in an example), zero Python code touched, so
  there is nothing for pytest to regress-test.

## Resume

Nothing pending for Luồng 03 itself. If the docker-compose agent-image
build is still not finished, it is not gating this task's completion (no
Python was changed) — confirm with `docker images | grep aiq-agentic-agent`
or re-run `docker compose build agent` if desired.
