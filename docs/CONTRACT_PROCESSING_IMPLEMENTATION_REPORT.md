# Contract Processing — Luồng 01 Hợp đồng

Verified against current source, fresh migrations, isolated PostgreSQL/MinIO, and a real HTTP lifecycle on 2026-09-11. This records verified behavior rather than trusting the earlier report.

## Requirement matrix

| Requirement | Status | Implemented / action |
|---|---|---|
| Classify sale, purchase, rent, loan contracts; appendices; acceptance/delivery documents | DONE | Agent extraction and Gateway processing support the taxonomy; invalid types create a deterministic error alert. |
| Extract parties, tax codes, values/VAT, payments, milestones, penalties, retention, warranty, effective terms | DONE | Existing Agent skill/tool maps semantic fields into the extended Contract model and `/process`. |
| Extract open and conditional clauses | DONE | Clause type, `is_open`, and `trigger_event` persist and generate review alerts. |
| Validate duplicate contract | DONE | Contract numbers are checked within tenant + Department and protected by a scoped unique index. |
| Validate amount, VAT, percentages and dates | DONE | Gateway recalculates VAT/schedule amounts and checks tolerance, 100% payment total, positive amount, contract date order, and fixed schedule dates. |
| Validate tax code | DONE | Deterministic 10/13-digit format validation; live registry lookup is deferred below. |
| Validate referenced documents / appendices | DONE | Actual scoped attachments are checked; required missing documents produce `NEEDS_DOCUMENT`; missing appendix references produce an alert. |
| Detect template deviation / open clauses | DONE | Penalty/retention thresholds and open/conditional clauses produce deterministic review alerts. Semantic identification stays in the Agent skill. |
| Generate payment schedule | DONE | Scoped rows include percentages, subtotal, VAT, total, fixed/conditional dates, triggers and offsets. |
| Generate milestones, reminders, expected dates and alerts | DONE | Fixed and conditional milestones persist with reminders; expected dates remain planning estimates only. |
| Never promote expected date to confirmed due date | DONE | Agent normalization and Gateway defense-in-depth force `due_date = NULL` whenever a trigger exists. |
| Generate checklist | DONE | Processing replaces only generated rows, preserves manual rows, and is retry-idempotent. |
| Human review / HITL | DONE | Processing stores a Department-scoped HITL action; ADMIN review resolves that same action. |
| Counterparty proposal | DONE | New tax codes create scoped informational proposals without duplicating known counterparties. |
| Handoff includes proposal, contract code and schedules | DONE | Idempotent handoff JSON contains all three and is explicitly `EXPORT_ONLY`. |
| No posting/invoice/tax side effects | DONE | No Contract path creates vouchers, journal suggestions, invoices, postings, or tax declarations. |
| USER assigned Agent + capability | DONE | Backend resolution controls list/read/create/upload/edit/process; legacy `contract.processing` preserves compatible published versions. |
| USER create/upload/edit-own only while editable | DONE | Ownership comes from the principal; another USER is denied; finalized records and attachments are immutable. |
| ADMIN full own-Department operations | DONE | ADMIN is not constrained by creator and operates only inside its authenticated Department. |
| Cross-tenant / cross-Department isolation | DONE | Queries, child tables, endpoints and composite foreign keys enforce both scope keys. |
| Existing Contract UI sections | DONE | Detail exposes the seven requested sections, checklist and documents; review/handoff controls are ADMIN-only. |
| Voucher Add/Upload regression | DONE | Authorized USER upload behavior remains; `expense_claim` remains available. |

## Architecture reused

- Existing Contract, clause, checklist, Agent, Capability, Workflow, Tool, Knowledge and HITL components were extended; no parallel stack was created.
- `tenant_id`, `department_id`, and creator identity come only from `AuthenticatedPrincipalContext`.
- The Agent handles semantic classification/extraction. Gateway remains the deterministic and authorization authority.
- The catalog adds `contract.intake`, `contract.classify`, `contract.extract`, `contract.validate`, `contract.payment_schedule`, `contract.milestone_tracking`, `contract.risk_review`, `contract.counterparty_check`, and `contract.handoff`; no Agent ID/name is hard-coded.

## Schema

- `027_contract_processing.sql`: Department ownership/workflow/accounting fields; parties, schedules, milestones, alerts, proposals, handoffs and history; nine capabilities.
- `028_contract_conditional_dates.sql`: trigger, offset/duration and non-binding expected-date fields.
- `029_contract_documents_and_outputs.sql`: contract code, warranty, clause semantics, HITL linkage, document metadata, checklist source, scope indexes and composite child foreign keys.

## Workflow

`RECEIVED → CLASSIFIED → EXTRACTED → VALIDATED → SCHEDULE_GENERATED → WAITING_REVIEW → APPROVED → HANDED_OFF`.

Exceptions are `NEEDS_DOCUMENT`, `NEEDS_REVIEW`, and `REJECTED`. `APPROVED`, `HANDED_OFF`, and `REJECTED` are immutable for update/delete/process. Only exact-role Department ADMIN can approve, reject, or hand off.

## Endpoints and upload

- CRUD: `GET/POST /contracts`, `GET/PATCH/DELETE /contracts/{id}`
- Processing: `POST /contracts/{id}/process`
- Review/export: `POST /contracts/{id}/review`, `POST /contracts/{id}/handoff`
- Documents: `GET/POST /contracts/{id}/documents`, `DELETE /contracts/{id}/documents/{documentId}`
- Checklist: existing scoped Contract checklist endpoints

Documents reuse guarded S3/MinIO storage, PDF MIME/magic-byte/size validation, tenant + Department object keys, authenticated uploader identity, and SHA-256 idempotency.

## Outputs 1–6

1. Contract record: code, classification, parties, dates, totals/VAT, penalty, retention, warranty, clauses and workflow.
2. Payment schedule: deterministic subtotal/VAT/total rows with fixed or conditional timing.
3. Milestones: reminders, fixed dates or unresolved triggers plus non-binding expected dates.
4. Alerts/checklist: validation, deviation, missing-document, trigger and follow-up items.
5. Counterparty proposal: Department-scoped proposal for new tax codes.
6. Accounting handoff: approved, idempotent `EXPORT_ONLY` JSON containing contract code, schedules and proposals.

## UI

The existing `/contracts` and `/contracts/[id]` pages are retained. Authorized USER and ADMIN can add/upload. Detail provides Tổng quan, Thanh toán, Mốc theo dõi, Cảnh báo, Đối tác, Bàn giao, Lịch sử, documents and checklist. ADMIN receives Review/Reject/Handoff actions. Gateway repeats every authorization check.

## Verification

- Isolated real HTTP lifecycle with fresh PostgreSQL + MinIO: **96 passed, 0 failed**.
- Agent pytest: **91 passed, 0 failed**.
- Web Vitest: **6 passed, 0 failed**.
- Web TypeScript: passed.
- Web production build: passed, 91 pages.
- Gateway, Agent and Web production Docker builds: passed.
- Gateway full regression (includes 5 focused Contract integration tests): **202 passed, 0 failed** against isolated PostgreSQL with pooling disabled to avoid per-schema test-pool exhaustion.
- `git diff --check`: passed.

## Remaining gaps

- Live Vietnamese tax-registry verification is **DEFERRED** because no authoritative provider/tool is configured. Shape validation and counterparty proposals do not claim live registry confirmation.
- Semantic extraction quality depends on the configured external LLM/OCR runtime and source quality. Deterministic normalization/Gateway validation are locally covered; isolated E2E did not call an external model service.
- Direct accounting posting, invoice issuance and tax submission are intentionally prohibited scope, not gaps.

## Completion

Luồng 01 is complete for the current architecture and `EXPORT_ONLY` boundary. The external integrations above remain explicitly deferred without an unsafe fallback.
