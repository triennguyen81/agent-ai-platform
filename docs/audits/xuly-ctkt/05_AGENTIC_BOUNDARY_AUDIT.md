# 05 — Agentic Boundary Audit

PDF giao cho "Agent" các việc sau:
- được cấu hình (tr.3);
- nhận, validate, xác định loại, rút trích, xử lý chứng từ (tr.4);
- đề xuất bút toán / đề xuất thanh toán để **kế toán viên review và xác nhận** (tr.6 mục 5, tr.7 mục 3);
- đề xuất quy trình HĐ — "đề xuất từ claude" (tr.5).

Nguyên tắc đối chiếu: Agent **đề xuất**; **Gateway** quyết định và ghi sổ; con người xác nhận.

## 1. Agent phụ trách

| Hạng mục | Hiện trạng | Bằng chứng |
|---|---|---|
| Agent | "Trợ lý kế toán" (department agent) với phiên bản đã publish; cấu hình document-processing theo version, chạy ghim version (pinned) | `gateway/.../DepartmentAgentEndpoints.cs:29-30,61-63`; `Vouchers/DocumentProcessing/DocumentProcessingRepository.cs` (`ResolveForUploaderAsync`, `ResolvePinnedAsync`); TEST `RuntimeConfigurationIntegrationTests` |
| Skill liên quan PDF | `voucher-universal-extractor`, `journal-entry-formatter`, `accounting-vietnam`, `accounting-vn-style`, `contract-extractor`, `contract-checklist-builder` | `agent/skills/*/SKILL.md` |
| Tool đọc | `list_customers`, `get_customer_receivables`, `list_suppliers`, `get_supplier_payables`, `get_ap_summary`, `list_vouchers`, `get_weekly_summary_report` | `agent/src/aiq_agent/agentic/report_tools.py:11-17` |
| Tool tính / tra cứu | `calculate_vat/pit/cit`, `lookup_account`, `check_filing_deadline`, `journal_entry_validator`, `import_excel_ledger` | `agentic/accounting_tools.py:252-693` |
| Tool ghi (giới hạn) | `save_voucher` (POST `/vouchers` hoặc PATCH `apply-extraction`), `save_journal_suggestions` (gợi ý), `propose_tax_verification` / `reject_tax_verified` (đề xuất), `verify_einvoice_tct`, `save_contract` / `save_checklist_items` | `agentic/voucher_tools.py:206-209,337,366,481`; `agentic/contract_tools.py:294,343,431,477` |
| Knowledge | RAG (knowledge base phòng ban / hệ thống), `search_notes` | `agent/src/aiq_agent/runtime/executors.py:126`; TEST `KnowledgeBaseIntegrationTests` |
| OCR / document extraction | Agent `/ocr/pdf`, `/ocr/image`, `/agent/document-extract` (tool-less, ghim version) — Gateway điều phối | `Jobs/VoucherOcrJobRunner.cs:37`; `Jobs/VoucherExtractJobRunner.cs:95`; `Jobs/ExpenseClaimItemJobRunner.cs` |

## 2. Phân định trách nhiệm (Agent vs Gateway)

| Nhiệm vụ PDF | Agent làm | Gateway làm | Con người | Đúng ranh giới? |
|---|---|---|---|---|
| Xác định loại chứng từ (tr.4 #2) | Phân loại độc lập (LLM) | Rule xác định (`VoucherTypeClassifier.cs:190`) + kết hợp (`:289`); chính sách cấu hình (`DocumentExtraction.cs:89`) | ADMIN xác nhận khi không chắc | Đúng |
| Rút trích (tr.4 #3) | Trích xuất JSON theo schema cấu hình | Validate schema, lưu provenance, độ tin cậy | — | Đúng |
| Nhận diện KH (tr.6 #1) | — | Xác định (`IBankStatementRepository.cs:188-250`, `ICashSettlementRepository.cs:430-460`) | ADMIN chọn khi mơ hồ | Đúng |
| Công nợ / chiết khấu (tr.6 #2–4) | Không có thẩm quyền | Tính xác định (`PaymentDiscountCalculator.cs:106`) | ADMIN xác nhận | Đúng |
| Đề xuất bút toán (tr.6 #5) | Gợi ý (`save_journal_suggestions`, chặn cặp TK không cho phép) | Bút toán ghi sổ do Gateway tạo theo rule tin cậy (`JournalRoutingRules.cs`) | ADMIN "Duyệt & ghi nhận" | Đúng |
| Kiểm tra hợp lệ đề nghị thanh toán (tr.7 #2) | Trích xuất từng bill | Định mức, trùng, thuế, kỳ công tác (`IExpenseClaimRepository.cs:256`, `ExpenseClaimItems.cs:78`) | ADMIN xác nhận khoản cần kiểm tra | Đúng |
| Đề xuất thanh toán (tr.7 #3) | — | Ghi nhận / chi (`ExpenseClaimRecognition.cs:201`, `CashSettlementExpenseClaimSource.cs:216`) | ADMIN duyệt 3 bước | Đúng |
| Đề xuất quy trình HĐ (tr.5 "đề xuất từ claude") | Trích xuất HĐ, checklist | Validate xác định (`IContractRepository.cs:324`) | ADMIN duyệt / từ chối | Đúng (quy trình chưa được PDF chốt) |
| Xác minh thuế | `propose_tax_verification` / `reject_tax_verified` = **đề xuất** | Chỉ ADMIN `confirm` (`VoucherEndpoints.cs:170`, `department_admin`) | ADMIN | Đúng |

## 3. Bằng chứng "LLM không có thẩm quyền" (TEST, chạy trong audit)

| Kiểm tra | Test |
|---|---|
| AI nói "đã chi" ⇒ không đổi gì | `ExpenseClaimPaymentIntegrationTests.Case10_LlmSayingPaid_ChangesNothing` |
| AI nói "đã ghi nhận" ⇒ không có bút toán | `ExpenseClaimRecognitionIntegrationTests.Case14_LlmSayingRecognized_NoAuthoritativeJournal` |
| TK do AI gợi ý không bao giờ được ghi sổ | `Case9_AiSuggestedAccount_NeverPosted_TrustedMappingUsed_SuggestionPreserved` |
| Tỷ lệ chiết khấu / TK do trích xuất không có hiệu lực | `CashSettlementIntegrationTests.Extracted_or_injected_discount_values_have_no_authority` |
| `tax_verified` / `gl_account` trong trích xuất bị bỏ qua | `ExpenseClaimMultiFileIntegrationTests.Extracted_tax_or_account_claims_have_no_authority` |
| Danh mục do AI đề xuất không quyết định TK | `ExpenseClaimItemRulesTests.Category_must_be_a_configured_one…` |
| Job không chạy với actor giả mạo | `DocumentGateHardeningIntegrationTests.Cross_tenant_or_unknown_actor_in_the_job_payload_is_refused` |

## 4. Độ tin cậy & Human-in-the-loop

- **Độ tin cậy phân loại:** `VoucherTypeClassification` (source / confidence / status). Khi không chắc ⇒ `voucher_type_unconfirmed` chặn ghi sổ (`ICashSettlementRepository.cs:254,587`).
- **Độ tin cậy trích xuất từng khoản chi:** < 0.80 hoặc OCR < 0.60 ⇒ `needs_review` (`ExpenseClaimItems.cs`).
- **Cổng duyệt theo workflow:** bước `require_review` tạo task bền vững (`DocumentReviewRepository.cs:69`), và `DocumentAccountingGate` chặn mọi mutation kế toán.
- **HITL cho tool có rủi ro:** `hitl` (`Governance/IHitlRepository.cs`); tool có `risk_tier` / `min_action_tier` (migration `048`).
- **Quyền thực thi:** USER chỉ dùng Agent được assign (capability resolution). Mọi quyết định kế toán đều dùng policy `department_admin`. TEST `AdminDepartmentAuthorityInvariantTests` (13), `CashSettlementEndpointPolicyTests` (7), `TaxVerificationEndpointPolicyTests` (6).

## 5. Phát hiện về ranh giới

| ID | Phát hiện | Mức | Bằng chứng |
|---|---|---|---|
| AG-01 | Tool `save_voucher` (PATCH `/vouchers/{id}/apply-extraction`, chạy bằng token người dùng) có thể ghi đè dữ liệu chứng từ **đã ghi sổ** (phiếu thu, sao kê, hoá đơn đã đưa vào công nợ) và đặt lại trạng thái — không có guard trạng thái ⇒ Agent **có thể** thay đổi bằng chứng của nghiệp vụ đã ghi nhận. Không ghi được bút toán, nhưng phá truy vết. Cùng gốc với R-01. | HIGH | `agent/src/aiq_agent/agentic/voucher_tools.py:206`; `gateway/.../VoucherEndpoints.cs:1735-1781`; `Vouchers/IVoucherRepository.cs:431-455` |
| AG-02 | OCR native của Agent không an toàn khi gọi đồng thời (process abort `realloc(): invalid old size`). Hiện đã giảm thiểu ở Gateway bằng tuần tự hoá OCR; gốc lỗi vẫn ở Agent. | MEDIUM | `gateway/.../Books/IAgentClient.cs` (`OcrGate`); log Agent đã quan sát trước audit (không tái hiện trong audit) |
| AG-03 | Agent không có công cụ / quyền gửi thông báo cho KH hay NV (PDF tr.6 #6, tr.7 #4) | MEDIUM | không có tool / endpoint |
| AG-04 | Báo cáo tài chính `/accounting/reports` do Agent soạn mẫu, không dựa trên dữ liệu ghi sổ ⇒ dễ bị hiểu nhầm là báo cáo thật | MEDIUM | `web/src/app/(app)/accounting/reports/page.tsx:44-60` |
| AG-05 | Nhận diện loại chứng từ không dùng "định dạng đã cấu hình" như PDF mô tả (rule tiêu đề cố định + LLM) | LOW | `VoucherTypeClassifier.cs:155-190` |

Không phát hiện trường hợp Agent tự ghi sổ, tự chi tiền, tự xác minh thuế hay tự chọn tài khoản hạch toán.
