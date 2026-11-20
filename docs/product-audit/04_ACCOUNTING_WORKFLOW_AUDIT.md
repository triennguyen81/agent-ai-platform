# 04 — Audit quy trình kế toán

Phạm vi: chứng từ, bút toán, công nợ phải thu/phải trả, ngân hàng, đề nghị thanh toán, kỳ kế toán, thuế. Thuế chi tiết: [docs/audit/TAX_WORKFLOW_AUDIT_REPORT.md](../audit/TAX_WORKFLOW_AUDIT_REPORT.md) (ngày 2026-11-13, cùng mã nguồn HEAD — vẫn đúng sau khi bỏ Phase 0).

## 1. Kết luận

AIQ có **lõi kiểm soát kế toán tốt hơn mức thường thấy ở sản phẩm AI**:
- Agent không có công cụ nào ghi sổ, duyệt hay chi tiền.
- Mọi bút toán ghi sổ đi qua route `department_admin`.
- Posting không sửa được (trigger DB) và chỉ đảo được bằng bút toán ngược.
- Kỳ kế toán khoá được.
- Xác thực hoá đơn bắt buộc ADMIN có bằng chứng.

Điểm yếu nằm ở **độ phủ nghiệp vụ** (không có sổ cái tổng hợp, không có báo cáo tài chính từ sổ, thuế sai tham số) và ở **một hàng đợi duyệt legacy** sai phạm vi quyền.

## 2. Luồng nghiệp vụ thực tế (từ mã nguồn)

```
Tải chứng từ (AD/US) ─► OCR (Agent /ocr/pdf) ─► Trích xuất trường (Agent, LLM theo cấu hình loại chứng từ)
   │                                                 │
   │                                       Job Gateway: phân loại, dò trùng, enrichment AR/AP, cờ rủi ro
   ▼                                                 ▼
Validator xác định (MST, cộng tiền, thuế suất theo ngày, ngày tương lai, kỳ khoá) ── lỗi ⇒ chặn chuyển trạng thái (tenant v2)
   ▼
Đề xuất bút toán (Agent/US, chỉ cặp TK cho phép) ─► USER nộp (submit) ─► ADMIN duyệt / trả lại / từ chối
   ▼
Ghi nhận nghiệp vụ (ADMIN): apply-invoice (AR) · apply-expense (AP) · receipt-match → apply-receipt · settlement/post
   ▼
Posting bất biến (cash_transactions + journal source='posting') ── đảo: settlement/reverse (có lý do)
```

## 3. Đánh giá theo yêu cầu C (Accounting Safety)

| Yêu cầu | Kết quả | Bằng chứng |
|---|---|---|
| AI không tự ghi sổ | **PASS** | Tool Agent chỉ gọi `POST /vouchers`, `PATCH /vouchers/{id}`, `/apply-extraction`, `/journal-suggestions`, `/tax-verification/propose` ([voucher_tools.py](../../agent/src/aiq_agent/agentic/voucher_tools.py)); không tool nào gọi `apply-*`, `settlement/post`, `approve`, `pay`. Test: `test_no_agent_tool_reaches_expense_approval_or_payment` ([test_expense_payment_tools.py:12](../../agent/tests/test_expense_payment_tools.py#L12)) |
| Không ghi nhận bút toán khi chưa duyệt hợp lệ | **PASS** | `settlement/post`, `apply-invoice/expense/receipt`, `approve`, `expense-claim/accounting-review`, `pay` đều `department_admin` ([evidence/gateway-routes.tsv](evidence/gateway-routes.tsv)) |
| Kiểm soát nghiệp vụ tài chính | **PARTIAL** | Có: posting bất biến (`VoucherPostingGuardIntegrationTests`), khoá kỳ (`AccountingPeriodTests`), danh mục cặp TK cho phép (`JournalRoutingRules.cs:36-49`), không ghi đè chứng từ đã ghi sổ (`VoucherEndpoints.cs:1840-1842, 1941-1942`). Thiếu: hàng đợi L2 legacy cho TO duyệt và không giới hạn phòng ban |
| LLM không quyết định ghi dữ liệu tài chính trực tiếp vào DB | **PARTIAL** | LLM ghi được: dữ liệu trích xuất chứng từ (`save_voucher`, `apply-extraction`) và **đề xuất** bút toán — đều chưa phải sổ sách. Khi ADMIN chat, Agent chạy bằng token ADMIN: an toàn hiện tại **chỉ dựa vào việc không có tool**, Gateway chưa có lớp chặn riêng cho token do Agent dùng ở các route ghi sổ |

## 4. Đánh giá theo yêu cầu A (Business Logic)

| Câu hỏi | Kết quả | Ghi chú |
|---|---|---|
| Luồng đầy đủ? | PARTIAL | Đủ từ chứng từ → ghi nhận công nợ → thu/chi tiền → posting. Thiếu: sổ cái tổng hợp, bảng CĐPS, BCTC từ sổ, kết chuyển cuối kỳ, VAT theo kỳ, tờ khai |
| Bỏ qua bước? | PASS có điều kiện | Tenant **không** bật v2: validator chỉ cảnh báo, không chặn; không có vòng đời submit/approve |
| Kiểm tra đầu vào | PASS | `VoucherValidator` (44 test), giới hạn file 25 MB, content-type; đề nghị thanh toán theo chính sách chi |
| Trùng lặp | **PARTIAL** | Hoá đơn: khoá `seller_mst + document_number` không chuẩn hoá, thiếu ký hiệu/năm, **tự từ chối** (TAX-H02). Đề nghị thanh toán: phát hiện trùng hoá đơn trong claim (`ExpenseClaimItems.cs:93`). Phân bổ thu tiền: chặn một hoá đơn xuất hiện 2 lần (`ICashSettlementRepository.cs:757`) |
| Rollback | PASS | Mỗi thao tác ghi sổ trong 1 transaction; đảo bút toán giữ lịch sử (`settlement/reverse`, `unapply`); posting không xoá được |

## 5. Theo phân hệ

| Phân hệ | Trạng thái | Điểm mạnh | Thiếu sót chính |
|---|---|---|---|
| Chứng từ | PASS/PARTIAL | Validator, vòng đời v2, xác thực HĐ có bằng chứng + vô hiệu khi dữ liệu đổi | Dò trùng sai khoá; không đọc XML HĐĐT; không có quan hệ HĐ gốc ↔ điều chỉnh/thay thế |
| Bút toán | PASS (đề xuất) / MISSING (sổ cái) | Danh mục cặp TK cho phép; posting bất biến | Không có sổ cái, sổ chi tiết theo TK, CĐPS |
| Phải thu (AR) | PASS | Khớp phiếu thu đủ/thiếu/thừa/trả trước; thu từ sao kê; chiết khấu; nhắc nợ | Ngoại tệ bị chặn (thiếu nguồn tỷ giá) — đúng thiết kế an toàn |
| Phải trả (AP) | PARTIAL | Ghi nhận HĐ mua, công nợ/tuổi nợ NCC, cờ rủi ro | Thanh toán NCC từ sao kê (money_out) chưa hoàn thiện; ngưỡng tiền mặt 30tr sai |
| Ngân hàng | PASS | Đọc sao kê CSV/XLSX, khớp đối tác, khớp chứng từ | — |
| Đề nghị thanh toán | PASS | Nhiều file, chính sách chi, duyệt 2 cấp, ghi nhận 64x/1331, chi tiền | Chỉ số phải trả > 0; VAT khấu trừ dựa vào "có số HĐ" |
| Kỳ kế toán | PARTIAL | Đóng/mở có lý do, kiểm tra chứng từ chưa ghi sổ, khoá trong transaction | Không có trang quản lý kỳ trên web |
| Thuế | FAIL/MISSING | Bảng thuế suất GTGT có ngày hiệu lực; xác thực HĐ | TNCN, TNDN, hạn nộp sai; không có kê khai |
| Báo cáo | PARTIAL | Báo cáo tuần (API), trạng thái chứng từ | BCTC do LLM soạn; trang "Báo cáo" là bí danh nhật ký Agent |

## 6. Phát hiện kế toán (đánh mã trong [07](07_PRODUCT_GAP_ANALYSIS.md))

| Mã | Mức | Nội dung |
|---|---|---|
| ACC-01 | HIGH | `/approval-queue` (legacy L2) dùng policy `admin`, chỉ kiểm tra tenant: TENANT_OWNER duyệt được chứng từ (trái `CLAUDE.md`), ADMIN phòng A duyệt chứng từ phòng B (`ApprovalEndpoints.cs:23-27`) |
| ACC-02 | HIGH | Báo cáo tài chính do LLM soạn từ KB/dữ liệu dán tay, không từ sổ (`web/src/app/(app)/accounting/reports/page.tsx`) |
| ACC-03 | HIGH | Không có sổ cái / CĐPS |
| ACC-04 | HIGH | Dò trùng hoá đơn sai khoá, tự từ chối chứng từ hợp lệ (TAX-H02) |
| ACC-05 | CRITICAL | Máy tính TNCN/TNDN sai luật kỳ 2026 (TAX-C01, TAX-C02) |
| ACC-06 | CRITICAL | Điều kiện khấu trừ VAT sai ngưỡng (30tr thay vì 5tr) và quá lỏng (TAX-C03) |
| ACC-07 | MEDIUM | Không có UI quản lý kỳ kế toán |
| ACC-08 | HIGH (demo) / MEDIUM | **Không tenant nào bật cờ `voucher_flow_v2`** (`tenant_feature_flags` rỗng): vòng đời nộp → duyệt/trả lại/từ chối, khoá kỳ và chặn theo validator đều **tắt** trên môi trường demo (route trả `voucher_flow_v2_disabled`, `VoucherLifecycleEndpoints.cs:67`) |
| ACC-09 | MEDIUM | Gateway chưa có lớp chặn token do Agent sử dụng trên các route ghi sổ (phòng thủ theo chiều sâu) |
