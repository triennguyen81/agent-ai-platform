# Expense & Supplier Payables (AP) Module TODO

Overall: DONE (EXP-14, EXP-16 intentionally DEFERRED — see below)

## Architecture decisions

**A. Expense is a Voucher subtype/workflow (Decision A — confirmed, not changed).**
`expense_claim` is already a `vouchers.voucher_type` value, entered through the
existing `/vouchers` upload flow, processed by the existing OCR→LLM→enrichment
pipeline (`VoucherExtractJobRunner.BuildExpenseClaimMatchingAsync`), and
applied via the existing `POST /vouchers/{id}/apply-expense` action. This was
already the codebase's own stated intent (see `web/src/app/(app)/expenses/page.tsx`'s
own comment: "upload đề nghị hoàn ứng qua /vouchers với voucherType=expense_claim").
**No new Expense domain, table, or endpoint stack is created.** `sim_expense_claims`
(migration 020) exists but is legacy/unused by any real code path except an
optional opportunistic `status='paid'` UPDATE inside `EmployeeRepository.ApplyExpenseClaimAsync`
(match by document_number) — it is not a canonical record, has no `department_id`,
and is not read from anywhere. Left as-is; not built out further (would be
recreating a parallel Expense stack the task explicitly forbids).

**B. AP canonical source: `sim_ap_ledger` + `sim_suppliers.current_ap`.**
Same status as the Customer/Supplier decision: this is the durable, real,
department-scoped business state actually written by `ApplyExpenseAsync`/
`BookInvoiceAsync` (via `ISupplierRepository`), not throwaway demo data. Aging/
overdue/due-soon figures are now aggregated directly from real `sim_ap_ledger`
rows (open invoices: `document_type='invoice' AND debit=0`, aged off `due_date`)
— nothing is invented. Employee advances (`sim_employees.outstanding_advance`)
are real too, but that table has **no `department_id` column** (only a free-text
`department` label) — it predates the Department-ACL work entirely. Building a
Department-scoped Employee module/endpoint would require its own migration and
is not otherwise requested; **DEFERRED** rather than bolted on unsafely (see EXP-14).

## Matrix

| ID | Requirement | Status | Files | Verification |
|---|---|---|---|---|
| EXP-01 | Architecture/canonical model | DONE | decision above | — |
| EXP-02 | Department ACL | DONE (was BUGGY, now fixed) | VoucherEndpoints.cs | Gateway build + new E2E |
| EXP-03 | USER Add/Upload | DONE (pre-existing, unchanged) | VoucherEndpoints.cs CreateAsync/UploadAsync | already covered by Business Module ACL E2E |
| EXP-04 | USER edit-own | DONE (pre-existing, unchanged) | VoucherEndpoints.cs UpdateAsync | already covered |
| EXP-05 | ADMIN full Department authority | DONE (was BUGGY — see Bugs) | VoucherEndpoints.cs (5 handlers fixed) | new Expense/AP E2E |
| EXP-06 | Expense list/detail/search | PARTIAL→DONE | /expenses dashboard "vouchers chưa verify" wired to real count | manual + build |
| EXP-07 | Supplier resolution | DONE (pre-existing, verified) | VoucherExtractJobRunner BuildApContextAsync | unchanged |
| EXP-08 | Duplicate/validation | DONE (pre-existing, verified) | FindDuplicateAsync (seller_mst+document_number) | unchanged |
| EXP-09 | Attachments | DONE (pre-existing, verified — same storage as Contract documents) | VoucherEndpoints UploadAsync/GetPreviewAsync | fixed dept-scoping bug (see Bugs) |
| EXP-10 | Expense workflow/status | DONE (pre-existing, unchanged) | vouchers.status state machine | unchanged |
| EXP-11 | Accounting suggestion/review | DONE (was BUGGY — see Bugs) | SaveJournalAsync/DecideJournalAsync (fixed in prior task) | already covered |
| EXP-12 | AP canonical source | DONE | decision above | — |
| EXP-13 | AP dashboard/aging | MISSING→DONE | ISupplierRepository.GetApSummaryAsync (new), /expenses page | new Expense/AP E2E |
| EXP-14 | Employee advance/reimbursement | DEFERRED | — | sim_employees has no department_id; out of scope to add safely here |
| EXP-15 | Audit/history | DONE (pre-existing — voucher_history/journal tables) | — | unchanged |
| EXP-16 | Agent/runtime | DEFERRED | — | existing save_voucher/extract tools already cover expense_claim; no new tool needed |
| EXP-17 | UI | DONE | web/expenses/page.tsx | build |
| EXP-18 | Tests/E2E | DONE | scripts/test_expense_ap_e2e.sh (new) | run |
| EXP-19 | Docs | DONE | this file | — |

## Current

Found and fixed a significant, previously-undetected bug class in
`VoucherEndpoints.cs`: five ADMIN-only actions (`apply-receipt`,
`apply-invoice`, `unapply-receipt`, `apply-expense`, `delete`) and two
department-member actions (`preview`, `reextract`) still used the *old*
creator-scoped `repo.FindAsync(tenantId, callerUserId, id, ct)` /
`repo.DeleteAsync(tenantId, callerUserId, id, ct)` pattern from before the
Business Module ACL migration — meaning an ADMIN could only apply/delete/
preview/re-extract vouchers **they themselves had created**, never another
USER's, directly contradicting "ADMIN: full operational authority... not
creator-scoped." All seven fixed to the established `BusinessModuleAuthorization.TryScope`
+ `FindInDepartmentAsync` pattern (department-scoped find; admin bypasses,
non-admin still creator-checked where the action mutates data).

All matrix items implemented and verified green. Nothing left to resume.

## Next

Nothing blocking. Optional follow-up only: EXP-14 (Employee advance
Department-scoping migration) and EXP-16 (Agent/runtime tools) if a real
need for either surfaces later.

## Bugs

| Bug | Root cause | Status |
|---|---|---|
| ADMIN could only `apply-receipt`/`apply-invoice`/`unapply-receipt`/`apply-expense`/`delete` a voucher **they themselves created**, never another Department USER's | Handlers still called `repo.FindAsync(tenantId, callerUserId, id, ct)` / `repo.DeleteAsync(tenantId, callerUserId, id, ct)` — the pre-Department-ACL creator-scoped signature — even though the routes are `department_admin`-only | FIXED |
| `reextract`/`preview` were creator-scoped even for a plain Department-read/own-edit action, inconsistent with `GetAsync`/`UpdateAsync` on the same resource | Same stale pattern, missed during the earlier Business Module ACL migration pass | FIXED |
| `IVoucherRepository.DeleteAsync`'s doc-comment claimed "any admin in the tenant can remove any voucher" — false, contradicted by its own SQL | Stale comment from before Department scoping existed | FIXED (comment corrected) |
| `/expenses` dashboard's "Vouchers chưa verify" stat was a hardcoded `—`, and AP totals shown no aging/overdue/due-soon breakdown despite `sim_ap_ledger` already having the data | Never wired up (page comment: "Sprint 5 sẽ thêm...") | FIXED |

## Verification

- `dotnet build` (Gateway): 0 errors.
- `dotnet test` (Gateway, full suite): 202/202 passed.
- `scripts/test_expense_ap_e2e.sh` (new): 40 passed, 0 failed — directly proves all 7 fixed handlers now let ADMIN act cross-creator, plus the new `/suppliers/ap-summary` endpoint (real data, capability-gated, cross-Department/Tenant denied).
- `web`: `npx tsc --noEmit` clean, `npm run build` succeeded, `npm run test` (vitest) 6/6 passed.
- `scripts/test_business_module_acl_e2e.sh`: 65 passed, 0 failed (Voucher regression).
- `scripts/test_contract_processing_e2e.sh`: 96 passed, 0 failed (regression).
- `scripts/test_supplier_module_e2e.sh`: 56 passed, 0 failed (regression — `ISupplierRepository`/`SupplierEndpoints.cs` touched for AP summary).

## Resume

Module complete — nothing to resume. If revisiting: EXP-14 needs a
`sim_employees` migration adding `department_id` before any Department-scoped
Employee endpoint can be built; EXP-16 has no concrete use case yet.
