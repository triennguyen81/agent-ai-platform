# Xử lý chứng từ kế toán — Requirement Audit

> Đối chiếu source code hiện tại với `docs/Xuly-ctkt.pdf` (7 trang).
> Báo cáo review, **không sửa code**.

| Mục | Giá trị |
|---|---|
| Ngày audit | 2026-10-05 |
| Code được audit | `master` @ `9622f8b` **cộng working tree chưa commit** (39 file sửa, 75 file mới, 4 xoá). Các phase gần đây (cash settlement, sao kê, đối soát đợt hợp đồng, xác minh thuế, chi tiền và ghi nhận chi phí đề nghị hoàn ứng) chỉ tồn tại trong working tree. |
| Runtime | Docker compose `aiq-agentic`, 12 service Up |
| Health | `GET /healthz/ready`: gateway `{"status":"ready","postgres":"ok"}`, agent `ready`; web `/login` 200; `GET /vouchers` không token → 401 |
| Migration | 60 migration đã áp; mới nhất `059_expense_claim_recognition.sql` = file mới nhất trong repo |

Quy ước trạng thái:

| Trạng thái | Ý nghĩa |
|---|---|
| `PASS` | Có implementation đúng ý PDF **và** có bằng chứng test/runtime |
| `PARTIAL` | Có một phần; phần thiếu ghi rõ |
| `MISSING` | Chưa có |
| `INCORRECT` | Có nhưng sai so với PDF |
| `NOT TESTED` | Có code, chưa kiểm chứng runtime |
| `BUSINESS REQUIREMENT NOT FULLY DEFINED` | PDF chưa định nghĩa đủ; không tự đặt luật nghiệp vụ |

Ưu tiên:
- **P0:** sai tài chính, chéo tenant, tự ghi sổ/tự duyệt, bảo mật, hỏng dữ liệu.
- **P1:** workflow cốt lõi trong PDF chưa có.
- **P2:** thiếu edge case/UI/audit/thông báo/validation.
- **P3:** cải tiến.

---

## 1. Executive Summary

### 1.1 Bóc tách requirement

Từ 7 trang PDF có **31 requirement**. Phân loại theo mức độ PDF định nghĩa:

| Loại | Số lượng | Requirement |
|---|---:|---|
| (1) Đã xác định | 22 | Phần lớn REQ-DOC, REQ-AGENT, REQ-FLOW, REQ-AR, REQ-EXP |
| (2) Chưa rõ trong PDF | 3 | REQ-DOC-001 ("cần xác định"), REQ-AR-008 ("xử lý thừa"), cách/kênh/nội dung thông báo (REQ-FLOW-006, REQ-AR-010, REQ-EXP-005, một phần) |
| (3) Cần xác nhận kế toán/nghiệp vụ | 4 | REQ-CON-001 ("cần tham khảo"), quy tắc chi tiết REQ-AR-005 (time/amount), REQ-EXP-003 (quy trình công ty), định dạng sao kê REQ-AR-003 ("cần tham khảo sao kê") |
| (4) Chỉ là đề xuất/khả năng | 2 | "Đề xuất từ Claude" (REQ-CON-001b); các ví dụ "có thể kèm kỳ công tác, yêu cầu mua hàng" (REQ-EXP-003); các ghi chú "có thể gom" (REQ-AGENT-005, REQ-FLOW-004) không thay đổi requirement |

Một requirement có thể thuộc nhiều loại; bảng trên đếm theo đặc điểm chính.

### 1.2 Kết quả

| Trạng thái | Số lượng |
|---|---:|
| **Tổng requirement** | **31** |
| PASS | 8 |
| PARTIAL | 15 |
| MISSING | 5 |
| INCORRECT | 0 |
| NOT TESTED | 0 |
| BUSINESS REQUIREMENT NOT FULLY DEFINED | 3 |

Mức đáp ứng, chỉ tính 28 requirement đánh giá được:
- **Đáp ứng đầy đủ:** 8/28 = **29%**.
- **Đáp ứng có trọng số** (PASS = 1, PARTIAL = 0.5): (8 + 7.5)/28 = **55%**.

### 1.3 Ý chính

- **Chạy end-to-end thật:** thu công nợ từ sao kê và phiếu thu nội bộ (đủ/thiếu/thừa, kế toán duyệt rồi mới ghi sổ), và chi phí nhân viên (đề xuất → 3 cấp duyệt → xác minh thuế → ghi nhận → chi tiền).
- **Chưa có:**
  - chiết khấu thanh toán từ hợp đồng;
  - thông báo ra ngoài (email/messenger);
  - connector đọc folder;
  - cấu hình loại/định dạng chứng từ.
- **Không phát hiện** Agent vượt quyền ghi sổ, duyệt hay đổi công nợ.

---

## 2. Requirement Matrix

Cột "Loại" dùng mã (1)–(4) như mục 1.1.

| ID | PDF Requirement | Loại | Implementation | Evidence | Status | Priority |
|---|---|---|---|---|---|---|
| REQ-DOC-001 | Hợp đồng — cần xác định kế toán xử lý thông tin gì trong hợp đồng (tr.2) | (2) | Module hợp đồng: upload → trích xuất LLM → lưu → review HITL → bàn giao chỉ xuất dữ liệu | `Endpoints/ContractEndpoints.cs`, `Contracts/IContractRepository.cs`, `agent/.../contract_tools.py`, migrations `023/027/028/029/042/043`; E2E `test_contract_processing_e2e.sh` 96/96 | BUSINESS REQUIREMENT NOT FULLY DEFINED | — |
| REQ-DOC-002 | Phiếu thu loại (a): thông báo/sao kê ngân hàng (tr.2) | (1) | Sao kê CSV/XLSX/PDF-OCR → dòng giao dịch → khớp KH → đối trừ AR | `Banking/BankStatementParser.cs` (`StatementLine`), `Banking/IBankStatementRepository.cs`, `Payments/CashSettlementStatementSource.cs`, migrations `054/055/056`; `CashSettlementIntegrationTests.StatementSettlement/Banking`; live 2026-10-04 | PARTIAL (chưa nhận **thông báo** NH lẻ: email/SMS biến động số dư) | P2 |
| REQ-DOC-003 | Phiếu thu loại (b): phiếu thu tài vụ, thu ngoài NH (tr.2) | (1) | Chứng từ `receipt` (tiền mặt 1111 / CK) → ADMIN duyệt & ghi sổ | `Payments/ICashSettlementRepository.cs` (`PostAsync`, `source_type='voucher'`), `Endpoints/CashSettlementEndpoints.cs`; `CashSettlementIntegrationTests.Receipt_*`; E2E receipt 117/117; live | PASS | — |
| REQ-DOC-004 | Thanh toán hoá đơn — nhân viên khai báo hoá đơn chứng từ cần thanh toán (tr.2) | (1) | Đề nghị hoàn ứng `expense_claim`; hoá đơn mua/phiếu chi → AP | `Employees/IExpenseClaimRepository.cs`, `VoucherEndpoints.ApplyExpenseAsync` | PARTIAL (khai báo = upload một chứng từ để LLM trích xuất, không có form) | P1 |
| REQ-AGENT-001 | Agent cấu hình trước giao dịch, cấu hình lại được, do người quản lý Agent (tr.3) | (1) | Agent Builder: agent/version draft→validate→publish, tool, skill (gán), knowledge, workflow; manifest resolve mỗi lần chạy | `Endpoints/DynamicAgentEndpoints.cs`, `SkillEndpoints`, `WorkflowEndpoints`, `CapabilityEndpoints`, `agent/.../runtime/manifest.py`; `DynamicAgentRepositoryIntegrationTests`, `DepartmentWorkflowSkillKnowledgeIntegrationTests` | PARTIAL (xem REQ-AGENT-002..005) | P1 |
| REQ-AGENT-002 | Cấu hình loại chứng từ (3 loại) (tr.3) | (1) | Hằng số trong code | `Vouchers/VoucherTypeClassifier.cs:25-38` | MISSING (không cấu hình được) | P1 |
| REQ-AGENT-003 | Cấu hình định dạng từng loại (tr.3) | (1) | Luật trong classifier, parser sao kê, `SKILL.md` trong image agent | `VoucherTypeClassifier.Detect`, `BankStatementParser`, `agent/skills/voucher-universal-extractor/SKILL.md` | MISSING | P1 |
| REQ-AGENT-004 | Cấu hình workflow từng loại (tr.3) | (1) | Workflow DB chỉ có step `prompt` / `require_knowledge` / `require_tool`; workflow nghiệp vụ hard-code | migration `026`, `agent/.../runtime/executors.py:156-200`, `Jobs/VoucherExtractJobRunner.cs` (switch theo loại) | PARTIAL | P1 |
| REQ-AGENT-005 | Cấu hình thông tin cần xử lý từng loại (có thể gom #3) (tr.3) | (1)+(4) | Schema trích xuất = `SKILL.md` (filesystem); skill khai báo trong DB **không** được runtime nạp | `agent/.../skills/loader.py` (`SkillRegistry` chỉ đọc filesystem), `skills.declarative_content` | PARTIAL | P1 |
| REQ-FLOW-001 | Nhận chứng từ bằng upload (tr.4) | (1) | `POST /vouchers/upload`, OCR job, UI upload; hợp đồng `POST /contracts/{id}/documents` | `VoucherEndpoints.UploadAsync`, `Jobs/VoucherOcrJobRunner.cs`, `web/.../vouchers/upload/page.tsx`; live | PASS | — |
| REQ-FLOW-002 | Nhận chứng từ đọc folder qua connector (tr.4) | (1) | Không có | grep toàn repo (không watcher/IMAP/Drive) | MISSING | P1 |
| REQ-FLOW-003 | Validate trong scope theo định dạng cấu hình ⇒ loại chứng từ (tr.4) | (1) | Luật tất định + đồng thuận LLM, confidence, chặn ghi sổ khi chưa chắc; ADMIN sửa loại | `VoucherTypeClassifier` (`BlocksPosting`), `POST /vouchers/{id}/voucher-type`; `VoucherTypeClassifierTests` + fixtures | PARTIAL (không theo cấu hình; không có trạng thái "ngoài scope/không hợp lệ") | P2 |
| REQ-FLOW-004 | Rút trích thông tin (có thể gom bước 2) (tr.4) | (1)+(4) | OCR → LLM (skill) → JSON có cấu trúc → cột `vouchers` + `extracted_fields`; parser sao kê tất định | `VoucherExtractJobRunner`, `BankStatementParser` | PARTIAL (không confidence theo trường; trích xuất lại ghi đè, không lịch sử; LLM không trả JSON thì im lặng ở `ocr_complete`) | P2 |
| REQ-FLOW-005 | Xử lý theo workflow cấu hình theo loại (tr.4) | (1) | Luồng riêng cho từng loại nhưng cố định trong code | `VoucherExtractJobRunner`, `VoucherEndpoints`, `CashSettlementEndpoints` | PARTIAL | P1 |
| REQ-FLOW-006 | Thông báo kết quả xử lý (tr.4) | (1)+(2) | Thông báo in-app cho ADMIN ở một số sự kiện | `Contracts/INotificationRepository.cs`, `NotificationEndpoints`, `ContractDeadlineJob`, `AccountsReceivableOverdueJob`, `IReceiptMatchRepository`, `IExpenseClaimRepository` | PARTIAL | P1 |
| REQ-CON-001 | Quy trình xử lý hợp đồng: tham khảo quy trình kế toán (chuyên gia / đề xuất Claude) (tr.5) | (3)+(4) | Xem mục 4 | — | BUSINESS REQUIREMENT NOT FULLY DEFINED | — |
| REQ-AR-001 | Lấy mã KH/HĐ từ STK khách hàng ⇒ mã KH, số tiền (tr.6) | (1) | Bank Account Master + khớp `bank_account_exact` (1.0) | migration `054` (`counterparty_bank_accounts`), `Banking/CounterpartyMatcher.cs`; `CounterpartyMatcherTests`, `CashSettlementIntegrationTests.Banking`; live | PASS | — |
| REQ-AR-002 | … từ tên khách hàng (tr.6) | (1) | `name_exact` 0.85 tất định; `name_similar` chỉ gợi ý; phiếu thu nội bộ: MST rồi tên (`ResolveCounterpartyAsync`) | `CounterpartyMatcher.cs`, `ICashSettlementRepository.cs:399+`; tests | PASS | — |
| REQ-AR-003 | … từ thông tin thanh toán đơn hàng (cần tham khảo sao kê) (tr.6) | (1)+(3) | Số hoá đơn trong nội dung CK (`invoice_reference` 0.95); tham chiếu tới HĐ không còn mở không bao giờ tự áp sang HĐ khác | `CashSettlementRules.ExplicitInvoiceReferences/FindReferencedDocumentNumbers`; tests | PARTIAL (mã đơn hàng/hợp đồng trong nội dung không dùng để khớp) | P2 |
| REQ-AR-004 | Lấy công nợ KH ⇒ tổng công nợ (tr.6) | (1) | `sim_ar_ledger` theo hoá đơn (`paid_amount`/`status`), `current_ar` tính lại, aging/due date, lọc tenant + phòng ban | `Customers/ICustomerRepository.cs` (`GetReceivablesAsync`, `RecomputeCurrentArAsync`), migration `045`; `ArApOpenItemIntegrationTests`; E2E receipt | PASS | — |
| REQ-AR-005 | Lấy chiết khấu thanh toán từ HĐ, validate time & amount (tr.6) | (1)+(3) | Không có. `PayinCalculator` chỉ có unit test, không được gọi; trường CK chỉ ở `sim_contracts` (bảng giả lập) | `Payin/PayinCalculator.cs`, `PayinCalculatorTests`, migration `027_contract_discount_terms`; settlement trả `discount_amount = 0` | MISSING | P1 |
| REQ-AR-006 | Thu = tổng nợ − CK ⇒ đề xuất bút toán (tr.6) | (1) | Thu đúng số nợ ⇒ gợi ý `certain` ⇒ ADMIN ghi sổ Nợ 111x/112x / Có 131 | `CashSettlementRules.SuggestAllocations`, `PostCoreAsync`; tests; live | PARTIAL (thiếu vế chiết khấu) | P1 |
| REQ-AR-007 | Thu thiếu ⇒ cập nhật công nợ (tr.6) | (1) | Phân bổ một phần, hoá đơn `partial`, công nợ còn lại tính lại; đợt hợp đồng tính lại | `ApplyOneAsync`, `ContractScheduleRecompute`; tests; live | PASS | — |
| REQ-AR-008 | Thu thừa ⇒ xử lý thừa (tr.6) | (2) | Phần thừa ghi "chưa phân bổ" (Có 131 – ứng trước), ADMIN phân bổ sau | `PlanAllocations`, cờ `LeaveUnapplied`; tests | BUSINESS REQUIREMENT NOT FULLY DEFINED | — |
| REQ-AR-009 | Đề xuất bút toán cho KTV review và xác nhận (tr.6) | (1) | Agent/Gateway chỉ đề xuất; ghi sổ do ADMIN; sửa phân bổ, unapply, đảo; gợi ý AI chỉ tham khảo; bút toán đã ghi sổ bất biến | `CashSettlementEndpoints` (`post/allocate/unapply/reverse` = `department_admin`), trigger `trg_journal_posting_immutable` (`059`); tests; live | PASS | P3 (không có reject kèm lý do) |
| REQ-AR-010 | Thông báo các bên qua email/messenger theo kênh KH đăng ký (tr.6) | (1)+(2) | Không có kênh đăng ký, không có bộ gửi | `sim_customers` chỉ có `email`/`phone` | MISSING | P1 |
| REQ-EXP-001 | Đọc khai báo thanh toán: thông tin thanh toán + chứng từ (tr.7) | (1) | Upload chứng từ đề nghị; LLM trích xuất mã NV, dòng chi phí, tạm ứng | `VoucherExtractJobRunner.cs:355+` | PARTIAL (không form; chứng từ đính kèm không phải tài liệu riêng có liên kết) | P1 |
| REQ-EXP-002 | Đối soát thông tin và chứng từ (tr.7) | (1) | Phát hiện trùng (MST + số HĐ), cờ `receipt_attached`, ADMIN đối chiếu từng HĐ với cổng thuế; Gateway so khớp dữ liệu | `IExpenseClaimRepository.ProposeAsync`, `Vouchers/TaxVerification.cs`; `TaxVerificationIntegrationTests` | PARTIAL (không tự so số liệu khai báo với hoá đơn gốc upload riêng) | P2 |
| REQ-EXP-003 | Kiểm tra hợp lệ theo quy trình công ty (kỳ công tác, yêu cầu mua hàng, thanh toán) (tr.7) | (1)+(3)+(4) | Policy tất định `department_expense_policies` (hạn mức theo khoản mục/đơn vị/chức danh, chứng từ bắt buộc, ngoại lệ); 3 cấp duyệt; xác minh thuế | migrations `034/040/044`, `IExpenseClaimRepository.ProposeAsync`; tests | PARTIAL (không có kỳ công tác / yêu cầu mua hàng) | P1 |
| REQ-EXP-004 | Đề xuất thanh toán cho KTV review và xác nhận (tr.7) | (1) | Đề xuất tất định → duyệt bộ phận → "Duyệt kế toán" (ghi nhận Nợ 64x/1331 / Có 141/334) → "Duyệt & chi tiền" (Nợ 334 / Có 111x/112x) | migrations `058/059`, `Employees/ExpenseClaimRecognition.cs`, `Payments/CashSettlementExpenseClaimSource.cs`; `ExpenseClaimRecognitionIntegrationTests`, `ExpenseClaimPaymentIntegrationTests`; live 2026-10-04 | PASS | — |
| REQ-EXP-005 | Thông báo cho các thành phần liên quan (tr.7) | (1)+(2) | In-app cho ADMIN ở một số kết quả đề xuất | `IExpenseClaimRepository.cs:178, 576` | PARTIAL | P2 |

---

## 3. Generic Document Pipeline Audit

| Stage | Requirement | Implementation | Evidence | Status |
|---|---|---|---|---|
| Intake | Upload | `POST /vouchers/upload` (PDF/ảnh/CSV/XLSX), OCR job, UI upload | `VoucherEndpoints.UploadAsync`, `VoucherOcrJobRunner` | PASS |
| Intake | Folder connector | — | — | MISSING |
| Validate | Trong scope, theo định dạng cấu hình | Luật tất định + LLM; `needs_confirmation` / `hint_mismatch` chặn ghi sổ | `VoucherTypeClassifier` | PARTIAL |
| Classification | Xác định loại | Như trên, có confidence/evidence/source; ADMIN sửa | `VoucherTypeClassifierTests` | PARTIAL (hard-code) |
| Extraction | Dữ liệu có cấu trúc | LLM theo skill → cột + JSON; sao kê parse tất định | `VoucherExtractJobRunner`, `BankStatementParser` | PARTIAL |
| Workflow | Theo loại đã cấu hình | Nhánh cố định theo `voucher_type` | Job + endpoints | PARTIAL |
| Notification | Thông báo kết quả | In-app một số sự kiện | `notifications` | PARTIAL |

Lệch so với PDF:
- **PDF yêu cầu:** validate và xác định loại dựa trên định dạng *đã cấu hình*.
- **Code hiện làm:** luật cố định trong C#; trích xuất dùng `SKILL.md` đóng gói trong image agent.
- **Impact:** người quản lý Agent không thêm/sửa được loại hay định dạng; đổi định dạng phải sửa code và deploy.
- **Cách sửa:** bảng cấu hình loại chứng từ (mã, nhãn, quy tắc nhận dạng, schema trích xuất, workflow ref) theo tenant/phòng ban, có version/publish; classifier và job đọc cấu hình; runtime agent nạp skill khai báo từ manifest.

---

## 4. Contract Workflow Audit

| Câu hỏi | Kết quả | Bằng chứng |
|---|---|---|
| Nhận/upload hợp đồng | Có | `POST /contracts`, `/contracts/{id}/documents`, `/contracts/extract-text` |
| Nhận từ folder/connector | Không | — |
| Validate / xác định loại | Có loại tài liệu hợp đồng (`contract`, `sale_contract`, `purchase_contract`, `appendix`…), chiều mua/bán xác định từ MST công ty | `ContractEndpoints.DocumentTypes`, migration `045` (`contracts.direction`) |
| Trích xuất | LLM `extract_contract_fields`; chuẩn hoá tất định (% đợt cộng 100, `due_date` vs `trigger_event`) | `contract_tools.py`, `contract_normalize.py`; `test_contract_*normalize*` |
| Trường được lưu | Số/mã HĐ, tiêu đề, bên A/B, ngày ký/hiệu lực/kết thúc, giá trị, VAT, `retention_percent`, `penalty_percent`, bảo hành; **bên** (tên, MST, địa chỉ, đại diện); **đợt thanh toán** (%, số tiền, hạn hoặc sự kiện + offset); mốc; điều khoản; checklist | bảng `contracts`, `contract_parties`, `contract_payment_schedules`, `contract_milestones` |
| Workflow sau trích xuất | Process → review HITL (`hitl_pending_actions`) → APPROVED → bàn giao **chỉ xuất dữ liệu** (`EXPORT_ONLY`); job hạn; job phạt | `ContractEndpoints` (process/review/handoff), `IContractRepository.PrepareHandoffAsync`, `ContractDeadlineJob`, `ContractInstallmentPenaltyJob` |
| Dùng cho công nợ | Hoá đơn AR gắn `contract_payment_schedule_id`; thanh toán → tính lại trạng thái đợt và tiến độ | migrations `038/045`, `Contracts/ContractScheduleRecompute.cs`; `CashSettlementIntegrationTests.Schedule_*` |
| Liên kết khách hàng | Chỉ qua MST trong `contract_parties`; **không có FK tới `sim_customers`** | schema |
| Chiết khấu thanh toán (tỷ lệ, thời hạn, điều kiện số tiền) | **Không có** trên `contracts` | schema; `PayinCalculator` không dùng |
| Hợp đồng có tạo AR/AP? | Không (đúng nguyên tắc) | E2E: "no vouchers / no journal suggestions were created by the full contract lifecycle" |

Mức độ: upload ✓, trích xuất ✓, lưu ✓, xử lý nghiệp vụ một phần, dùng cho công nợ một phần.
- Trường hiện có **đủ** cho lịch thanh toán và theo dõi đợt.
- **Không đủ** cho chiết khấu thanh toán, tức bước 3 trang 6.

Status: **BUSINESS REQUIREMENT NOT FULLY DEFINED.** Chiết khấu là gap riêng, xem REQ-AR-005.

Ghi nhận thêm (P2): `ContractInstallmentPenaltyJob` tính phạt cho mọi hợp đồng có `penalty_percent`, **không lọc trạng thái phê duyệt**. Số tiền phạt có thể tính từ dữ liệu LLM trích xuất chưa được duyệt. Đây chỉ là trường thông tin, không ghi sổ.

---

## 5. Receipt / Accounts Receivable Workflow Audit

| Bước | Hiện trạng | Bằng chứng | Status |
|---|---|---|---|
| B1 Nhận | Upload `receipt` / `bank_statement`; nguồn ghi ở `cash_transactions.source_type` (`voucher` / `bank_statement_line`); không connector | `VoucherEndpoints.UploadAsync`, migration `056` | PARTIAL |
| B2 Validate | Classifier tất định + LLM, chặn ghi sổ khi chưa chắc | `VoucherTypeClassifier` | PARTIAL (hard-code) |
| B3 Extract | Sao kê: ngày, mã GD, nội dung, tiền vào/ra, TK/tên/NH đối ứng (`StatementLine`). Phiếu thu: người nộp, MST, lý do, số tiền, số TK (`SKILL.md` §receipt) | `BankStatementParser.cs:20`, `SKILL.md:115-127`; `BankStatementParserTests` | PASS (sao kê) / PARTIAL (phiếu thu dựa LLM, không confidence trường) |
| B4 Resolve KH | Ưu tiên: STK 1.0 › MST 0.97 › số HĐ 0.95 › mã KH 0.90 › tên 0.85; tên gần giống/số tiền chỉ gợi ý. **Không khớp** → không ghi sổ được (`customer_not_matched`), ADMIN chọn. **Nhiều KH / mâu thuẫn** → `needs_review`. ADMIN xác nhận khớp và có thể lưu STK vào master | `CounterpartyMatcher.cs`, `IBankStatementRepository.ConfirmMatchAsync`; tests | PASS |
| B4 Resolve HĐ | Hợp đồng chỉ suy ra qua hoá đơn đã liên kết đợt; Luồng 02 (đợt theo MST) chỉ còn là gợi ý | `ContractScheduleRecompute`, `IReceiptMatchRepository` | PARTIAL |
| B5 Công nợ | Theo hoá đơn, tổng tính lại, aging; nhiều hoá đơn: gợi ý tất định (tham chiếu › đúng số tiền › HĐ duy nhất), ngoài ra ADMIN chọn | `SuggestAllocations`, `GetReceivablesAsync` | PASS (quy tắc FIFO tự động: chưa định nghĩa) |
| B6 Chiết khấu | Không có | xem REQ-AR-005 | MISSING |
| B7 Case 1 (đủ) | Gợi ý chắc chắn → ADMIN ghi sổ → hoá đơn PAID | tests, live | PARTIAL (không có CK) |
| B7 Case 2 (thiếu) | Phân bổ một phần, công nợ còn lại tính lại, hoá đơn PARTIAL | tests, live | PASS |
| B7 Case 3 (thừa) | Phần thừa "chưa phân bổ" trên 131 | tests | BUSINESS REQUIREMENT NOT FULLY DEFINED |
| B7 Edge | Số tiền 0/âm chặn (`amount_missing`, `CHECK amount > 0`); ngoại tệ chặn; trùng chính xác (cùng mã GD) chặn, trùng nghi vấn cần ADMIN; một bút toán sống / chứng từ / dòng (unique index); `numeric(18,2)` | migrations `051/056`; `Statement*` tests | PASS (trùng phiếu thu ↔ phiếu thu khác voucher: NOT TESTED) |
| B8 Agent chỉ đề xuất | Agent chỉ lưu gợi ý (`source='suggestion'`); không tool nào ghi sổ/duyệt; sao kê không có gợi ý AI (`055`); dòng posting bất biến (`059`) | `agent/.../voucher_tools.py`; `test_tax_verification_tools.py`, `test_expense_payment_tools.py`; `AiSuggestionSave_*` | PASS |
| B9 Human review | ADMIN (vai trò kế toán theo ACL V2) duyệt & ghi sổ, chọn KH/hoá đơn, unapply, đảo; tự duyệt chứng từ mình lập được phép (quy tắc ACL V2 đã chốt); audit `posted_by` / `applied_by` / `reversed_by`, `settlement_evidence` | `CashSettlementEndpointPolicyTests`, `BankingEndpointPolicyTests`; UI `cash-settlement-panel.tsx`, `statement-settlement-panel.tsx` | PASS (thiếu reject kèm lý do: P3) |
| B10 Notification | Không có kênh KH, không email/messenger | — | MISSING |

---

## 6. Expense Payment Workflow Audit

| Bước | Hiện trạng | Bằng chứng | Status |
|---|---|---|---|
| C1 Đọc khai báo | Upload chứng từ `expense_claim`; LLM trích xuất mã NV, dòng (khoản mục, mô tả, số tiền, VAT, MST NCC, số HĐ, ngày), tạm ứng. Không có form khai báo; trang `/expenses` là dashboard công nợ NCC | `VoucherExtractJobRunner.cs:355+`, `web/.../expenses/page.tsx` | PARTIAL |
| C2 Đối soát | Trùng trong đề nghị và với chứng từ khác; `receipt_attached`; ADMIN đối chiếu HĐ với cổng thuế, Gateway so khớp danh tính HĐ; sửa dữ liệu sau xác minh thì huỷ xác minh | `ProposeAsync`, `TaxVerification.cs` | PARTIAL |
| C3 Validate | Policy tất định trong DB (không phải prompt): hạn mức theo khoản mục/đơn vị (`per_claim`/`per_item`/`per_day`/`per_trip`…)/chức danh, chứng từ bắt buộc, ngoại lệ có duyệt. Kỳ công tác / yêu cầu mua hàng: không có | `department_expense_policies` (`034/040/044`) | PARTIAL |
| C4 Đề xuất thanh toán | Đề xuất tất định (duyệt/chặn/cắt, VAT khấu trừ, tạm ứng, phải trả); Agent không thanh toán | `ProposeAsync`; agent test không tool nào chạm route duyệt/chi | PASS |
| C5 Kế toán review | 3 cấp, mỗi cấp khác người, không phải người lập: duyệt bộ phận → duyệt kế toán (**ghi nhận chi phí tất định**) → duyệt & chi tiền. Từ chối ở 3 cấp đầu; cấp ngoại lệ. **Chỉnh sửa số liệu: không** (phải trích xuất/đề xuất lại trước khi ghi nhận; sau ghi nhận bị khoá) | `IExpenseClaimRepository`, `ExpenseClaimRecognition`, `CashSettlementExpenseClaimSource`; trigger `059` | PASS (edit: P2) |
| C6 Notification | In-app cho ADMIN ở một số kết quả; không báo nhân viên khi duyệt/chi | `IExpenseClaimRepository.cs:178, 576` | PARTIAL |

Chưa hỗ trợ (cần quyết định kế toán):
- nhân viên hoàn trả tạm ứng thừa;
- đảo ghi nhận / đảo chi;
- thanh toán một phần;
- công ty trả thẳng NCC.

---

## 7. Agent Configuration Audit

| Thành phần PDF | Nơi lưu | CONFIGURABLE / HARDCODED | Theo tenant | Version / draft / publish | Audit thay đổi | Ai sửa | Runtime nạp động? |
|---|---|---|---|---|---|---|---|
| Agent (prompt, model, giới hạn) | DB `agents`, `agent_versions` | CONFIGURABLE | Có | Có | `agent_version_audit` | SUPER_ADMIN (system), ADMIN phòng ban (ACL V2) | Có (manifest mỗi lần chạy) |
| Tool, knowledge | DB | CONFIGURABLE | Có | Theo version | Có | như trên | Có |
| 1. Loại chứng từ | `VoucherTypeClassifier.VoucherTypes` | **HARDCODED** | — | — | — | Dev | Cần deploy |
| 2. Định dạng từng loại | Luật C#, `BankStatementParser`, `SKILL.md` trong image | **HARDCODED** | — | — | — | Dev | Cần rebuild image |
| 3. Workflow từng loại | Workflow DB = bước prompt; nghiệp vụ thật trong `VoucherExtractJobRunner`/`VoucherEndpoints` | **HARDCODED** (workflow DB chỉ hướng dẫn LLM) | DB: có | DB: có | DB: có | ADMIN | Prompt: có; nghiệp vụ: deploy |
| 4. Thông tin cần xử lý | `SKILL.md` (filesystem); `skills.declarative_content` trong DB **không** được `SkillRegistry` nạp | **HARDCODED** | — | — | — | Dev | Cần rebuild |
| Định khoản chi phí theo khoản mục | DB `department_expense_policies.debit_account` | CONFIGURABLE (dữ liệu) | Tenant + phòng ban | Không | Không | **Không có API/UI** (chỉ SQL) | Có |

Kết luận: có hạ tầng Agent Builder động. Nhưng 4 nội dung PDF yêu cầu người quản lý Agent cấu hình đều **hard-code**, trừ phần prompt của workflow.

---

## 8. Business Logic vs Agentic Boundary

| Business state (phải tất định) | Hiện trạng | Đánh giá |
|---|---|---|
| Customer | Khớp tất định trên DB + ADMIN | Đúng |
| Contract | Người gắn hoá đơn ↔ đợt; dữ liệu HĐ từ LLM có review HITL | Đúng |
| Receivable / invoice / payment | `sim_ar_ledger`, `cash_transactions`, `payment_applications`; tính lại, không cộng dồn | Đúng |
| Discount rules | Chưa có (không LLM nào tạo) | — |
| Accounting rules | `CashSettlementRules`, `JournalRoutingRules`, `ExpenseRecognition`; TK chi phí từ cấu hình tin cậy | Đúng |
| Status transition / approval | Gateway + CHECK/trigger DB (`paid ⇒ recognized`, `verified ⇒ attested`, posting bất biến) | Đúng |
| Persistence | Gateway, transaction nguyên tử | Đúng |

| Agentic responsibility | Hiện trạng |
|---|---|
| Classify | LLM + luật, đồng thuận mới cho ghi sổ |
| Extract | LLM theo skill |
| Match có confidence | Tất định có confidence (không dùng LLM) |
| Phát hiện sai lệch / đề xuất / giải thích | Gợi ý bút toán (tham khảo), đề xuất xác minh thuế, chat |
| Yêu cầu người duyệt | Mọi ghi sổ đều qua ADMIN |

Kiểm tra từng nguy cơ LLM:

| Nguy cơ | Kết quả | Bằng chứng |
|---|---|---|
| Tự tạo số liệu đi thẳng vào sổ | **Không**. Số liệu trích xuất (số tiền, dòng chi phí) do LLM đọc, nhưng chỉ vào sổ qua tính toán Gateway và người duyệt. Thiếu confidence theo trường (P2) | — |
| Tự thay đổi công nợ | Không | không tool nào gọi route settlement |
| Tự ghi accounting record | Không (chỉ `suggestion`; posting bất biến) | trigger `059`, test |
| Tự approve / tự đổi trạng thái payment | Không | agent tests, policy tests |
| Bỏ qua domain layer | Không | — |
| Tự bật cổng xác minh thuế | Đã chặn: agent chỉ propose | `TaxVerificationIntegrationTests` |

**Lưu ý triển khai:** hai biện pháp chặn ở dòng cuối cùng và dòng "tự ghi" (`057`, `059`) **chưa commit**. Bản HEAD đã commit vẫn có hai lỗ hổng P0: agent bật cổng thuế; lưu gợi ý AI xoá/ghi đè bút toán đã ghi sổ.

Rủi ro bảo mật (P1): tool `fetch_url` (`agent/.../agentic/tools.py`) GET URL tuỳ ý **không có SSRF guard**, có trong allowlist runner legacy (`runner.py:44`). Kiểm tra chỉ đọc: Qdrant trả 401 vì có API key, nên chưa chứng minh được rò rỉ chéo tenant; các service nội bộ khác vẫn GET được. Mức khai thác: NOT TESTED.

---

## 9. RBAC / Tenant Isolation

### 9.1 Phân quyền

| Hành động | SUPER_ADMIN | TENANT_OWNER | ADMIN (phòng ban) | USER |
|---|---|---|---|---|
| Cấu hình agent hệ thống | Có | — | — | — |
| Cấu hình agent/skill/workflow phòng ban | — | — | Có | — |
| Upload, xem chứng từ/công nợ | Không (không phải principal phòng ban) | Không | Có | Có (cần capability `voucher.processing`) |
| Chạy Agent | theo gán | theo gán | Có | Có (gán agent) |
| Đề xuất xác minh thuế | — | — | Có | Có |
| Ghi sổ thu/chi, phân bổ, đảo | — | — | Có | Không (403) |
| Duyệt/ghi nhận/chi đề nghị hoàn ứng | — | — | Có (SoD) | Không |
| Xác minh thuế / kết luận không hợp lệ | — | — | Có | Không |
| Hàng chờ duyệt L2 (`/approval-queue`) | Có | **Có** | Có | Không |
| Nhận thông báo | in-app theo người nhận | | | |

Role `reviewer`/`viewer` tồn tại nhưng không dùng cho kế toán; `uploader` chỉ dùng ở `BookEndpoints` (P3).

### 9.2 Tenant isolation

- **Truy vấn:** endpoint nghiệp vụ dùng `BusinessModuleAuthorization.TryScope` (tenant/phòng ban lấy từ principal); repository lọc `tenant_id` + `department_id`.
  - Đã kiểm tra mẫu mọi câu `WHERE id=@…` không có tenant trong `Payments`, `Banking`, `Employees`, `Contracts`, `Customers`, `Suppliers`, `Receipts`, `Vouchers`.
  - Các câu này chỉ dùng cho bản ghi vừa đọc/khoá với điều kiện tenant trong cùng transaction, hoặc cho job nội bộ.
  - Không phát hiện IDOR. Đây là kiểm tra mẫu, không phải chứng minh hình thức.
- **Tool agent:** gọi Gateway bằng token người dùng nên cùng scope; `fetch_url` là ngoại lệ (mục 8).
- **Runtime:**
  - E2E `test_business_module_acl_e2e.sh` 65/65: tenant B không đọc được voucher/khách hàng tenant A (404); TENANT_OWNER không phải principal phòng ban (403).
  - E2E receipt: báo cáo tenant B không có dữ liệu tenant A.
  - Integration `AclV2*`, `Case6_OtherDepartmentOrTenant_*`, `OtherDepartmentOrTenant_*`.

Status: **PASS** cho các thực thể document, customer, contract, receivable, payment, expense. Hàng chờ L2 cho TENANT_OWNER duyệt là trái nguyên tắc "TENANT_OWNER không duyệt nghiệp vụ" (POTENTIAL CONFLICT, P2), dù hiện không có hiệu lực kế toán.

---

## 10. HITL / Approval

| Luồng | Trạng thái trước | Sau duyệt | Reject | Resubmit | Sửa đề xuất | Ai / khi nào | Lý do |
|---|---|---|---|---|---|---|---|
| Phiếu thu / sao kê | voucher `extracted`; dòng `NOT_POSTED` | `verified` / `POSTED`; hoá đơn `partial`/`paid` | Không có reject riêng (ADMIN không ghi sổ); đảo/unapply sau ghi sổ | Ghi lại sau đảo | Có (chọn KH, hoá đơn, số tiền phân bổ) | `posted_by`, `applied_by`, `reversed_by` + thời gian | `reversal_reason`; `settlement_evidence` |
| Đề nghị hoàn ứng | `proposed` → `dept_reviewed` → `accounting_reviewed` (+ `recognized`) | `confirmed` + `paid` / `not_due` | Có (3 cấp đầu) | Có (trích xuất/đề xuất lại khi chưa ghi nhận) | Không sửa số liệu trực tiếp | cột người/thời gian từng cấp, `recognized_by`, `paid_by` | Không có lý do reject (P2) |
| Xác minh thuế | NULL / `pending_review` | `verified` | `invalid` / `unavailable` (ADMIN + bằng chứng) | Có (huỷ khi dữ liệu đổi) | — | `voucher_tax_verification_events` | Có |
| Hợp đồng | `WAITING_REVIEW` / `NEEDS_REVIEW` | `APPROVED` → `HANDED_OFF` | Có | Có | — | `contract_history`, `hitl_pending_actions` | Có (comment) |
| Hàng chờ L2 | `sim_approval_queue` | approved | Có | — | — | Có | Có | 

**Lưu ý hàng chờ L2:** không luồng ghi sổ nào kiểm tra trạng thái này, nên nó **không chặn** gì. Gate rời rạc, P2.

---

## 11. Audit & Traceability

| Cần trace | Có? | Nơi lưu |
|---|---|---|
| document id / type / tenant / user | Có | `vouchers`, `voucher_type_classification`, `audit_log` (mọi request thay đổi trạng thái) |
| agent / agent version / config | **Không theo chứng từ** | `agent_usage_events` có `agent_version_id` nhưng không liên kết voucher |
| workflow | Không | — |
| extracted data | Một phần | `extracted_fields` (bị ghi đè khi trích xuất lại) |
| confidence | Một phần | loại chứng từ, khớp KH |
| tools invoked | Không (theo chứng từ) | — |
| matching result | Có | `match_evidence`, `settlement_evidence` |
| proposal | Có | gợi ý giữ lại kèm lý do supersede; `recognition_basis` |
| human decision | Có | cột người duyệt, `contract_history`, `voucher_tax_verification_events` |
| final status / timestamp | Có | các bảng nghiệp vụ |
| errors | Một phần | `vouchers.error_message`; lỗi LLM không trả JSON chỉ ở log |

Kết luận: truy vết được quyết định **kế toán**. Chưa truy vết được phần **AI** (agent/version/tool) theo từng chứng từ. P2.

---

## 12. Test Coverage

Quy ước:
- **E2E** = script HTTP với Postgres + Gateway tách biệt (`scripts/test_*_e2e.sh`).
- **Browser** = kiểm chứng tay bằng Playwright trên stack live (các phase trước, 2026-10-04).

| Requirement | Unit | Integration | E2E | Browser | Status |
|---|:-:|:-:|:-:|:-:|---|
| Upload chứng từ (REQ-FLOW-001) | ❌ | ❌ | ✅ (gián tiếp) | ✅ | GOOD |
| Phân loại (REQ-FLOW-003) | ✅ | ❌ | ❌ | ✅ | PARTIAL |
| Trích xuất LLM (REQ-FLOW-004) | ❌ | ❌ | ❌ | ✅ | PARTIAL (không test tự động cho job) |
| Cấu hình Agent (REQ-AGENT-001) | ✅ | ✅ | ✅ (D3–D6/V2E, không chạy lần này) | ❌ | PARTIAL |
| Hợp đồng hiện có (REQ-CON) | ✅ | ✅ | ✅ 96/96 | ❌ | GOOD |
| Sao kê → KH (REQ-AR-001/002) | ✅ | ✅ | ❌ | ✅ | GOOD |
| Tham chiếu thanh toán (REQ-AR-003) | ✅ | ✅ | ✅ | ✅ | GOOD |
| Công nợ (REQ-AR-004) | ❌ | ✅ | ✅ | ✅ | GOOD |
| Chiết khấu (REQ-AR-005) | ✅ (không dùng) | ❌ | ❌ | ❌ | MISSING |
| Đủ/thiếu/thừa (REQ-AR-006..008) | ✅ | ✅ | ✅ | ✅ | GOOD |
| Kế toán duyệt (REQ-AR-009) | ✅ | ✅ | ✅ | ✅ | GOOD |
| Thông báo (REQ-AR-010, FLOW-006, EXP-005) | ❌ | ❌ | ❌ | ❌ | MISSING |
| Đề nghị hoàn ứng (REQ-EXP-001..003) | ✅ | ✅ | ❌ | ✅ | PARTIAL |
| Đề xuất → duyệt → ghi nhận → chi (REQ-EXP-004) | ✅ | ✅ | ❌ | ✅ | GOOD |
| Tenant / RBAC | ✅ | ✅ | ✅ 65/65 | ❌ | GOOD |
| Agent không ghi sổ/duyệt | ✅ | ✅ | ❌ | ✅ | GOOD |

Verification đã chạy trong audit này (2026-10-05), không sửa test, không đổi dữ liệu live:

| Lệnh | Kết quả |
|---|---|
| Gateway `dotnet test` (15 batch) | **584/584 pass** |
| Agent `pytest tests` | **215 passed**, 8 skipped (test live) |
| Web `tsc --noEmit` / `vitest run` / `next build` | OK / **95/95** / thành công |
| `scripts/test_receipt_ar_e2e.sh` | **117/117** |
| `scripts/test_business_module_acl_e2e.sh` | **65/65** |
| `scripts/test_contract_processing_e2e.sh` | **96/96** |
| Health gateway / agent / web | ready / ready / 200 |
| Auth check `GET /vouchers` không token | 401 |

Không chạy:
- E2E D3–D6, V2D/V2E, customer, supplier (không trực tiếp thuộc PDF);
- browser test mới trong lần audit này.

---

## 13. Missing / Ambiguous Business Requirements

**PDF chưa định nghĩa (không tự đặt luật):**
1. Thông tin kế toán cần lấy từ hợp đồng (REQ-DOC-001, REQ-CON-001).
2. Quy tắc chiết khấu thanh toán:
   - loại (thanh toán sớm / thương mại / rebate);
   - điều kiện thời gian và số tiền;
   - tính trên tổng nợ hay từng hoá đơn;
   - tài khoản hạch toán.
3. "Xử lý thừa": giữ ứng trước 131 (hiện tại), 3388, hoàn tiền, hay cấn trừ? Ngưỡng tự động?
4. Thu thiếu: có dung sai, phí ngân hàng không?
5. Phân bổ một khoản thu cho nhiều hoá đơn khi không có tham chiếu (FIFO vs ADMIN chọn).
6. Thông báo: ai nhận, nội dung, kênh, thời điểm, có cần duyệt nội dung?
7. "Quy trình công ty" cho chi phí: dữ liệu kỳ công tác / yêu cầu mua hàng ở đâu, ai duyệt?
8. Định dạng thông báo/sao kê của từng ngân hàng (REQ-AR-003 "cần tham khảo").

**Implementation khác hoặc vượt PDF:**

| Hạng mục | PDF | Code hiện làm | Impact | Phân loại / đề xuất |
|---|---|---|---|---|
| Thu thừa | "xử lý thừa" (chưa định nghĩa) | Ghi "chưa phân bổ" Có 131 | Có thể khác ý kế toán | NEEDS BUSINESS CONFIRMATION |
| Phạt chậm thanh toán HĐ | Không đề cập | Job tính phạt, kể cả HĐ chưa duyệt | Số liệu tham khảo có thể sai | NEEDS CONFIRMATION; lọc HĐ đã duyệt |
| Hàng chờ L2 | Không đề cập | Enqueue nhưng không chặn; TENANT_OWNER duyệt | Người dùng tưởng có kiểm soát | POTENTIAL CONFLICT; gắn làm gate hoặc bỏ |
| Ghi nhận chi phí tại "duyệt kế toán" | Chỉ nói "đề xuất thanh toán → KTV xác nhận" | Ghi nhận Nợ 64x/1331 / Có 141/334 trước khi chi | Đúng hướng kế toán, cần xác nhận thời điểm và quy tắc VAT | VALID EXTENSION, cần xác nhận |
| 3 cấp duyệt + SoD đề nghị hoàn ứng | "KTV review và xác nhận" | 3 người khác nhau | Chặt hơn PDF | VALID EXTENSION |
| Đề nghị NV phải hoàn trả tạm ứng thừa | Không đề cập | Bị chặn ở bước duyệt kế toán | Không xử lý được loại này | NEEDS BUSINESS CONFIRMATION |
| Bank Account Master, trùng chéo nguồn | Ngầm trong "STK khách hàng" | Có | Tốt | VALID EXTENSION |

---

## 14. Recommended Implementation Order

Đây là đề xuất remediation; chưa thực hiện.

| # | Việc | Requirement | Ưu tiên | Điều kiện trước |
|---|---|---|---|---|
| 0 | **Commit / review working tree hiện tại**, đặc biệt `057` (cổng thuế) và `059` (bút toán bất biến) | — | P0 khi triển khai | Code review |
| 1 | Chặn SSRF cho `fetch_url` (dùng `security/ssrf.safe_get` như `import_excel_ledger`) hoặc gỡ khỏi allowlist | Bảo mật | P1 | — |
| 2 | Chiết khấu thanh toán: điều khoản CK trên `contracts` (trích xuất + review), luật validate thời gian/số tiền tất định, đưa vào đề xuất đối trừ và bút toán CK | REQ-AR-005/006 | P1 | Kế toán chốt mục 13.2 |
| 3 | Thông báo ra ngoài: kênh đăng ký trên khách hàng/nhân viên, outbox, email trước, messenger sau, retry/trạng thái/audit | REQ-AR-010, FLOW-006, EXP-005 | P1 | Chốt mục 13.6 |
| 4 | Cấu hình loại chứng từ, định dạng, schema trích xuất, workflow theo tenant/phòng ban (version/publish); runtime agent nạp skill khai báo trong DB | REQ-AGENT-002..005, FLOW-003/005 | P1 | Thiết kế |
| 5 | Connector folder (sau đó email/Drive), đi vào cùng pipeline upload | REQ-FLOW-002 | P1 | — |
| 6 | Khai báo thanh toán có cấu trúc + đính kèm chứng từ riêng + đối soát khai báo ↔ hoá đơn; thực thể kỳ công tác / yêu cầu mua hàng | REQ-EXP-001..003, DOC-004 | P1 | Chốt mục 13.7 |
| 7 | Trích xuất: trạng thái ngoài scope, confidence theo trường, lịch sử giá trị, báo lỗi LLM rõ; liên kết chứng từ ↔ agent/version/tool | FLOW-003/004, audit | P2 | — |
| 8 | Khớp theo mã đơn hàng / hợp đồng trong nội dung CK; nhận thông báo NH lẻ | REQ-AR-003, DOC-002 | P2 | Mẫu ngân hàng |
| 9 | HITL L2: gắn làm gate hoặc bỏ; lý do reject cho đối trừ và đề nghị hoàn ứng; job phạt chỉ HĐ đã duyệt; API/UI cấu hình định khoản chi phí | — | P2 | — |
| 10 | Dọn role không dùng, container E2E cũ | — | P3 | — |

---

## 15. Final Verdict

**1. Project đáp ứng bao nhiêu % yêu cầu PDF?**

Trên 28 requirement đánh giá được (3 requirement PDF chưa định nghĩa đủ):
- **29%** đáp ứng đầy đủ (8 PASS);
- **55%** nếu tính PARTIAL bằng nửa.

**2. Phần thực sự chạy end-to-end** (có test + E2E/live):
- Thu công nợ từ **sao kê ngân hàng** và **phiếu thu nội bộ**: nhận → phân loại → trích xuất → khớp khách hàng → công nợ theo hoá đơn → thu đủ/thiếu/thừa → kế toán duyệt → ghi sổ tất định → cập nhật đợt hợp đồng.
- **Chi phí nhân viên:** đề xuất → 3 cấp duyệt → xác minh thuế → ghi nhận chi phí → chi tiền.
- **Hợp đồng:** upload → trích xuất → review → bàn giao dữ liệu.

**3. Có UI/API/schema nhưng chưa hoàn chỉnh business flow:**
- Workflow DB (chỉ là bước prompt).
- Skill khai báo trong DB (runtime không nạp).
- `PayinCalculator` và bảng `sim_contracts` có trường chiết khấu (không dùng).
- Hàng chờ duyệt L2 (không chặn gì).
- Thông báo in-app (không ra ngoài).
- Khai báo chi phí (chỉ upload).

**4. Hoàn toàn chưa implement:**
- chiết khấu thanh toán từ hợp đồng;
- thông báo email/messenger theo kênh đăng ký;
- connector folder;
- cấu hình loại chứng từ;
- cấu hình định dạng chứng từ.

**5. PDF chưa định nghĩa đủ:**
- thông tin kế toán từ hợp đồng / quy trình hợp đồng;
- "xử lý thừa";
- quy tắc chi tiết chiết khấu, quy trình công ty cho chi phí, nội dung/kênh thông báo, định dạng sao kê (mục 13).

**6. Có logic đi sai so với PDF không?**
Không có requirement nào INCORRECT. Có lệch cấu trúc: loại/định dạng/workflow hard-code thay vì cấu hình (mục 3, mục 7). Các điểm cần xác nhận: hàng chờ L2, job phạt, xử lý thừa (mục 13).

**7. Agent có vượt quyền business/accounting không?**
Không, trong working tree hiện tại:
- Agent chỉ trích xuất/đề xuất; mọi ghi sổ, duyệt, chi tiền, xác minh thuế đều do ADMIN qua Gateway;
- bút toán đã ghi sổ bất biến.

Hai lưu ý:
- **Bản HEAD đã commit** vẫn cho agent bật cổng thuế và cho lưu gợi ý AI xoá/ghi đè bút toán.
- `fetch_url` thiếu SSRF guard.

**8. Năm việc quan trọng nhất tiếp theo:**
1. Review và commit working tree (đưa các chặn P0 vào nhánh chính), chặn SSRF `fetch_url`.
2. Chốt nghiệp vụ, rồi implement **chiết khấu thanh toán** theo hợp đồng.
3. **Thông báo** ra ngoài theo kênh khách hàng/nhân viên đăng ký.
4. **Cấu hình loại chứng từ, định dạng, schema, workflow** cho người quản lý Agent (kèm runtime nạp skill từ DB).
5. **Connector folder** và **khai báo chi phí có cấu trúc** + đối soát với chứng từ, kỳ công tác, yêu cầu mua hàng.
