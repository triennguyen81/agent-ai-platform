# 04 — Accounting Integrity Audit

PDF **không quy định** định khoản Nợ/Có nào. Mục này đánh giá tính nhất quán và an toàn của các định khoản mà hệ thống tự áp dụng, đối chiếu với TT200 (chuẩn chung), **không** coi chúng là requirement PDF.

Đường dẫn tương đối `gateway/src/Aiq.Gateway/`.

## 1. Định khoản thực tế do Gateway ghi sổ

| Nghiệp vụ | Điều kiện ghi nhận | Nợ | Có | Ghi bởi | Bằng chứng |
|---|---|---|---|---|---|
| Phiếu thu (TM / CK) | ADMIN "Duyệt & ghi nhận"; KH xác định; chiều thu; VND; loại đã xác nhận; cổng duyệt chứng từ | 1111 / 112x (TK ngân hàng thật) | 131 | `ICashSettlementRepository.cs:682,720` | TEST `CashSettlementIntegrationTests.Receipt*`, `Case*` |
| Dòng sao kê tiền vào | Như trên + dòng đã khớp KH + TK công ty đã liên kết (112x) | 112x | 131 | `CashSettlementStatementSource.cs` → `PostCoreAsync` | TEST `Statement*`, `Case6…` |
| Phiếu chi / UNC cho NCC (ngoài scope PDF) | Tương tự | 331 | 111x / 112x | cùng lõi | TEST `Payment*` |
| Ghi nhận đề nghị thanh toán | Duyệt bộ phận; kết quả APPROVED / PARTIALLY; thuế đã xác minh (nếu có HĐ GTGT); có mapping khoản mục → TK; payable ≥ 0 | 641/642/627 (từ `department_expense_policies.debit_account`) + 1331 | 141 (tạm ứng cấn trừ), 334 (phải trả NV) | `ExpenseClaimRecognition.cs:103,201,231`; router `Accounting/JournalRoutingRules.cs` `ForExpenseSettlement` | TEST `ExpenseClaimRecognitionIntegrationTests` (18) |
| Chi tiền đề nghị | Đã ghi nhận; payable > 0; thuế hợp lệ | 334 | 1111 / 112x | `CashSettlementExpenseClaimSource.cs:216` | TEST `ExpenseClaimPaymentIntegrationTests` (18) |
| Đảo phiếu thu / chi | ADMIN reverse | đảo dòng gốc (source=`reversal`) | | `ICashSettlementRepository.cs:1104` `ReverseCoreAsync` | TEST `Case10_Reverse_RestoresAr…` |
| Hoá đơn bán → công nợ phải thu | apply-invoice | **(không ghi bút toán)** | | `Customers/ICustomerRepository.cs:810` chỉ INSERT `sim_ar_ledger` | STATIC |
| Hoá đơn mua → công nợ phải trả | apply-expense (invoice_vat_in) | **(không ghi bút toán)** | | `SupplierRepository.BookInvoiceAsync` | STATIC |
| Chiết khấu thanh toán | — | **Chưa có chính sách TK** ⇒ dừng `payment_discount_accounting_policy_missing` | | `Payments/PaymentDiscountCalculator.cs:182,193` | TEST |
| Chi tạm ứng NV | — | **Không có** (cặp 141/111x, 112x chỉ nằm trong allow-list `JournalRoutingRules.cs:43`) | | — | STATIC |
| NV hoàn lại tạm ứng thừa | — | **Chặn** `employee_refund_not_supported` (`ExpenseClaimRecognition.cs:131`) | | — | TEST `Case13…` |

**Quy tắc đối ứng:**
- Bút toán do AI đề xuất cho phiếu thu / đề nghị thanh toán chỉ được lưu nếu cặp TK thuộc `JournalRoutingRules.AllowedPairs` (`JournalRoutingRules.cs:36-49`; `VoucherEndpoints.cs:1847-1853`).
- Đề xuất **không bao giờ** tự thành bút toán ghi sổ (TEST `Case9_AiSuggestedAccount_NeverPosted_TrustedMappingUsed…`).

## 2. Sổ chi tiết, sổ cái, số dư

| Thành phần | Hiện trạng | Đánh giá |
|---|---|---|
| Sổ chi tiết công nợ phải thu | `sim_ar_ledger` (dòng invoice + paid_amount + status; dòng payment ứng với `payment_applications`) | Có |
| Sổ chi tiết phải trả | `sim_ap_ledger` | Có |
| Tạm ứng theo NV | `sim_employees.outstanding_advance` (một con số, chỉ giảm khi ghi nhận; tăng chỉ qua seed / SQL) | **Không phải sổ chi tiết** |
| Tiền | `sim_bank_accounts.current_balance` (cộng / trừ khi post / reverse) | Có (cho TK ngân hàng) |
| Nhật ký (bút toán ghi sổ) | `voucher_journal_suggestions` với `source IN ('posting','reversal')`, bất biến (trigger `059_expense_claim_recognition.sql:100-112`) | Có, nhưng **không đầy đủ** (thiếu bút toán hoá đơn) |
| **Sổ cái / cân đối số phát sinh / số dư theo TK** | **Không có** API hay màn hình (Gateway chỉ có `/customers/{id}/receivables` và `/tenant/reports/weekly-summary`) | **MISSING** |
| `current_ar` | Tính lại = Σ(debit − paid_amount) hoá đơn open / partial (`ICustomerRepository.cs:859-874`) | Không cộng dồn (tốt); **không phản ánh** tiền KH trả thừa / chưa phân bổ |

## 3. Đối chiếu

- **Đối chiếu ngân hàng:** có. Dòng sao kê được đối chiếu (reconcile) với bút toán đã ghi, không ghi lần hai. Có `uq_sbt_reconciled_cash_tx` (`056:41`). TEST `Case7*`, `StatementGate_BlocksReconcile`.
- **Đối chiếu công nợ ↔ sổ cái:** **không thể**, vì không có sổ cái và hoá đơn không sinh bút toán.
- **Đối chiếu tạm ứng 141:** **không thể**, vì không có bút toán chi tạm ứng (Nợ 141) và không có sổ chi tiết 141.
- **Đối chiếu tiến độ hợp đồng:** suy ra từ ghi nhận thực tế qua hoá đơn có liên kết (`Contracts/ContractScheduleRecompute.cs:58`). TEST `Schedule*`.

## 4. Huỷ, sửa, hoàn tác

| Nghiệp vụ | Hỗ trợ | Ghi chú |
|---|---|---|
| Bỏ phân bổ phiếu thu / chi | Có (`UnapplyCoreAsync` `ICashSettlementRepository.cs:1045`) | Tiền giữ nguyên, thành chưa phân bổ |
| Đảo phiếu thu / chi | Có (`ReverseCoreAsync` `:1104`) | Bút toán đảo; số dư ngân hàng hoàn lại |
| Đảo ghi nhận / chi đề nghị thanh toán | **Không có** | `recognition_status='reversed'` có trong CHECK nhưng không có code |
| Đảo hoá đơn đã đưa vào công nợ | Không có (ngoài scope PDF) | — |
| Sửa chứng từ sau khi ghi sổ | Chặn cho đề nghị thanh toán **đã ghi nhận** (trigger `059:80-94`) và cho PATCH thủ công (`VoucherEndpoints.cs:1644`); **không chặn** cho phiếu thu / sao kê / hoá đơn đã ghi sổ khi trích xuất lại hoặc `apply-extraction` | Xem R-01 |

## 5. Chống ghi nhận hai lần

| Cơ chế | Bằng chứng |
|---|---|
| Một bút toán tiền cho mỗi chứng từ | `uq_cash_tx_voucher_posted` (`051:82`, `056:26`) |
| Một bút toán tiền cho mỗi dòng sao kê | `uq_cash_tx_bank_line_posted` (`056:28`) |
| Một bút toán chi cho mỗi đề nghị | `uq_cash_tx_expense_claim_posted` (`058:39`) |
| Một dòng công nợ cho mỗi hoá đơn | `uq_sim_ar_ledger_tenant_voucher`, `uq_sim_ap_ledger_tenant_voucher` (`045:88,90`) |
| Một phân bổ active cho mỗi (bút toán, hoá đơn) | `uq_payment_app_active_ar/ap` (`051:128,131`) |
| Khoá hàng | Thứ tự khoá voucher → đối tượng → hoá đơn; đồng thời: TEST `Case8_ConcurrentPosts…`, `Case5_ConcurrentRecognition…`, `Case8_ConcurrentPay…`, `Concurrent_confirmations…` |
| Ghi nhận đề nghị đúng một lần; NV cập nhật một lần | `ExpenseClaimRecognition.cs:223`; TEST `Case4_DoubleRecognition…`, `Case1…EmployeeUntouchedByPayment` |
| Trùng chứng từ (MST + số HĐ) | `VoucherExtractJobRunner.cs:539-548`; `ApplyExtractionAsync` 409 |
| Trùng phiếu thu với sao kê | `possible_duplicate` / `already_recorded_elsewhere` (TEST `Case7*`) |

Kết luận: **không phát hiện** đường ghi nhận hai lần trong phạm vi đã test.

## 6. Rủi ro toàn vẹn kế toán

| ID | Rủi ro | Mức | Bằng chứng | Hệ quả |
|---|---|---|---|---|
| R-01 | Chứng từ **đã ghi sổ** (phiếu thu, sao kê, hoá đơn đã đưa vào công nợ) vẫn bị **trích xuất lại** hoặc **apply-extraction** ghi đè `total_amount`, `extracted_fields` và đặt lại `status='extracted'`. Không có guard ở `ReextractAsync` (`VoucherEndpoints.cs:1230` (guard duy nhất `:1254-1264`) chỉ chặn đề nghị thanh toán đã ghi nhận), extract job (`VoucherExtractJobRunner.cs:132` chỉ bỏ qua `ocr_unavailable` / `rejected`, ghi tại `:609`), `ApplyExtractionAsync` (`VoucherEndpoints.cs:1735-1781`, gọi bởi tool Agent `save_voucher` — `agent/src/aiq_agent/agentic/voucher_tools.py:206`) và `ReplaceExtractedAsync` (`Vouchers/IVoucherRepository.cs:431-455`). DB chỉ có trigger đóng băng cho đề nghị thanh toán đã ghi nhận (đã xác minh trên DB cô lập: `vouchers` chỉ có `trg_voucher_recognized_expense_claim_frozen`, `trg_vouchers_updated_at`, `trg_vouchers_user_tenant`). | **HIGH** | STATIC + catalog DB cô lập | Chứng từ gốc lệch với bút toán / công nợ đã ghi; danh sách hiển thị "chưa ghi sổ" cho chứng từ đã ghi sổ ⇒ **mất truy vết**. Không nhân đôi bút toán (unique index). Chưa tái hiện động (không thêm test). |
| R-02 | Nhật ký không đầy đủ: hoá đơn bán / mua không sinh bút toán Nợ 131 / Có 511-3331 (và tương ứng 331) | **HIGH** | `ICustomerRepository.cs:810`; chỉ `ICashSettlementRepository.cs:720` và `ExpenseClaimRecognition.cs:231` ghi `posting` | Không thể lập sổ cái hay cân đối số phát sinh từ dữ liệu; số dư 131 / 331 theo nhật ký bị lệch |
| R-03 | `current_ar` không trừ tiền KH đã trả nhưng chưa phân bổ (dư Có 131) | MEDIUM | `ICustomerRepository.cs:859-874` vs `cash_transactions` unapplied | Công nợ hiển thị cao hơn thực tế khi KH trả thừa / trả trước |
| R-04 | Tạm ứng NV (141) không có nghiệp vụ chi; `outstanding_advance` chỉ nhập bằng seed / SQL; đề nghị nhiều chứng từ cấn trừ **toàn bộ** tạm ứng | HIGH | `Employees/IEmployeeRepository.cs:122`; `ExpenseClaimIntakeRepository.cs` | Cấn trừ 141 có thể sai đối tượng / sai chuyến; không đối chiếu được 141 |
| R-05 | Không đảo được ghi nhận / chi của đề nghị thanh toán | MEDIUM | không có code | Sai sót sau ghi nhận phải xử lý ngoài hệ thống |
| R-06 | Chiết khấu thanh toán chưa ghi sổ được (chưa có TK) | MEDIUM | `PaymentDiscountCalculator.cs:193` | Chiết khấu hợp lệ không ghi nhận được; KH bị treo công nợ phần chiết khấu |
| R-07 | Xử lý thu thừa chưa được định nghĩa (để "chưa phân bổ" trên Có 131; 3388 chỉ là cặp cho phép, không có luồng) | MEDIUM | `ICashSettlementRepository.cs:738` | Phụ thuộc quyết định nghiệp vụ |
| R-08 | ADMIN được tự lập → duyệt → ghi nhận → chi (không SoD) — **quyết định sản phẩm tường minh** (`docs/ACL_V2_RESOURCE_VISIBILITY_RULES.md` §15) | Ghi nhận (accepted risk) | `CLAUDE.md` | Kiểm soát nội bộ phụ thuộc phân quyền ADMIN; không phải lỗi |
