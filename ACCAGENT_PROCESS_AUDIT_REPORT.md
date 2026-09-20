# AccAgent Process Audit — PDF vs. Implementation

Source of truth: `docs/business/Quy Trình Nghiệp Vụ AccAgent.pdf` (v0.1 — bản thảo, 12 trang). Cross-checked against the actual Gateway/Agent/Web codebase. Review only — no code, DB, migration, or test was modified.

Standards applied per the audit brief: test PASS ≠ nghiệp vụ hoàn chỉnh; backend có ≠ UI có; menu/UI tồn tại ≠ workflow đã implement. Every status below is backed by a code citation or explicitly marked NOT VERIFIED.

## Executive Summary

The three core processing pipelines (contract classification/extraction/scheduling, receipt-to-invoice matching with 8 deterministic exception outcomes, and expense-claim policy/VAT-CIT/3-stage approval) are implemented, tested, and match the PDF closely at the mechanical level — this is the same ground the prior `BUSINESS_FLOW_GAP_ANALYSIS.md`/`BUSINESS_FIX_REPORT.md` remediation already covered and it remains correct (verified unchanged: `git diff --stat` shows zero code changes since that pass).

This PDF-driven audit surfaces a **second layer of gaps that the prior report's requirement matrix (R01–R22) never framed**, because the prior source document (`AccAgent_Business_Process_v0.1.md`) didn't contain the same level of worked-example detail this PDF does. The new findings cluster into three themes:

1. **Cross-flow business logic implied by the PDF's own worked examples is missing**: a contract has no "tiến độ %" field at all, and a milestone's `trigger_event` is never auto-resolved when the event it's waiting on (e.g., a payment) actually happens. Late-payment interest (`lãi phạt`) is never computed even though `penalty_percent` is captured. Section: Luồng 01/02.
2. **The PDF's "closed allow-list" of situational journal entries is not enforced anywhere in code** — the only journal-suggestion tool call is a generic, TT200-general-knowledge LLM prompt that doesn't even receive the classified outcome (OVERPAYMENT/ADVANCE_PAYMENT/FX) as input, and has no validation that a proposed entry actually appears in the PDF's table. This is the single highest-risk finding in this audit.
3. **Backend-computed detail is not reaching the UI** on both Luồng 01 (contract MST/active-status/penalty%/retention%/progress%) and Luồng 03 (per-line-item Đạt/Cắt/Chặn breakdown) — the data exists in the API response or database but the page never renders it.

A genuine positive: the platform's non-negotiable principle — **Agent never writes to the books** — remains rigorously enforced (re-verified independently in this pass, not just cited).

## 1. Nguyên tắc chung — Agent không ghi sổ

| Requirement | Status | Evidence |
|---|---|---|
| Agent chỉ extract/validate/match/calculate/recommend | PASS | `agent/src/aiq_agent/agentic/voucher_tools.py` exposes exactly 6 tools (`extract_voucher_fields`, `save_voucher`, `verify_einvoice_tct`, `confirm_tax_verified`, `suggest_journal_entry`, `save_journal_suggestions`); none call any ledger-mutating endpoint. |
| Human review/approve/post accounting | PASS | All ledger-writing routes (`apply-receipt`, `apply-expense`, `apply-invoice`, `grant-expense-exception`, `department-review`, `accounting-review`) require `RequireAuthorization("department_admin")` — `gateway/src/Aiq.Gateway/Endpoints/VoucherEndpoints.cs:72,79,83,111,117,118`. |
| Agent không tự ghi sổ | PASS | `save_journal_suggestions` writes only to `voucher_journal_suggestions` (a proposal table) via `POST /{id}/journal-suggestions` — `VoucherEndpoints.cs:48,1306`; a human must separately call `DecideJournalAsync` (`department_admin`-gated, `VoucherEndpoints.cs:49`) to act on it. Repo-wide grep for the 6 ledger-endpoint path fragments inside `agent/src/` returns zero hits. |
| Agent's own privilege is bounded by the calling human's | PASS | `build_voucher_tools(*, token, gateway_base)` binds every tool call to the caller's own JWT (`voucher_tools.py`, per-request factory) — the Agent has no identity beyond whichever human's session invoked it. |

## 2. Luồng 01 — Hợp đồng

| Requirement | Flow | Backend | DB | UI | Test | Status | Evidence | Gap |
|---|---|---|---|---|---|---|---|---|
| Nhận & phân loại | 01 | Yes | Yes | Yes | E2E 96/96 | PASS | `IContractRepository.cs` classify/extract/validate pipeline | — |
| Đọc điều khoản (extract) | 01 | Yes | Yes | Yes (Điều khoản section) | E2E | PASS | `IContractRepository.cs:311` clauses | — |
| Kiểm tra (MST format, trùng hợp đồng, lệch mẫu) | 01 | Yes | Yes | Yes (Cảnh báo section) | E2E | PASS | `IContractRepository.cs:348,391-406` | MST check is format-regex only, no live active-status lookup (see R05 below) |
| Sinh lịch thu tiền (payment schedule) | 01 | Yes | Yes | Yes | E2E | PASS | `contract_payment_schedules`, migration `027` | — |
| Kế toán công nợ soát (human review) | 01 | Yes | Yes | Yes (`ContractReviewActions`) | E2E | PASS | `contracts/[id]/page.tsx:152` | Approval is a flat admin action — the PDF's named-role escalation ("Kế toán trưởng nếu giá trị vượt ngưỡng") is not modeled (see §6 approval matrix) |
| Bàn giao (handoff) | 01 | Yes | Yes | Yes, with explicit no-journal note | E2E | PASS | `contracts/[id]/page.tsx:255` "không tạo bút toán, hóa đơn hay tờ khai" | — |
| **Kết quả 1 — Bản ghi hợp đồng** | 01 | Yes (full) | Yes (`penalty_percent`, `retention_percent`, `subtotal`, `vat_amount` all captured — `IContractRepository.cs:74-75`) | **PARTIAL** | E2E asserts backend fields only | **PARTIAL** | Page header only renders title/`partyA`/`partyB` (plain strings)/dates/`totalValue`/`workflowState` — `contracts/[id]/page.tsx:140-193`. The `parties` array (carries `taxCode`) is never even destructured from the API response (`page.tsx:119` omits it), so it's not a "forgot to style it" gap, it's fully unused. `subtotal`/`vatRate`/`vatAmount` are in the TS type (`page.tsx:25-27`) but never rendered. `retentionPercent`/`penaltyPercent`/progress% aren't in the TS type at all. | MST + counterparty active-status, giá trị trước thuế/VAT breakdown, phạt chậm %, tiền giữ lại %, and "tiến độ %" are computed/stored server-side but invisible to the user. |
| **Kết quả 2 — Lịch thu tiền** | 01 | Yes | Yes | **FULL** | E2E | PASS | `page.tsx:251` renders ordinal/trigger/%/amount/date/status | — |
| **Kết quả 3 — Mốc theo dõi** | 01 | Yes | Yes | **FULL** | E2E | PASS | `page.tsx:252` | — |
| **Kết quả 4 — Cảnh báo** | 01 | Yes | Yes | **FULL**, severity-styled | E2E | PASS | `page.tsx:253` | — |
| **Kết quả 5 — Đề xuất mở mã đối tượng** | 01 | Yes | Yes | **FULL** | E2E | PASS | `page.tsx:254` | — |
| **Kết quả 6 — Gói bàn giao** | 01 | Yes | Yes | **FULL** | E2E | PASS | `page.tsx:255` | — |
| Ký hợp đồng không sinh bút toán | 01 | Yes | Yes | Yes | E2E: contract-processing §6 explicitly asserts zero vouchers/journal-suggestions from the full lifecycle | PASS | Fresh re-run this session: 96/96 | — |
| Cập nhật tiến độ hợp đồng + kích hoạt mốc kế tiếp khi thanh toán (PDF worked example, p.4: "tiến độ hợp đồng lên 30%... kích hoạt theo dõi mốc M2") | 01↔02 | **No** | **No** column exists | **No** | NOT VERIFIED (no test targets this because it doesn't exist) | **MISSING** | Repo-wide grep for `progress`/`tien_do` on `contracts`/`contract_milestones` schema and repository: zero hits. `trigger_event` on `contract_milestones`/`contract_payment_schedules` is only ever stored/displayed (`IContractRepository.cs:91,313,439,444`), never resolved by any code path when a prior event completes. | The per-installment `status` DOES flip to `paid`/`partial` on receipt confirmation (`IReceiptMatchRepository.cs:331-334` — this part is real and correct), but the contract-level progress percentage the PDF's own example shows, and the cascading activation of the next trigger-based milestone, do not exist anywhere. |
| Kế toán trưởng ngưỡng cao hơn cho hợp đồng vượt giá trị / lệch mẫu chuẩn (approval matrix, p.9) | 01 | **No** | **No** | **No** | NOT VERIFIED | **MISSING** | Only `super_admin`/`tenant_owner`/`admin`/`user` roles exist (`Data/Migrations/001,017`) — no "Kế toán công nợ" vs "Kế toán trưởng" vs "Pháp chế" vs "Ban giám đốc" distinction, no value-based escalation. | Contract review is a single flat `department_admin` action regardless of contract value or clause-deviation severity. |

## 3. Luồng 02 — Phiếu thu công nợ

| Requirement | Flow | Backend | DB | UI | Test | Status | Evidence | Gap |
|---|---|---|---|---|---|---|---|---|
| Đọc giao dịch / nhận diện khách (MST) | 02 | Yes | Yes | Yes | E2E 90/90 | PASS | `IReceiptMatchRepository.cs:161-172` deterministic MST lookup, confidence=1.0 exact match | — |
| Ghép công nợ (match invoice + installment) | 02 | Yes | Yes | Yes | E2E | PASS | `IReceiptMatchRepository.cs:174-219` | — |
| 8 tình huống (full/partial/over/advance/unknown/unmatched/reversed/overdue) + FX | 02 | Yes (8 outcomes) | Yes | Yes (outcome badge) | E2E §2, all 8 asserted | PASS | `IReceiptMatchRepository.cs:145-219`, pure deterministic branching, no LLM | Determinism independently re-verified this session |
| Kế toán xác nhận (human review) | 02 | Yes | Yes | Yes | E2E | PASS | `apply-receipt` `department_admin`-gated + self-approval blocked, `VoucherEndpoints.cs:72,191` | — |
| Bút toán khi thành công (Nợ1121/Có1311) | 02 | **PARTIAL** | Yes (`voucher_journal_suggestions`) | Yes (journal suggestions section exists on voucher page) | NOT VERIFIED (no test asserts the *specific account pair* proposed) | **PARTIAL/MISSING** | See dedicated finding below — journal suggestion is a separate, outcome-blind LLM call. | Real risk: a human trusting the tool's suggestion may post the wrong account for OVERPAYMENT/ADVANCE_PAYMENT/FX cases. |
| Linkage customer/contract/installment/invoice | 02 | Yes | Yes | Yes | E2E §10, 7/7 assertions | PASS | `contract_id`/`contract_installment_id` on `vouchers`, migration `038`; `LinkContractAsync` tenant/department-scoped (`IVoucherRepository.cs:794-818`) | — |
| Thu bằng ngoại tệ — chênh lệch tỷ giá 515/635 | 02 | **No** | No rate concept anywhere | Outcome shown, no rate/gain-loss | E2E only asserts the `FOREIGN_CURRENCY` outcome label | **BLOCKED (refined)** | `IReceiptMatchRepository.cs:145-149` classifies and stops; zero `exchange_rate`/`FxRate` concept in the Gateway outside an unrelated static Supplier/PO field (`ISupplierRepository.cs:122`, defaults to 1, never fetched live). | **Important nuance the PDF itself supplies (p.6): "Áp tỷ giá theo quy định NỘI BỘ"** — the business requirement is an internally-declared policy rate, not a live market feed. The prior audit's R09 "BLOCKED — no real rate source, forbidden to fabricate" framing is correct only for a *live market* rate; an admin-configured internal rate (same pattern as `ExpenseRiskOptions`/`ArOverdueOptions`) would NOT be fabrication and is buildable without any external dependency. Recommend reclassifying this specific PDF requirement as **MISSING (buildable now)**, distinct from a hypothetical live-FX-API integration which remains genuinely blocked. |
| Quá hạn không thu được — leo thang + tính lãi phạt | 02 | **PARTIAL** | Yes (`penalty_percent` on `contracts`) | Notification only, no amount | xunit `AccountsReceivableOverdueJobTests` (203/203, re-verified this session) | **PARTIAL** | `AccountsReceivableOverdueJob.cs:96-116` computes only a notification `kind`/title from days-overdue; it never reads `contracts.penalty_percent` or computes a penalty amount. | The escalation-ladder *notification* mechanism (R07/R08) is genuinely done and tested; the actual **lãi phạt amount** the PDF explicitly asks the ladder to produce is never calculated, despite the data (`penalty_percent`) already existing. |
| Giao dịch bị hoàn / không nhận diện được người nộp | 02 | Yes | Yes | Yes | E2E §2, §8 dedup assertions | PASS | `IReceiptMatchRepository.cs:152,157,276-296` event-driven notification, deduplicated | — |
| Bù trừ công nợ hai chiều (331/131, cần biên bản ký 2 bên) | 02 | **No** | **No** | **No** | NOT VERIFIED | **BLOCKED, confirmed** | Repo-wide grep for `offset_document`/`bien_ban_bu_tru`/`ar_ap_offset` returns zero hits (only an unrelated `offset_days` date field on contract milestones). | No signed-bilateral-document type exists to trigger this per the PDF's own explicit precondition — correctly left unbuilt rather than auto-derived. |
| Notification recipients per PDF table (Kế toán công nợ đầu ngày/ngay, Kế toán doanh thu, Kế toán trưởng, Ban giám đốc hàng tuần) | 02 | **PARTIAL** | Yes (`notifications` table, generic) | Yes (`/notifications` page) | Partial E2E (department-admin fan-out only) | **PARTIAL** | `NotifyDepartmentAdminsAsync` (`INotificationRepository.cs:154-173`) fans out to every `admin` in a department — it cannot target the PDF's specific named recipients (Kế toán doanh thu vs Kế toán thuế vs Ban giám đốc) since those roles don't exist (see §6). Weekly digest to Ban giám đốc (dòng tiền, tuổi nợ) is not implemented as a scheduled report at all. | |

## 4. Luồng 03 — Hoá đơn thanh toán

| Requirement | Flow | Backend | DB | UI | Test | Status | Evidence | Gap |
|---|---|---|---|---|---|---|---|---|
| Tách chứng từ (split multi-invoice photo) | 03 | **No** | N/A | **No** | NOT VERIFIED | **MISSING** | `UploadAsync` takes a single `IFormFile` → exactly one voucher (`VoucherEndpoints.cs:971-1010`); zero split/batch logic anywhere in Gateway or Agent. | A user must already have pre-split their photos before upload; the PDF's step 1 assumes the system does this. |
| Đọc số liệu (extract) | 03 | Yes | Yes | Yes | E2E 120/120 | PASS | `voucher-universal-extractor` skill, generic-per-type prompt | Extraction routing itself has zero automated test coverage (see §8) |
| Xác thực cổng thuế (as a **gate**, not just a capability) | 03 | **PARTIAL** | Yes (`tct_verified`) | Yes (badge) | E2E §9 (persistence only) | **PARTIAL** | `verify_einvoice_tct`/`confirm_tax_verified` exist and persist correctly (`IVoucherRepository.cs:820-828`) — this is the real, previously-fixed R05 gap. But `tct_verified` is **never read** by `IExpenseClaimRepository.ProposeAsync`/`ConfirmAsync` or anywhere in the approval chain (`grep` for `TctVerified`/`tct_verified` in `IExpenseClaimRepository.cs`: zero hits). | A claim can be fully approved and paid whether or not its invoice was ever checked against the tax portal, or even if the check came back negative — internal-control gate #1 ("Hoá đơn đã huỷ hoặc thay thế bị chặn") is not enforced as a block anywhere. |
| Chống trùng (duplicate) | 03 | Yes | Yes | Yes | E2E, prior remediation | PASS | Cross-voucher + in-claim duplicate detection, `IExpenseClaimRepository.cs` | — |
| Đối chiếu chính sách (định mức theo cấp bậc) | 03 | **PARTIAL** | **PARTIAL** | Yes (shows cap result) | E2E §14 | **PARTIAL** | `department_expense_policies` (migration `034:8-23`) is scoped by `tenant+department+category` only — no employee-rank/level column. | The PDF's own worked example ("Định mức 1,5tr/đêm cấp chuyên viên", p.6) implies rank-differentiated limits (e.g., a Trưởng phòng gets a higher hotel cap than a Chuyên viên) — the current schema cannot express two different limits for the same category based on the claimant's rank. |
| Phân bổ & đối chiếu tạm ứng | 03 | Yes | Yes | Yes | E2E | PASS | Signed `payable_amount`, prior remediation | — |
| Ba chặng duyệt: Bộ phận → Kế toán → Duyệt chi | 03 | Yes | Yes | Yes | E2E §14/§15, 14 dedicated sequence/actor assertions | PASS | `IExpenseClaimRepository.cs:532-603`, migration `037` | — |
| Không tự duyệt (mọi chặng) | 03 | Yes | Yes | Yes | E2E §13/§15 | PASS | `IsSelfApproval` at every stage endpoint, `VoucherEndpoints.cs:473,494,730` | — |
| VAT-deductible / CIT-deductible tách riêng | 03 | Yes | Yes | Yes | E2E §14, independently hand-recomputed | PASS | `vat_amount`/`vat_deductible_amount`/`non_deductible_amount`, migration `036`; proration logic `IExpenseClaimRepository.cs:336-358` | — |
| Bút toán quyết toán (641/642/627/1331 → 141/334/3388) | 03 | **PARTIAL** | Yes | Yes (suggestions section exists) | NOT VERIFIED (no test asserts the specific account pair) | **PARTIAL/MISSING** | Same root cause as Luồng 02 — see dedicated finding below. | |
| **Kết quả — Bảng kê quyết toán chi phí, per-line Đạt/Cắt/Không được trừ/Chặn** | 03 | Yes (`duplicateFlagsJson`, `policyFlagsJson` computed and stored) | Yes | **PARTIAL** | E2E asserts aggregate totals only | **PARTIAL** | `vouchers/[id]/page.tsx:206-217` declares `duplicateFlagsJson`/`policyFlagsJson`/`resolvedVendorsJson` in its TS type but never parses/renders them — only aggregate `blockedAmount`/`cappedAmount`/`missingDocuments` are shown (`page.tsx:1741-1752`). | The user sees "Bị cắt: 600,000" as a lump sum, not *which specific line* was cut, by how much, and why — the PDF's worked example (p.6-7) is explicitly a 5-line itemized table. |

### Finding: Journal-entry proposal ignores the PDF's closed situational allow-list (Luồng 02 + 03, **P1**)

The PDF is explicit and absolute: *"Bảng này là danh mục đầy đủ những bút toán mà agent được phép đề xuất. Ngoài danh mục này, agent không tự sinh bút toán nào khác"* (p.8) — a closed allow-list keyed to the **specific classified situation** (FULL_MATCH, OVERPAYMENT→3388, ADVANCE_PAYMENT→3387, FX→515/635, quyết toán chi phí→641/642/627/1331/141/334/3388, etc.).

In the actual implementation, the only journal-suggestion mechanism is the Agent's `suggest_journal_entry` tool (`agent/src/aiq_agent/agentic/voucher_tools.py:334-384`):
- It fetches only `GET /vouchers/{id}` — **not** the receipt-match or expense-claim result, so the LLM is never told which of the 8/3 situational outcomes was actually classified.
- Its hardcoded "Quy tắc chung" is 5 generic lines (`voucher_tools.py:370-378`) that don't reference 3387/3388, 515/635, 331 offset, or any of Luồng 03's 141/334/3388 accounts at all, and even the phần "Phiếu thu: Nợ 111 / Có 131" line doesn't distinguish 1111 (cash) vs 1121 (bank), unlike the PDF's own table.
- The invoked skills (`journal-entry-formatter`, `accounting-vietnam`) contain only general TT200 chart-of-accounts knowledge (checked directly: `grep` for `3387`/`3388`/`OVERPAYMENT` in both skill files returns zero hits) — not the PDF's specific situational table.
- No code anywhere validates a proposed `(debit_account, credit_account)` pair against the PDF's allow-list before it reaches `SaveJournalAsync`.

**Impact**: the closed-allow-list principle — arguably the single most load-bearing control in the whole document, since it's what lets a "kế toán viên" trust and quick-approve an AI suggestion — is not enforced in code at all. This is distinct from (and more serious than) the already-known R18 notification gaps, because it's about the correctness of the actual accounting entry a human is likely to rubber-stamp.

## 5. Shared capabilities — duplicate check

| Capability | Status | Evidence |
|---|---|---|
| Voucher OCR/extraction skill routing (voucher-receipt/bank-transfer/bank-statement vs voucher-universal-extractor) | PASS, fixed | Confirmed still fixed and unchanged: exactly `voucher-invoice-vat` + `voucher-universal-extractor` exist; `_SKILL_BY_TYPE`/`_DEFAULT_SKILL` route correctly (`voucher_tools.py:35-57`). **Zero automated test coverage on either side** (both E2E scripts run the Gateway with `Agent__BaseUrl=http://agent.invalid:8000`, so the extraction job path is never actually exercised; no xunit test references `VoucherExtractJobRunner`). Still PARTIAL from a regression-safety standpoint, unchanged since the last verification pass. |
| Customer/supplier matching | PASS, single implementation | `FindByMstAsync` used consistently by both `IReceiptMatchRepository` and the AP-side supplier resolution path; no second matching engine found. |
| Invoice/VAT extraction | PASS, consolidated | One skill (`voucher-universal-extractor`) covers all non-legacy types; `voucher-invoice-vat` kept only for the bare legacy `invoice_vat` type. |
| TT200 journal-entry proposal | **DUPLICATE-adjacent, unresolved** | `accounting-vietnam` and `accounting-vn-style` both keyword-activate on overlapping terms (kế toán/định khoản/hạch toán/VAS/TT200) — `agent/skills/accounting-vietnam/SKILL.md` vs `agent/skills/accounting-vn-style/SKILL.md`. `accounting-vietnam`'s own description claims to cover "mọi yêu cầu kế toán doanh nghiệp — trừ... style thuần tuý", suggesting `accounting-vn-style` may be intentionally narrower (terminology/style only), but this was never evaluated in either the original gap analysis or this PDF (which doesn't name either skill) — flagged, not resolved. Unchanged since the last pass. |

## 6. Data model / security

| Requirement | Status | Evidence |
|---|---|---|
| Tenant isolation | PASS | Every ledger-consequence method's `WHERE` filters `tenant_id=@tenantId AND department_id=@departmentId`, sourced server-side via `BusinessModuleAuthorization.TryScope` from the JWT — re-verified this session across `IExpenseClaimRepository`, `IReceiptMatchRepository`, `IVoucherRepository`. |
| FK/domain relationships | PASS | `contract_id`/`contract_installment_id` FKs on `vouchers` (migration `038`); `dept_reviewed_by`/`accounting_reviewed_by` FKs to `users` (migration `037`). |
| Idempotency | PASS | Notification dedup via real partial unique index `uq_notifications_tenant_dedupe` (migration `015:31-33`), backing `ON CONFLICT ... DO NOTHING`; upsert-guard pattern (`WHERE review_status <> 'confirmed'`) on both receipt and expense-claim propose paths. |
| Audit | **PARTIAL** | `AuditMiddleware.cs:40-54` logs `actorId`/`action` (METHOD+path)/`resourceType`/`resourceId`/`statusCode` for every state-changing request — this satisfies "ai sửa" (who) and roughly "khi nào" (when, via row timestamp) but **not** "sửa gì" (what specifically changed): the request body / before-after field diff is never captured. Control #8 in the PDF ("mọi lần sửa đề xuất của agent đều ghi ai sửa, sửa gì, khi nào") is therefore only half-satisfied. |
| Separation of duties | PASS | `IsSelfApproval` at every approval-stage endpoint + distinct-actor checks per stage (`IExpenseClaimRepository.cs:559,583-587`). |
| Source-document traceability | PASS | `voucher_journal_suggestions` keyed by `voucher_id`; every voucher has an object-storage-backed source file (`IObjectStorage`, `VoucherEndpoints.cs:1010+`). Control #6 satisfied. |

## 7. Notifications / internal controls

| Control gate (PDF) | Status | Evidence |
|---|---|---|
| #1 Hoá đơn phải tồn tại/còn hiệu lực — chặn trước khi cho đi tiếp | **MISSING as enforced gate** | See Luồng 03 finding above — `tct_verified` is never read by the approval pipeline. |
| #2 Bên mua đúng doanh nghiệp (so tên/địa chỉ/MST) | NOT VERIFIED | No code found comparing extracted buyer identity fields against a legal-entity master record beyond the MST-format regex; no negative finding either — needs a dedicated look at extraction validation, out of this pass's budget. |
| #3 Chi từ 20 triệu không dùng tiền mặt | **CONFLICT** | PDF (p.11) states the threshold as **20,000,000₫**. Implementation default is `ExpenseRiskOptions.CashPaymentVatDeductibleThreshold = 30_000_000m` (`gateway/src/Aiq.Gateway/Jobs/ExpenseRiskOptions.cs:12`), justified in-code as "TT78 điều 6... mức luật hiện hành" (current law level). Whichever figure is legally correct today is outside this audit's scope to adjudicate, but the shipped default does not match the audited business document — needs an explicit business/legal sign-off on which number governs, then either update the PDF or the default. |
| #4 Không ghi nhận trùng chứng từ | PASS | Duplicate check cross-references claimed/booked vouchers before proposing, `IExpenseClaimRepository.cs`. |
| #5 Chi phí không hoá đơn tách riêng, gắn cờ ngay | PASS | `non_deductible_amount` flagged at proposal time, not deferred to year-end — `IExpenseClaimRepository.cs:347-352`. |
| #6 Mọi bút toán truy được về chứng từ gốc | PASS | See §6 above. |
| #7 Người duyệt và người thực hiện tách bạch | PASS | See §6 above. |
| #8 Lưu vết mọi hiệu chỉnh | **PARTIAL** | See §6 above — actor+route logged, field-level diff not. |
| Notification digest principle ("thông báo chỉ để biết thì gom vào bản tổng hợp cuối ngày, không gửi rời", p.10) | **MISSING** | Every event-driven notification (`NotifyDepartmentAdminsAsync`, `NotifyCreatorAsync`) inserts an individual row immediately; no end-of-day/informational-vs-actionable batching logic found anywhere in `INotificationRepository.cs` or any job. |
| Ban giám đốc weekly cash-flow/aging report | **MISSING** | No scheduled job or report endpoint producing "dòng tiền thu, tuổi nợ, nhóm khách quá hạn, chi phí theo bộ phận" on a weekly cadence was found. |

## 8. Test coverage map

| Requirement | Test | Coverage |
|---|---|---|
| Self-approval blocking | `test_receipt_ar_e2e.sh` §8, `test_expense_payment_e2e.sh` §13 | Direct |
| 3-stage sequence/actor enforcement | `test_expense_payment_e2e.sh` §15 (14 assertions) | Direct |
| VAT/CIT split + export | `test_expense_payment_e2e.sh` §14 | Direct, independently hand-verified |
| 8 receipt outcomes | `test_receipt_ar_e2e.sh` §2 | Direct |
| AR overdue escalation ladder | `AccountsReceivableOverdueJobTests` (xunit) | Direct |
| Contract lifecycle + no-journal proof | `test_contract_processing_e2e.sh` §6 | Direct |
| **Extraction skill routing** | none | **PARTIAL** — code-review only, both sides |
| **Journal-entry account selection per situation** | none | **MISSING** — no test asserts a specific account pair for any outcome |
| **Contract progress%/milestone activation** | none | **N/A** — feature doesn't exist |
| **Tax-verification-as-gate** | none | **N/A** — not implemented as a gate |
| **Per-line expense-claim UI breakdown** | none | **N/A** — not rendered |
| **Cash-threshold value (20M vs 30M)** | none | **N/A** — conflict, not a test gap |

Per the audit brief's rule ("code có nhưng không có regression test thì PARTIAL"): extraction routing and the general audit-log mechanism are marked PARTIAL specifically for this reason even though their existing code is correct.

## 9. Blockers — re-confirmed this session

| ID | Status | Evidence |
|---|---|---|
| R09 — FX rate | **BLOCKED (live rate) / MISSING (internal policy rate)** — see refined finding in §3 | Zero rate-source code anywhere in Gateway; PDF's actual requirement ("quy định nội bộ") is a lower, buildable bar than a live market feed. |
| R05 — Contract-side tax-status provider | **BLOCKED, confirmed** | `IContractRepository.cs:348` — regex format check only, zero `HttpClient`/external call in that file. |
| R19 — Field-level provenance | **BLOCKED, confirmed** | Repo-wide grep for `provenance`/`source_field`/`field_source`/`extracted_by`: zero hits. |
| R10 — AR/AP bilateral offset | **BLOCKED, confirmed** | Repo-wide grep for `offset_document`/`bien_ban_bu_tru`/`ar_ap_offset`: zero hits; PDF itself requires a signed bilateral document as precondition, which has no corresponding upload/document type in the data model. |

None of the 4 were turned into DONE. No new implementation was added for any of them during this review-only pass.

## Readiness

**Luồng 01: 82%** — mechanical pipeline (classify→extract→validate→schedule→review→handoff) and 5/6 "kết quả" are solid and tested; held back by the missing progress%/milestone-activation cross-flow logic and incomplete Kết quả-1 UI.

**Luồng 02: 78%** — classification is excellent (8/8 deterministic outcomes, thoroughly tested) but the journal-entry-per-outcome gap, FX rate application, and late-payment-penalty calculation are real, business-visible holes beyond pure classification.

**Luồng 03: 80%** — the highest-value, most rigorously tested part of the system (policy/duplicate/VAT-CIT/3-stage approval), held back by the missing document-split step, the tax-verification-not-a-gate issue, and per-line UI detail loss.

**UI parity**: Luồng 01 — PARTIAL (Kết quả 1 header incomplete). Luồng 02 — PARTIAL (outcome/amount/status shown; match-confidence/evidence line and journal-account-per-outcome not surfaced). Luồng 03 — PARTIAL (per-line Đạt/Cắt/Chặn breakdown computed but not rendered).

**P0: 0 | P1: 2 | P2: 8 | P3: 1**

P1 (2): journal-entry proposal not tied to classified outcome / no allow-list enforcement (Luồng 02+03); tax-portal verification not enforced as a mandatory gate (Luồng 03).
P2 (8): contract progress%/milestone auto-activation missing; late-payment penalty never computed; document-splitting missing; Luồng 01 Kết quả-1 UI incomplete; Luồng 03 per-line UI incomplete; rank-based expense policy missing; role-granular/value-escalating approval matrix missing; notification digest + weekly BGĐ report missing.
P3 (1): audit log lacks field-level before/after diff.
(Cash-threshold 20M-vs-30M is tracked separately as a CONFLICT requiring a business decision, not sized as P0-P3.)

**Production readiness: CONDITIONAL** — the core three flows are functionally sound, tested, and safe from the platform's non-negotiable standpoint (Agent never posts to books, self-approval blocked, tenant isolation, VAT/CIT correctness). Conditional or the journal-entry allow-list and tax-verification-gate findings (both P1, both carrying real risk of an incorrect entry being posted or an invalid invoice being paid) should be resolved, or explicitly risk-accepted by the business owner, before this is called fully READY.

## Recommended next actions

1. **[P1]** Feed the classified receipt-match/expense-claim outcome into `suggest_journal_entry`'s prompt context, and encode the PDF's actual situational allow-list (not the current 5-line generic summary) into the invoked skill or the tool itself; consider a code-side validation that rejects any proposed account pair outside that list.
2. **[P1]** Decide whether tax-portal verification should be a hard gate before `apply-expense`/`apply-invoice` can proceed (per control gate #1), and if so wire `tct_verified` into `IExpenseClaimRepository`'s approval checks.
3. **[P2]** Add a `progress_percent` (or computed-on-read equivalent) to contracts, and resolve `trigger_event`-based milestones/installments when their triggering event actually fires.
4. **[P2]** Surface `parties[].taxCode`, `subtotal`/`vatAmount`, `penaltyPercent`/`retentionPercent`, and progress on the contract detail page; parse and render `duplicateFlagsJson`/`policyFlagsJson` per-line on the expense-claim voucher page.
5. **[P2]** Resolve the 20M-vs-30M cash-threshold conflict with the business/legal owner and update whichever source is stale.
6. Lower priority: rank-based expense policy dimension, notification digest batching, weekly Ban giám đốc report, document-splitting for bundled photo uploads, field-level audit diff.
