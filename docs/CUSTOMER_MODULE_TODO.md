# Customer Module TODO

Overall: DONE (CUS-12 agent/runtime intentionally DEFERRED — see below)

## Canonical model decision

`sim_customers` (gateway/src/Aiq.Gateway/Data/Migrations/020_simulator_business_schema.sql)
is the canonical Customer Master for this deployment phase. It already has
`tenant_id`+`department_id` (added by migration 030), unique constraints on
`(tenant_id, customer_code)` and `(tenant_id, mst)`, and is the ONLY
customer-shaped table in the schema — Vouchers' entire AR flow
(`ApplyReceiptAsync`/`BookInvoiceAsync`/`FindByMstAsync`) already writes to
and reads from it. No second "customers" table exists or will be created.
The `sim_` prefix is a legacy naming artifact from the original demo-seeding
phase (see web/src/app/(app)/customers/page.tsx's own footnote: "simulator
for pilot, same structure when MCP-synced from a real ERP later") — it is
not a throwaway/derived table; a rename is out of scope here (touches FKs
across 3+ files) and is not required to make the module correct.

`contract_parties` / `contract_counterparty_proposals` remain **contract-scoped
snapshots**, not a second Customer master — CT.NEW's "propose new
counterparty" check is being widened (CUS-10) to first check `sim_customers`/
`sim_suppliers` by tax code so an already-known customer never gets a false
"open new code" proposal.

## Matrix

| ID | Requirement | Status | Files | Verification |
|---|---|---|---|---|
| CUS-01 | Canonical model | DONE | migration 031 | decision above; Gateway build green |
| CUS-02 | Department ACL | DONE | CustomerEndpoints.cs | Customer E2E §8/§9 |
| CUS-03 | USER Add | DONE | CustomerEndpoints.cs CreateAsync, ICustomerRepository.cs CreateAsync | Customer E2E §1 |
| CUS-04 | USER edit-own | DONE | CustomerEndpoints.cs UpdateAsync | Customer E2E §4 |
| CUS-05 | ADMIN full Department authority | DONE | same | Customer E2E §6 |
| CUS-06 | Search/detail | DONE | web/customers/page.tsx, web/customers/[id]/page.tsx (new) | build + Customer E2E list/get |
| CUS-07 | Duplicate detection | DONE | ICustomerRepository.cs CreateAsync (hard 409 + fuzzy pg_trgm warning) | Customer E2E §3 |
| CUS-08 | Tax validation (local format only) | DONE | CustomerValidation.IsValidMstFormat | Customer E2E §2 |
| CUS-09 | Deactivate/archive | DONE | ICustomerRepository.cs SetStatusAsync, CustomerEndpoints.cs | Customer E2E §7 |
| CUS-10 | Contract integration (no dup master) | DONE | IContractRepository.cs ProcessAsync CT.NEW check widened to sim_customers/sim_suppliers | Contract E2E regression (96/96) |
| CUS-11 | Voucher integration | DONE (pre-existing, verified) | VoucherExtractJobRunner.cs BuildArContextAsync | Customer E2E §10 |
| CUS-12 | Agent/runtime | DEFERRED | — | no chat-based customer-creation flow requested; revisit if needed |
| CUS-13 | UI | DONE | web/customers/page.tsx, customers/[id]/page.tsx, customer-form.tsx, customer-add-panel.tsx, customer-lifecycle-actions.tsx | `next build` + `tsc --noEmit` green |
| CUS-14 | Audit/history | DONE | migration 031 customer_history table, written on create/deactivate/reactivate | Customer E2E (indirect — rows inserted, not separately asserted) |
| CUS-15 | Tests/E2E | DONE | scripts/test_customer_module_e2e.sh (new, 44 assertions) | run: 44 passed, 0 failed |
| CUS-16 | Docs | DONE | this file | — |

## Current

All matrix items implemented and verified green. Nothing left to resume.

## Next

Nothing blocking. Optional follow-up only: CUS-12 agent/runtime tools if a
chat-based "tạo khách hàng mới" flow is ever requested.

## Bugs

| Bug | Root cause | Status |
|---|---|---|
| CT.NEW proposes "open new counterparty" even when the tax code already exists as a real `sim_customers`/`sim_suppliers` row | `knownElsewhere` check in `IContractRepository.ProcessAsync` only looked at `contract_parties` history, never the actual Customer/Supplier master | FIXED |
| `POST /customers` 500'd on every call that hit the fuzzy-duplicate-name query | `DuplicateCandidate.Similarity` was `decimal`, but Postgres `similarity()` (pg_trgm) returns `real`/`float4` — Dapper's record-constructor deserializer can't bridge that type mismatch | FIXED (cast to `::numeric` in SQL) |
| `ContractProcessingIntegrationTests.cs`'s hand-rolled schema broke once ProcessAsync started querying `sim_customers`/`sim_suppliers` | Test fixture never created those tables (didn't need to, before CUS-10's fix) | FIXED (added minimal `sim_customers`/`sim_suppliers` tables to the fixture) |

## Verification

- `dotnet build` (Gateway): 0 errors.
- `dotnet test` (Gateway, full suite): 202/202 passed (after both fixture and Similarity fixes).
- `scripts/test_customer_module_e2e.sh`: 44 passed, 0 failed.
- `web`: `npx tsc --noEmit` clean, `npm run build` succeeded, `npm run test` (vitest) 6/6 passed.
- `scripts/test_contract_processing_e2e.sh`: 96 passed, 0 failed (regression check for CUS-10's CT.NEW widening).
- `scripts/test_business_module_acl_e2e.sh`: 65 passed, 0 failed (regression check for Customer endpoint changes).

## Resume

Module complete — nothing to resume. If revisiting: CUS-12 (agent/runtime
tools for customer create/search) is the only deferred item.
