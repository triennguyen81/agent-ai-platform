# Supplier Module TODO

Overall: DONE (SUP-12 agent/runtime intentionally DEFERRED — see below)

## Canonical model decision

`sim_suppliers` (gateway/src/Aiq.Gateway/Data/Migrations/020_simulator_business_schema.sql)
is the canonical Supplier Master — same reasoning as Customer
(`docs/CUSTOMER_MODULE_TODO.md`): it already has `tenant_id`+`department_id`
(migration 030), a unique constraint on `(tenant_id, supplier_code)`, and is
the only supplier-shaped table in the schema. `ISupplierRepository`
(`ApplyExpenseAsync`/`BookInvoiceAsync`/`FindByMstAsync`/
`MatchInvoiceToPurchaseOrderAsync`) already reads/writes it for the whole
AP/expense flow. No second table created.

Difference from Customer worth noting: `sim_suppliers` has **no** unique
constraint on `mst` (only `sim_customers` got one originally). Adding one
via migration risks failing on an environment with legacy duplicate MSTs
already seeded — not adding it. Duplicate-MST prevention for new rows is
enforced at the application level in `CreateAsync` (409) regardless,
exactly like the `customer_code` check — a missing DB-level backstop here
is a deliberately accepted, low-risk gap (informational only in the TODO,
not a blocking bug).

`contract_parties`/`contract_counterparty_proposals` CT.NEW check was
already widened during the Customer task to check both `sim_customers` AND
`sim_suppliers` by tax code — SUP-10 only needs verification, not new work.

Bank/account info: **not added**. Current architecture has no secure
storage pattern for payment/bank account data anywhere in the schema, and
inventing one (encryption at rest, access audit) is out of scope for this
task — DEFERRED.

## Matrix

| ID | Requirement | Status | Files | Verification |
|---|---|---|---|---|
| SUP-01 | Canonical model | DONE | migration 032 | decision above; Gateway build green |
| SUP-02 | Department ACL | DONE | SupplierEndpoints.cs | Supplier E2E §8/§9 |
| SUP-03 | USER Add | DONE | SupplierEndpoints.cs CreateAsync, ISupplierRepository.cs CreateAsync | Supplier E2E §1 |
| SUP-04 | USER edit-own | DONE | SupplierEndpoints.cs UpdateAsync | Supplier E2E §4 |
| SUP-05 | ADMIN full Department authority | DONE | same | Supplier E2E §6 |
| SUP-06 | Search/detail | DONE | web/suppliers/page.tsx, web/suppliers/[id]/page.tsx (new) | build + Supplier E2E |
| SUP-07 | Duplicate detection | DONE | ISupplierRepository.cs CreateAsync (hard 409 + fuzzy pg_trgm warning) | Supplier E2E §3 |
| SUP-08 | Tax validation (local format only) | DONE | Suppliers.SupplierValidation.IsValidMstFormat | Supplier E2E §2 |
| SUP-09 | Deactivate/archive | DONE | ISupplierRepository.cs SetStatusAsync, SupplierEndpoints.cs | Supplier E2E §7 |
| SUP-10 | Contract integration (no dup master) | DONE (fixed during Customer task, verified here) | IContractRepository.cs ProcessAsync CT.NEW | Contract E2E regression |
| SUP-11 | Voucher/Expense integration | DONE (pre-existing, verified) | VoucherExtractJobRunner.cs BuildApContextAsync | Supplier E2E §10 |
| SUP-12 | Agent/runtime | DEFERRED | — | no chat-based supplier-creation flow requested |
| SUP-13 | UI | DONE | web/suppliers/page.tsx, suppliers/[id]/page.tsx, supplier-form.tsx, supplier-add-panel.tsx, supplier-lifecycle-actions.tsx | `tsc --noEmit` + `next build` + vitest all green |
| SUP-14 | Audit/history | DONE | migration 032 supplier_history table, written on create/deactivate/reactivate | Supplier E2E (indirect) |
| SUP-15 | Tests/E2E | DONE | scripts/test_supplier_module_e2e.sh (new) | run in progress |
| SUP-16 | Docs | DONE | this file | — |

## Current

Implementation complete, mirroring Customer's verified pattern exactly
where semantics match. One deliberate deviation from Customer, called out
in the canonical-model section above: mutations (create/update) require
"supplier.management" specifically, not the read-level "supplier.management"
OR "expense.processing" policy the route group uses for GETs — an
expense-processing-only USER can view suppliers but not create/edit the
master. Added a dedicated E2E scenario (§8b) for this since it has no
Customer equivalent to copy.

All matrix items implemented and verified green. Nothing left to resume.

## Next

Nothing blocking. Optional follow-up only: SUP-12 agent/runtime tools if a
chat-based "tạo nhà cung cấp mới" flow is ever requested.

## Bugs

| Bug | Root cause | Status |
|---|---|---|
| `test_supplier_module_e2e.sh`'s cross-tenant login (`admin-b`) got 401 instead of the expected 404 | Test-script-only issue: `/auth/*` is a global fixed-window rate limiter (10/min, by design, since there's no user identity to key by pre-login) — this script's setup issues 11 register/login calls, one more than Customer's equivalent script (the extra "Expense User" persona added for §8b), tripping the limiter on the last login so the token came back empty | FIXED (added a 61s pacing sleep before the last 2 logins — not a product bug, no product code changed) |

## Verification

- `dotnet build` (Gateway): 0 errors.
- `dotnet test` (Gateway, full suite): 202/202 passed.
- `scripts/test_supplier_module_e2e.sh`: 56 passed, 0 failed.
- `web`: `npx tsc --noEmit` clean, `npm run build` succeeded, `npm run test` (vitest) 6/6 passed.
- `scripts/test_contract_processing_e2e.sh`: 96 passed, 0 failed (regression check — CT.NEW's sim_suppliers path, already fixed during Customer task).
- `scripts/test_business_module_acl_e2e.sh`: 65 passed, 0 failed (regression check — Supplier ListAsync signature change).

## Resume

Module complete — nothing to resume. If revisiting: SUP-12 (agent/runtime
tools for supplier create/search) is the only deferred item.
