# AccAgent Business Sample Data

A complete, fictional sample-data pack for manual browser testing and E2E tests of the three business flows (Luồng 01 — Hợp đồng, Luồng 02 — Phiếu thu công nợ, Luồng 03 — Hoá đơn thanh toán). Based on `docs/business/Quy Trình Nghiệp Vụ AccAgent.pdf` and verified against the actual current schema/API by direct inspection and live testing (not assumed).

**No production IDs. No real people, companies, tax codes, or bank accounts. No user accounts are created by this pack** — every file assumes you provide a real tenant/department/user from your own onboarding flow (see below), and the pack only ever adds master/business data on top of that.

## Files

| File | Contents |
|---|---|
| `master_data.json` | 9 customers, 10 suppliers, a project reference list, 4 employees, 5 expense policies |
| `luong01_contracts.json` | 5 contract-processing cases (request bodies + expected alerts/results) |
| `luong02_receivables.json` | 9 receipt/AR-matching cases |
| `luong03_expenses.json` | 12 expense-claim cases |
| `expected_results.json` | One-line index of every case's expected outcome, for quick assertions |
| `seed.sql` | Loads all master data + minimum supporting contracts/ledger rows directly via SQL |
| `curl_examples.sh` | Boots an isolated demo stack, bootstraps a tenant, loads seed.sql, walks through one representative case per flow |

## Quick start

```bash
bash tests/fixtures/business_samples/curl_examples.sh
```

This boots its own isolated Postgres + Gateway (own Docker network, ports, and volumes — it never touches a shared dev/production stack), creates ONE fictional demo tenant ("AccAgent Demo Co."), loads `seed.sql`, and runs one full example per Luồng end-to-end so you can see real request/response shapes. It tears the whole stack down when it exits (via a shell trap) — nothing it creates persists.

To explore every case, not just the one walked through by the script: open `master_data.json` / `luong0N_*.json` and copy the request bodies against the same bootstrapped stack (or your own dev environment) using the `req()` pattern in `curl_examples.sh` as a template.

## Using `seed.sql` against your own environment

`seed.sql` never creates a tenant, department, or user — it only inserts customers, suppliers, employees, expense policies, and the minimum contract/ledger rows Luồng 02's cases need. You must supply real, already-existing IDs:

```bash
psql "$DATABASE_URL" \
  -v tenant_id="'<your-tenant-id>'" \
  -v department_id="'<your-department-id>'" \
  -v user_id="'<any-real-user-id-in-that-tenant>'" \
  -f tests/fixtures/business_samples/seed.sql
```

Get a tenant/department/user by onboarding one the normal way (`POST /system/tenants/onboard` as a super_admin — see `curl_examples.sh` §2 for the exact sequence), or by reusing one you already have in a scratch/dev database. **Never run this against a production database.**

`seed.sql` is idempotent (verified by running it twice against a real bootstrapped instance) — re-running it after the first load is safe and inserts nothing new.

## Field-name and schema fidelity

Every field name, status value, and enum used in these fixtures was taken directly from the current schema/code — not guessed:

- Customer/supplier fields: `gateway/src/Aiq.Gateway/Data/Migrations/020_simulator_business_schema.sql`, `031_customer_master_fields.sql`, `032_supplier_master_fields.sql`.
- Contract fields and alert codes: `gateway/src/Aiq.Gateway/Contracts/ContractRecords.cs`, `IContractRepository.cs`.
- Receipt-match outcomes and journal routing: `gateway/src/Aiq.Gateway/Receipts/IReceiptMatchRepository.cs`, `gateway/src/Aiq.Gateway/Accounting/JournalRoutingRules.cs`.
- Expense-claim fields, policy engine, 3-stage approval: `gateway/src/Aiq.Gateway/Employees/IExpenseClaimRepository.cs`.
- Tax-verification tri-state: `gateway/src/Aiq.Gateway/Vouchers/IVoucherRepository.cs`.

No column or API field was invented for this pack. Where the business document describes something the schema doesn't support (see "Known gaps" below), the fixture says so explicitly rather than inventing a field to paper over it.

## Two independent classification paths in Luồng 02 (important, found while verifying this pack)

`POST /vouchers/{id}/receipt-match/propose` and `POST /vouchers/{id}/apply-receipt` are **not** a sequential propose-then-confirm pair, even though they look like one. They are two independent code paths that each compute a receipt-match outcome from different inputs:

- `/receipt-match/propose` derives everything from the voucher's own `buyerMst`/`totalAmount`/`paymentMethod` fields (set at `POST /vouchers` time). It is classification-only — it never settles anything.
- `/apply-receipt` derives its own match from `extractedFields.ar_context.customer.Id` + `extractedFields.receipt_matching.{receipt_amount,applications}` (set via `PATCH /vouchers/{id}/apply-extraction`), and is the **only** path that actually settles `sim_ar_ledger`, recomputes contract `progress_percent`, and activates milestones.

Every AR-00N case in `luong02_receivables.json` demonstrates the classification via `/receipt-match/propose` (accurate and sufficient for outcome/journal-routing testing). Only AR-001 additionally demonstrates full settlement, since it's the one case that needs to show contract-progress/milestone-activation — its `applyExtractionForSettlement` field shows the exact second payload required.

## Important — avoid creating a second contract for the same customer's tax code

`seed.sql` pre-loads a contract for CUS-001 (Công ty CP Kỹ thuật Bình Minh, MST 0301234567) so Luồng 02's AR-001 case can settle against a known installment id. If you ALSO run Luồng 01's HD-001 case (which creates its own, separate contract for the SAME customer/MST via `POST /contracts`) in the same tenant/department, `IReceiptMatchRepository.FindOpenInstallmentAsync`'s tax-code-based "earliest unpaid installment" lookup becomes ambiguous between the two contracts — found live while building this pack (a demo receipt settled against the wrong contract, leaving the intended one at `progress_percent=0` instead of the expected `30`). To test AR-001's progress/milestone-activation behavior cleanly, either use `seed.sql`'s pre-loaded HD-001 only (skip creating HD-001 via the Luồng 01 API in the same tenant), or process a DIFFERENT customer's contract for your Luồng 01 walkthrough — `curl_examples.sh` demonstrates this by using HD-002 (a different customer) for its Luồng 01 step instead of re-deriving HD-001.

## Known gaps and honest discrepancies (do not paper over these)

- **FX (AR-009)**: no FX rate source is configured anywhere in this system. The fixture's expected result is `BLOCKED — no_fx_rate_source_configured`, not an invented rate. Never make this case "pass" by adding one.
- **Government tax verification (EXP-005 and any invoice-bearing claim)**: never fabricate a `verified` status. The real state values are `null` (never checked) / `verified` / `invalid` / `unavailable` — this pack only ever uses `reject` (invalid/unavailable) or leaves the field unchecked.
- **Cancelled-invoice timing (EXP-005)**: the business document's own worked example shows a cancelled invoice blocked immediately during processing. The current implementation only blocks it at final payment approval (a separate, later tax-verification gate) — propose-time duplicate/policy checks do not consult tax-portal status at all. This fixture documents the real behavior, not the document's aspirational one.
- **Hotel per-night cap (EXP-003)**: the business document's worked example implies a per-night rate multiplied by nights stayed. The current policy engine applies a flat per-item cap with no quantity/night-count field — this fixture uses the real flat-cap number, not the document's multiplied one.
- **VAT vs CIT non-deductible (EXP-009/EXP-010)**: currently driven by the exact same condition (a missing `invoice_no`) in the expense-claim flow — they cannot be triggered independently today even though they're tracked as separate numbers. Both fixture cases use the same underlying mechanic, clearly labelled.
- **"Projects" (PRJ-014 etc.)**: no `projects` table/entity exists in the schema. This is a plain reference list for tagging expense-item descriptions with a project code, matching how the business document itself uses it (free text, not a foreign key).
- **AR-008 (overdue receivable)**: has no direct HTTP trigger — it is exercised only by the scheduled `AccountsReceivableOverdueJob`/`ContractInstallmentPenaltyJob` (daily 07:00 VN in production; see `gateway/tests/Aiq.Gateway.Tests/Jobs/*JobTests.cs` for their exact, already-tested expected behavior).

See `BUSINESS_SAMPLE_TEST_MATRIX.md` (repo root) for the full case-by-case matrix.

## Verification performed while building this pack

Before finalizing, the following were run against a freshly-bootstrapped, fully isolated instance (own Docker network/volumes, torn down afterward) — not just written and assumed correct:

- `seed.sql` loaded successfully and confirmed idempotent (identical row counts on a second run).
- AR-001 (the most complex Luồng 02 case) run fully end-to-end: propose → apply-extraction → apply-receipt → verified `progressPercent=30` and milestone ordinal 2 activated with a computed `expected_date`, matching the fixture's documented expectation exactly.
- AR-005 (unknown payer) and AR-009 (foreign currency) confirmed to produce zero journal-suggestion rows, as documented.
- EXP-001 (the most complex Luồng 03 case, matching the business document's own worked example) run fully end-to-end: propose → 3-stage approval → tax-verification gate (confirmed blocking before verification, then succeeding after) → final journal proposal, all three journal entries matching the documented amounts exactly (641/141=3,000,000; 641/334=500,000; 1331/334=318,182).
- HD-002 (missing appendix) run live: `workflowState=WAITING_REVIEW`, `alertCount=1` (`MISSING_APPENDIX`), exactly as documented.
- `curl_examples.sh` itself was run end-to-end four times while debugging real issues it surfaced (see below), and its final version completes cleanly start to finish with every printed value matching the fixture's documented expectations exactly.

Four real bugs were found and fixed during this process — all caught by actually running things, not by inspection alone:
1. Two customers were originally given the same MST, violating the real `UNIQUE(tenant_id, mst)` constraint on `sim_customers`.
2. `POST /vouchers/{id}/apply-receipt` does not chain off `/receipt-match/propose` — they are independent classification paths (see the dedicated section above).
3. `/contracts` requires the `department_member` policy — a `tenant_owner` token is rejected; a `department_admin` token is required.
4. `/contracts/{id}/process` requires an agent bound to the `contract.processing` (or `contract.validate`) capability for the department — unlike `/vouchers`, an admin role alone does not bypass this. Also: creating a second contract for the same customer's tax code as an already-settled one makes the installment-matching heuristic ambiguous (see the dedicated warning above) — `curl_examples.sh` avoids this by using a different customer for its Luồng 01 walkthrough step.

Cases not re-verified live (AR-002/003/004/006/007/008, HD-001/003/004/005, EXP-002 through EXP-012) follow the exact same, already-proven code paths and account/outcome logic as the ones that were verified — see the inline `expected` blocks in each JSON file for the precise reasoning behind each number.
