# AccAgent Business Sample Test Matrix

Companion index to `tests/fixtures/business_samples/`. Every row below is backed by an `expected` block in the corresponding JSON fixture file — this matrix is a navigation aid, not a duplicate source of truth. Rows marked **VERIFIED LIVE** were actually executed against a freshly-bootstrapped, fully isolated demo instance while building this pack (not just written and assumed correct).

## Luồng 01 — Hợp đồng

| Case | Fixture | Expected workflow state | Expected alerts | Journal entries |
|---|---|---|---|---|
| HD-001 Normal contract | `luong01_contracts.json` | SCHEDULE_GENERATED | none | 0 (contracts never post) |
| HD-002 Missing appendix | `luong01_contracts.json` | SCHEDULE_GENERATED | MISSING_APPENDIX (warning) | 0 |
| HD-003 Template deviation | `luong01_contracts.json` | SCHEDULE_GENERATED | PENALTY_DEVIATION (warning) | 0 |
| HD-004 Ambiguous retention | `luong01_contracts.json` | SCHEDULE_GENERATED | RETENTION_DEVIATION (warning) | 0 |
| HD-005 Appendix linked to parent | `luong01_contracts.json` | SCHEDULE_GENERATED | none (appendix document uploaded before /process) | 0 |

## Luồng 02 — Phiếu thu công nợ

| Case | Fixture | Expected outcome | Journal routing | Notes |
|---|---|---|---|---|
| AR-001 Full payment | `luong02_receivables.json` | FULL_MATCH | 1 entry: 1121/131 | **VERIFIED LIVE** — progressPercent→30, milestone ordinal 2 activated |
| AR-002 Partial payment | `luong02_receivables.json` | PARTIAL_PAYMENT | 1 entry: 1121/131 | |
| AR-003 Overpayment | `luong02_receivables.json` | OVERPAYMENT | 2 entries: 1121/131 + 1121/3388 | |
| AR-004 Advance before invoice | `luong02_receivables.json` | ADVANCE_PAYMENT | 1 entry: 1121/131 (never auto-selects 3387) | |
| AR-005 Unknown payer | `luong02_receivables.json` | UNKNOWN_PAYER | none — review only | **VERIFIED LIVE** — 0 journal rows |
| AR-006 Known customer, unmatched invoice | `luong02_receivables.json` | CUSTOMER_NO_OPEN_RECEIVABLE | none — review only | closest real equivalent to "unmatched invoice" |
| AR-007 Reversed transaction | `luong02_receivables.json` | REVERSED_TRANSACTION | none — review only | fires `receipt_reversed_transaction` notification |
| AR-008 Overdue receivable | `luong02_receivables.json` | N/A (scheduled-job case) | N/A | no direct HTTP trigger — see `AccountsReceivableOverdueJobTests.cs` |
| AR-009 Foreign currency | `luong02_receivables.json` | FOREIGN_CURRENCY | **BLOCKED** — no_fx_rate_source_configured | **VERIFIED LIVE** — 0 journal rows, no invented rate |

## Luồng 03 — Hoá đơn thanh toán

| Case | Fixture | Expected outcome | Approved / blocked / capped | Journal routing |
|---|---|---|---|---|
| EXP-001 Normal claim with advance | `luong03_expenses.json` | APPROVED | 3,500,000 / 0 / 0 | **VERIFIED LIVE** — 641/141=3,000,000; 641/334=500,000; 1331/334=318,182 |
| EXP-002 Claim without advance | `luong03_expenses.json` | APPROVED | 300,000 / 0 / 0 | no tax-verification gate (no invoice_no) |
| EXP-003 Hotel over policy | `luong03_expenses.json` | PARTIALLY_APPROVED | 1,500,000 / 0 / 1,500,000 | flat per-item cap (see discrepancy note below) |
| EXP-004 No valid invoice | `luong03_expenses.json` | RETURN_FOR_DOCUMENTS | blocks the WHOLE claim | none — cannot reach apply-expense |
| EXP-005 Cancelled invoice | `luong03_expenses.json` | APPROVED at propose, BLOCKED at final approval | 2,000,000 / 0 / 0 | blocked by tax-verification gate (409), not at propose time |
| EXP-006 Duplicate invoice (cross-voucher) | `luong03_expenses.json` | 1006A=APPROVED, 1006B=REJECTED | 1006B blocked 880,000 | |
| EXP-007 Exception approval | `luong03_expenses.json` | WAITING_EXCEPTION_APPROVAL → APPROVED after grant | 2,000,000 capped → 3,000,000 uncapped | grant resets to 'proposed', re-enters 3-stage chain |
| EXP-008 Blocked line (within-claim duplicate) | `luong03_expenses.json` | PARTIALLY_APPROVED | 700,000 / 500,000 blocked / 0 | |
| EXP-009 VAT non-deductible | `luong03_expenses.json` | APPROVED | 450,000 / 0 / 0 | vatAmount=0, vatDeductibleAmount=0 |
| EXP-010 CIT non-deductible | `luong03_expenses.json` | APPROVED | 350,000 / 0 / 0 | nonDeductibleAmount=350,000 (same mechanic as EXP-009 — see discrepancy note) |
| EXP-011 Self-approval attempt | `luong03_expenses.json` | N/A (actor-sequence test) | N/A | 403 on department-review / apply-expense / reject, all as creator — **VERIFIED LIVE** (found in EXP-001's own run) |
| EXP-012 Wrong approval-stage attempt | `luong03_expenses.json` | N/A (actor-sequence test) | N/A | 409 on skip-stage / repeat-actor attempts |

## Customer / Supplier resolution tests

| Test | Backed by | Evidence |
|---|---|---|
| Resolve by tax code | `GET /customers/by-mst/{mst}`, and implicitly every AR-00N propose call | CUS-001 through CUS-009 each have a distinct MST |
| Resolve by normalized name / ambiguous match | `POST /customers` (and `/suppliers`) — `possibleDuplicates` via pg_trgm `similarity(name, ...) > 0.4`, informational only, never blocks creation | `ICustomerRepository.cs`/`ISupplierRepository.cs` `CreateAsync` |
| New master-data proposal | Contract processing's `NEW_COUNTERPARTY` alert + `contract_counterparty_proposals` row when a contract party's MST isn't yet a known customer/supplier | HD-001 through HD-005 (before their counterparty is seeded) |
| Duplicate prevention | Exact `(tenant_id, mst)` / `(tenant_id, customer_code)` conflict → 409, hard DB constraint | Discovered live while building this pack — see "Corrections" below |
| Cross-tenant rejection | Standard Department-scoped ACL (404, not 403, on a cross-tenant fetch) — already covered by the platform's existing E2E suites (`test_business_module_acl_e2e.sh`), not re-demonstrated here | — |
| Inactive/unverified status | CUS-008 (`status=inactive`, `mst=null`) | `master_data.json` |

## Blocked capabilities — never faked in this pack

| Capability | Status | Behavior demonstrated |
|---|---|---|
| FX rate source | BLOCKED (no provider configured) | AR-009 — zero entries, explicit reason string, no invented rate |
| Government company-status lookup (Luồng 01) | BLOCKED (no provider configured) | Format-regex validation only; no live check attempted |
| Government invoice-status lookup (Luồng 03) | Real lookup exists (`verify_einvoice_tct`) but this pack never calls the live external API — EXP-005 uses `reject_tax_verified`/`tax-verification/reject` to record a genuine negative result, never fabricates `verified` |

## Discrepancies between the business process document and the current implementation (found while building this pack, documented — not hidden)

1. **Hotel per-night cap (EXP-003)**: the document's worked example implies a rate × nights-stayed calculation. The current policy engine has no quantity/night-count field — it applies one flat per-item cap regardless of how the limit is labeled. This pack's expected numbers use the real, flat-cap behavior.
2. **Cancelled-invoice timing (EXP-005)**: the document shows a cancelled invoice blocked immediately during claim processing. The current implementation only checks tax-portal invoice status at final payment approval (a separate, later gate) — propose-time duplicate/policy detection never consults it.
3. **VAT vs CIT independence (EXP-009/EXP-010)**: the document treats "VAT-deductible" and "CIT-deductible" as two independently settable flags per line. The current expense-claim implementation drives both from the exact same single condition (presence of `invoice_no`) — they cannot yet diverge on the same line.
4. **Four real bugs found and fixed by actually running the pack, not just inspecting it**: (a) two customers originally shared the same MST, violating `sim_customers`' real `UNIQUE(tenant_id, mst)` constraint; (b) `/apply-receipt` does not chain off `/receipt-match/propose` — they are two independent classification paths, only one of which settles anything (see `README.md`); (c) `/contracts` requires a `department_member`-policy token, not `tenant_owner`; (d) `/contracts/{id}/process` requires an agent bound to `contract.processing`/`contract.validate` for the department — an admin role does not bypass this the way it does for `/vouchers`. All four are fixed in the final fixture files and `curl_examples.sh`.

## Readiness cross-reference

This sample pack exercises, with real request/response evidence, every P1 fix and most P2 fixes from the prior remediation passes (`ACCAGENT_AUDIT_FIX_REPORT.md`): deterministic journal routing (J1), the tax-verification gate (T1), contract progress %/milestone activation (C1/C2), rank-based expense policy (P1x). It does not attempt to exercise, and does not fake, the capabilities still correctly classified as blocked there (P2x, CF1, BR09, BR05, BR19, BR10).
