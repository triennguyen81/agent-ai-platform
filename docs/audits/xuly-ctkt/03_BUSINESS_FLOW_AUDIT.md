# 03 — Business Flow Audit (end-to-end)

Mỗi luồng được truy vết theo chuỗi: Input → Validation → Processing → Approval → Accounting Recognition → Payment/Collection → Reconciliation → Reporting.

Mức bằng chứng:
- **TEST** = integration test chạy trong lần audit này trên Postgres cô lập (đủ init + 65 migration, cùng 101 bảng với live). Toàn bộ 822 test Gateway đều PASS.
- **LIVE** = không thực hiện: cấm ghi DB live và đổi mật khẩu. Mọi luồng đều cần đăng nhập và ghi dữ liệu nên **LIVE = BLOCKED**.

## Tổng quan luồng

| # | Luồng | Requirement | Điểm đầu → điểm cuối | Kết quả audit | Bằng chứng |
|---|---|---|---|---|---|
| F1 | Tiếp nhận & xử lý chứng từ chung | PR-01..06, CF-01..05 | Upload → loại + dữ liệu + cổng duyệt | Chạy được (upload); **thiếu** connector và thông báo kết quả | TEST |
| F2 | Phiếu thu tài vụ → công nợ phải thu | SC-03, AR-01..07 | Phiếu thu → ghi sổ Nợ 111x/112x Có 131 → công nợ, tiến độ HĐ | Chạy xuyên suốt (không chiết khấu) | TEST |
| F3 | Sao kê ngân hàng → công nợ phải thu | SC-02, AR-01..07 | Sao kê → từng dòng → KH → ghi sổ | Chạy xuyên suốt; thiếu "thông báo ngân hàng" đơn lẻ | TEST |
| F4 | Chiết khấu thanh toán theo HĐ | AR-03, AR-04 | Điều khoản HĐ → đề xuất → xác nhận | **BLOCKED có chủ đích** tại `payment_discount_accounting_policy_missing` | TEST |
| F5 | Đề nghị thanh toán 1 chứng từ | SC-04, EX-01..04 | Chứng từ → kết quả Luồng 03 → duyệt → ghi nhận → chi | Chạy xuyên suốt | TEST |
| F6 | Đề nghị thanh toán nhiều chứng từ | SC-04, EX-01..04 | N file → N khoản → duyệt → ghi nhận → chi | Chạy xuyên suốt | TEST |
| F7 | Hợp đồng → lịch thanh toán → tiến độ | SC-01, HD-01 | HĐ → xử lý → duyệt → lịch TT ← hoá đơn liên kết ← thu tiền | Chạy được; PDF chưa định nghĩa phạm vi | TEST |
| F8 | Thông báo bên liên quan | PR-06, AR-08, EX-04 | Kết quả → email/messenger/in-app | **MISSING** (email/messenger); in-app một phần | STATIC |
| F9 | Tiếp nhận qua folder connector | PR-02 | Folder → chứng từ | **MISSING** | STATIC |
| F10 | Đối chiếu & báo cáo (sổ cái, số dư) | (giai đoạn Reporting/Reconciliation của F2–F7) | Bút toán đã ghi → sổ cái/cân đối → đối chiếu | **MISSING** | STATIC |

- Luồng chạy xuyên suốt, đã xác minh bằng test: **6** (F1 phần upload, F2, F3, F5, F6, F7).
- BLOCKED: **1 có chủ đích** (F4) + **mọi luồng ở mức LIVE**.
- MISSING: **3** (F8, F9, F10).

---

## F1 — Tiếp nhận & xử lý chứng từ chung (trang 3–4)

| Bước | Actor | Hiện trạng | API / code | DB | Kết quả |
|---|---|---|---|---|---|
| Cấu hình loại / trường / workflow | Department ADMIN ("người quản lý Agent") | Draft → publish phiên bản; snapshot bất biến | `DepartmentAgentEndpoints.cs:29-30,61-63`; `DocumentProcessingRepository.cs` | `agent_versions`, `agent_version_document_types`, `workflow_definitions` | PASS (TEST) |
| Nhận — upload | Người dùng phòng ban | Có cho chứng từ, HĐ, đề nghị nhiều file | `VoucherEndpoints.cs:1322`; `ContractEndpoints.cs:24,26`; `ExpenseClaimEndpoints.cs:35,37` | `vouchers`, MinIO | PASS |
| Nhận — folder connector | Hệ thống | **Không có** | — | — | MISSING |
| OCR | Hangfire job | Có; OCR Agent được tuần tự hoá ở Gateway | `Jobs/VoucherOcrJobRunner.cs:37`; `Books/IAgentClient.cs` (`OcrGate`) | `vouchers.raw_ocr_text` | PASS |
| Validate scope + xác định loại | Gateway + Agent | Rule tiêu đề cố định + LLM độc lập + chính sách cấu hình (loại không bật ⇒ chặn) | `VoucherTypeClassifier.cs:190,289`; `DocumentExtraction.cs:89` | `extracted_fields.voucher_type_classification` | PARTIAL (không dựa trên "định dạng cấu hình") |
| Trích xuất | Agent (pinned version) | Manifest version hoặc legacy; provenance đầy đủ | `VoucherExtractJobRunner.cs:95-246` | `document_processing_runs`, `vouchers.processed_by_agent_*` | PASS |
| Xử lý theo workflow | Gateway | Handler cố định theo loại + cổng `require_review` | `VoucherExtractJobRunner.cs:332-526,584` | `document_review_tasks` | PARTIAL |
| Thông báo kết quả | — | Không có thông báo khi xử lý xong/lỗi; UI tự refresh; banner lỗi trích xuất | `web/src/components/extraction-failure-banner.tsx` | — | PARTIAL |

Trường hợp lỗi:
- Loại chứng từ không chắc chắn ⇒ `voucher_type_unconfirmed` chặn ghi sổ (`ICashSettlementRepository.cs:254,587`).
- Provider lỗi ⇒ trạng thái `extraction_failed` + retry.
- Ngoài scope ⇒ `unsupported_document_type`.
- Trùng (MST + số HĐ) ⇒ `rejected` (`VoucherExtractJobRunner.cs:539-548`).
- Sai tenant ⇒ not-found (`DocumentExtractionTenantIsolationTests`).

## F2 — Phiếu thu tài vụ → công nợ phải thu (trang 2 mục 2b, trang 6)

| Bước | Actor | API / code | DB | Kết quả |
|---|---|---|---|---|
| 1. Xác định KH | Gateway | `ICashSettlementRepository.cs:430-460` (MST → tên) | `sim_customers` | PASS cho KH; HĐ chỉ qua hoá đơn liên kết |
| 2. Tổng công nợ | Gateway | `LoadOpenItemsAsync`; `CustomerRepository.RecomputeCurrentArAsync` | `sim_ar_ledger` | PASS |
| 3. Chiết khấu | Gateway | `CashSettlementDiscount.cs:105` (đề xuất) | `contract_payment_discount_terms` | PARTIAL (xem F4) |
| 4a/4b. Đủ / thiếu / thừa | Gateway | `CashSettlementRules.cs:229` `SuggestAllocations`; `ICashSettlementRepository.cs:738-789` | `payment_applications` | Đủ/thiếu PASS; thừa ⇒ để "chưa phân bổ" (PDF chưa định nghĩa) |
| 5. Đề xuất bút toán → xác nhận | Department ADMIN | `CashSettlementEndpoints.cs:22-27` | `cash_transactions` (posted_by), `voucher_journal_suggestions` (posting, bất biến — `059:100-112`) | PASS |
| Ghi nhận | Gateway (giao dịch duy nhất) | `ICashSettlementRepository.cs:682` `PostCoreAsync` → `:777` → `:834` → `:884` | + `sim_bank_accounts.current_balance` khi CK | PASS |
| Thu tiền / đối chiếu | — | Tiến độ HĐ suy ra từ ghi nhận (`ContractScheduleRecompute.cs:58`) | `contract_payment_schedules.status` | PASS |
| 6. Thông báo KH | — | **Không có** | — | MISSING |
| Báo cáo | — | Chỉ có công nợ theo từng KH (`CustomerEndpoints.cs:32`); không có sổ cái | — | MISSING (F10) |

Trường hợp lỗi, có test (TEST):

| Trường hợp | Test |
|---|---|
| Nhập trùng | `Case7_ExistingPhieuThu_ProbableDuplicate_NeverAutoPosted_ReconcileLinksInstead`, `Case7b_SameBankReference_IsExactDuplicate…`, `DuplicateReceiptApply_PaidAmountUnchangedAfterSecondAttempt` |
| Retry / bấm hai lần | `Case8_ConcurrentPosts_OnePosting_NoDuplicateEffects`; unique index `uq_cash_tx_voucher_posted` |
| Huỷ | `Case10_Reverse_RestoresAr_KeepsLine_AllowsCorrectedRepost`, `Case9_Unapply_ThenReallocate_SameCustomerOnly`, `UnapplyReceipt_RestoresOpenStatusAndCurrentAr` |
| Sai đối tượng | `Case4b_ReferenceToAnotherCustomersInvoice_NeedsReview`, `Case5_UnmatchedCustomer_AndMoneyOut_AreBlocked` |
| Sai số tiền | `allocation_exceeds_outstanding/available` (`ICashSettlementRepository.cs:768-789`) |
| Sai tenant / phòng ban | `CrossDepartment_And_CrossTenant_AreNotFound_AndChangeNothing`, `Tenant_CannotReadOrMutateOtherTenantsCustomer` |
| Sai loại chứng từ | `WrongUploadType_StoredAsDetectedPayment_SettlementRenders_ButWaitsForConfirmation` |
| Cổng duyệt chứng từ | `DocumentReviewGate_BlocksSettlementPosting_UntilApproved` |

**Lỗ hổng phát hiện:**
- Một phiếu thu **đã ghi sổ** vẫn có thể bị "Trích xuất lại" hoặc `apply-extraction` ghi đè số liệu chứng từ và trạng thái (xem R-01, `04_ACCOUNTING_INTEGRITY_AUDIT.md`).
- Bút toán và công nợ không bị nhân đôi, nhưng chứng từ gốc và sổ sách có thể lệch nhau.

## F3 — Sao kê ngân hàng → công nợ (trang 2 mục 2a, trang 6)

| Bước | Code | Kết quả |
|---|---|---|
| Parse sao kê CSV/XLSX hoặc trường đã trích xuất | `BankStatementParser.cs:82,162,218` | PASS (TEST: `BankStatementParserTests` 22) |
| Import idempotent theo fingerprint | `IBankStatementRepository.cs:71` | PASS |
| Nhận diện KH: STK → MST trong diễn giải → số HĐ → mã KH → tên | `IBankStatementRepository.cs:188-250` | PASS (TEST: `CounterpartyMatcherTests`, `Case1And2…`) |
| Gắn TK ngân hàng công ty | `Case6_CompanyBankAccountNotLinked_Blocks…` | PASS |
| Ghi sổ dòng tiền vào (cùng lõi với phiếu thu) | `CashSettlementStatementSource.cs`; `BankingEndpoints.cs:73-93` | PASS |
| Chống trùng với phiếu thu đã ghi | `Case7*`, `Reconcile` | PASS |
| Cổng duyệt chứng từ của sao kê | `StatementGate_*` (5 test) | PASS |
| Thông báo ngân hàng đơn lẻ (giấy báo có) | không có loại chứng từ | MISSING phần này |

## F4 — Chiết khấu thanh toán (trang 6 mục 3–4a) — BLOCKED có chủ đích

- Có điều khoản có cấu trúc, revision bất biến (`ContractPaymentDiscountTerms.cs:90`).
- Có tính toán xác định (`PaymentDiscountCalculator.cs:106`) và đề xuất trên cả phiếu thu lẫn sao kê.
- Khi ADMIN xác nhận, hệ thống tính lại, kiểm tra stale, rồi **dừng** `payment_discount_accounting_policy_missing` (`PaymentDiscountCalculator.cs:182,193`; `CashSettlementDiscount.cs`).
- Điểm dừng là chủ đích: chưa có tài khoản hạch toán chiết khấu được kế toán phê duyệt.
- TEST: 14 unit + 31 integration (các prefix `Term…Receipt_date`).

Khác biệt so với PDF:
- PDF nói "tổng công nợ − chiết khấu"; triển khai đánh giá **từng hoá đơn** có liên kết HĐ.
- Thanh toán một phần: không có chiết khấu (`partial_payment_discount_policy_undefined`).

Cần quyết định nghiệp vụ.

## F5 — Đề nghị thanh toán 1 chứng từ (trang 7)

| Bước | Actor | Code | DB | Kết quả |
|---|---|---|---|---|
| Khai báo (upload đề nghị) | NV / USER | `VoucherEndpoints.cs:1322` | `vouchers` | PASS |
| Đọc + phân loại kết quả (APPROVED / PARTIALLY_APPROVED / RETURN_FOR_DOCUMENTS / WAITING_EXCEPTION / REJECTED) | Gateway | `IExpenseClaimRepository.cs:256` | `expense_claim_results` | PASS |
| Đối soát khai báo ↔ chứng từ | — | Chỉ cờ `receipt_attached`, trùng MST + số HĐ (`:335,343`) | — | PARTIAL |
| Kiểm tra quy trình công ty | Gateway | Định mức `department_expense_policies` (`:248,365,440`); xác minh thuế | — | PARTIAL (không có yêu cầu mua hàng / lệnh công tác) |
| Duyệt bộ phận → Duyệt kế toán (ghi nhận) | Department ADMIN (được tự duyệt theo §15) | `VoucherEndpoints.cs:136-147`; `ExpenseClaimRecognition.cs:201` | `voucher_journal_suggestions` (Có 141 / Có 334) | PASS |
| Chi tiền | Department ADMIN | `CashSettlementExpenseClaimSource.cs:216` | `cash_transactions` (334/111x, 112x) | PASS |
| Thông báo | — | Chỉ khi trả về / từ chối / chờ ngoại lệ | `notifications` | PARTIAL |
| Đảo ghi nhận | — | **Không có** | — | MISSING (đề xuất) |

Trường hợp lỗi, có test:

| Trường hợp | Test |
|---|---|
| Chi trước khi ghi nhận | `Case3_PayBeforeRecognition_Blocked` |
| Ghi nhận / chi hai lần, đồng thời | `Case4_DoubleRecognition…`, `Case5_ConcurrentRecognition_ExactlyOne`, `Case7_DoubleSubmit_OnePayment`, `Case8_ConcurrentPay_ExactlyOneSucceeds` |
| Thuế chưa xác minh | `Case6_TaxNotVerified_RecognitionBlocked`, `Case3_TaxNoLongerVerified_Blocked_NoMutation` |
| Lỗi journal ⇒ rollback | `Case10_JournalFailure…`, `Case11_PaymentJournalFailure…` |
| Sửa / trích xuất lại sau ghi nhận ⇒ bị DB chặn | `Case11_EditOrReextractAfterRecognition_IsRefused_NoStaleJournal` (trigger `059:80-94`) |
| NV phải hoàn lại tạm ứng thừa | `Case13_EmployeeRefund_NotRecognized_NothingInvented` (chặn — chưa hỗ trợ) |
| LLM nói "đã chi" / "đã ghi nhận" | `Case10_LlmSayingPaid_ChangesNothing`, `Case14_LlmSayingRecognized…` |
| Sai phòng ban / tenant | `Case8_OtherDepartment_NoMutation`, `Case6_OtherDepartmentOrTenant_NotFound_NoMutation` |

## F6 — Đề nghị thanh toán nhiều chứng từ

- Một lần upload tạo **một** đề nghị; mỗi file là **một** khoản (`ExpenseClaimEndpoints.cs`, `ExpenseClaimIntakeRepository.cs`).
- Mỗi khoản được OCR và trích xuất độc lập, có provenance (`ExpenseClaimItemJobRunner`).
- Quy tắc review (`ExpenseClaimItems.cs:78`) đưa khoản vào trạng thái cần kiểm tra khi: thiếu số tiền/ngày, khoản mục không cấu hình, ngoại tệ, độ tin cậy thấp, ngoài kỳ công tác, trùng file/hoá đơn/ngày-tiền.
- Tổng được tính ở server rồi đưa vào `ProposeAsync` hiện có.
- Cổng `expense_claim_items_unresolved` chặn duyệt bộ phận và ghi nhận.
- Ghi nhận và chi dùng engine hiện có.
- TEST: `ExpenseClaimMultiFileIntegrationTests` (17) — 1/5/20 file; lỗi cô lập; retry; ghim version; trùng; khoá sau ghi nhận; cùng một ADMIN thực hiện cả luồng.

Giới hạn:
- Tạm ứng được cấn trừ là **toàn bộ** `outstanding_advance` của NV.
- Không có tạm ứng theo chuyến.
- Không có lệnh công tác chính thức.

## F7 — Hợp đồng → lịch thanh toán → tiến độ (trang 2 mục 1, trang 5)

Chuỗi: tạo → `/process` (cảnh báo xác định: VAT lệch, tổng % ≠ 100, MST không hợp lệ, trùng số HĐ, thiếu phụ lục) → `WAITING_REVIEW` / `NEEDS_REVIEW` → ADMIN duyệt (`APPROVED`) → handoff (`IContractRepository.cs:324,489,499`).

- Lịch thanh toán và mốc được sinh ra.
- Job phạt quá hạn chỉ tính cho HĐ đã duyệt.
- Tiến độ lịch thanh toán **chỉ** cập nhật khi thu tiền thực tế trên hoá đơn có liên kết HĐ.
- HĐ **không** tạo công nợ (đúng INF-01).

TEST: `ContractProcessingIntegrationTests` (11), `CashSettlementIntegrationTests.Schedule*` (18), `ContractInstallmentPenaltyJobTests` (9).

Phát hiện bổ sung:
- `contracts.direction` lấy từ `tenants.company_tax_id`, trong khi hoá đơn dùng `tenant_company_profiles.tax_code`.
- Tenant chưa đặt `company_tax_id` thì mọi HĐ có hướng `needs_review` (quan sát read-only trên DB live: `tenants.company_tax_id` trống ở tenant Sáu Trực). Mức: LOW.
- Việc tải HĐ qua `/vouchers/upload` (loại `contract`) **không** đi vào module Hợp đồng (handler `contract_reference` không xử lý gì) ⇒ có 2 lối vào cho cùng một loại chứng từ.

## F8 / F9 / F10 — Chưa triển khai

- **F8 Thông báo:** bảng `notifications` chỉ dùng in-app (`NotificationEndpoints.cs:15-18`). Không có gửi email/SMS/messenger, không có kênh đăng ký của KH, không có thông báo cho KH.
- **F9 Connector:** không có ingest từ folder/email/drive.
- **F10 Đối chiếu & báo cáo:**
  - Không có API sổ cái, cân đối số phát sinh, số dư theo tài khoản, hay đối chiếu 131/141/331/334 với bút toán.
  - `/accounting/reports` chỉ nhờ Agent soạn mẫu báo cáo; `/accounting/journal` là công cụ soạn bút toán thủ công.
  - Số dư `current_ar` / `current_ap` được tính lại từ open items, không từ sổ cái.
