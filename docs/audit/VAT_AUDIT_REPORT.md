# AIQ — Báo cáo audit quy trình Thuế GTGT (VAT)

Ngày audit: 2026-11-13 · Nhánh `feat/chung-tu-flow-v2` @ `b0d7aaf` · Kỳ pháp lý đối chiếu: 2025–2026.
Rủi ro: [TAX_RISK_REGISTER.md](TAX_RISK_REGISTER.md) · Test: [TAX_TEST_COVERAGE.md](TAX_TEST_COVERAGE.md).

## 1. Kết luận

**Mức sẵn sàng: KHÔNG sẵn sàng cho kê khai VAT.** AIQ đã có phần **kiểm tra chứng từ/hoá đơn** khá chắc chắn (validator xác định, bảng thuế suất có kỳ hiệu lực, cổng xác thực hoá đơn do ADMIN chứng thực, khoá kỳ kế toán). Phần **tính và kê khai VAT theo kỳ chưa tồn tại**: không có tổng hợp đầu ra/đầu vào, không chuyển kỳ, không có tờ khai 01/GTGT. Điều kiện khấu trừ đầu vào dùng ngưỡng sai (30tr thay vì 5tr) — rủi ro CRITICAL.

## 2. Bản đồ thành phần

| Thành phần | Vị trí | Vai trò |
|---|---|---|
| Validator chứng từ (thuần, không AI) | [VoucherValidator.cs](../../gateway/src/Aiq.Gateway/Vouchers/Validation/VoucherValidator.cs) (v3) | MST, bên mua/bán là công ty, cộng tiền, thuế từng dòng, thuế suất theo ngày, ngày tương lai, kỳ khoá |
| Bảng thuế suất có hiệu lực | `vat_rates` — [068_voucher_validation.sql:11-35](../../gateway/src/Aiq.Gateway/Data/Migrations/068_voucher_validation.sql#L11-L35) | code, rate, effective_from/to, legal_ref |
| Cổng xác thực hoá đơn | [TaxVerification.cs](../../gateway/src/Aiq.Gateway/Vouchers/TaxVerification.cs), bảng `voucher_tax_verification_events` | ADMIN chứng thực có bằng chứng; Agent/USER chỉ đề xuất; fingerprint vô hiệu khi dữ liệu đổi |
| Chống trùng | [IVoucherRepository.cs:1001](../../gateway/src/Aiq.Gateway/Vouchers/IVoucherRepository.cs#L1001), [VoucherExtractJobRunner.cs:551](../../gateway/src/Aiq.Gateway/Jobs/VoucherExtractJobRunner.cs#L551) | MST bán + số HĐ |
| Cờ rủi ro AP / khấu trừ | [VoucherExtractJobRunner.cs:1540-1600](../../gateway/src/Aiq.Gateway/Jobs/VoucherExtractJobRunner.cs#L1540-L1600) | `expense_over_30m_cash_payment` |
| VAT được khấu trừ trên đề nghị thanh toán | [IExpenseClaimRepository.cs:384-437](../../gateway/src/Aiq.Gateway/Employees/IExpenseClaimRepository.cs#L384-L437), [JournalRoutingRules.cs:195-198](../../gateway/src/Aiq.Gateway/Accounting/JournalRoutingRules.cs#L195-L198) | Nợ 1331 |
| Công cụ tách giá–thuế | `calculate_vat` [accounting_tools.py:262-310](../../agent/src/aiq_agent/agentic/accounting_tools.py#L262-L310) | 1 hoá đơn, rate ∈ {0,5,8,10} |
| Tra cứu TCT | `verify_einvoice_tct`, `propose_tax_verification` ([voucher_tools.py](../../agent/src/aiq_agent/agentic/voucher_tools.py)) | Best-effort; chỉ đề xuất |
| UI | [/accounting/tax-calc](../../web/src/app/(app)/accounting/tax-calc/page.tsx), [/accounting/deadline](../../web/src/app/(app)/accounting/deadline/page.tsx), panel xác thực trên chi tiết chứng từ | |
| MISA / XML HĐĐT | **Không có** ([extraction.py:15](../../agent/src/aiq_agent/documents/extraction.py#L15) ghi rõ chưa có parser XML) | |

## 3. Đánh giá theo hạng mục

### 3.1 Thu thập dữ liệu

| Hạng mục | Trạng thái | Bằng chứng / ghi chú |
|---|---|---|
| Hoá đơn đầu vào | PARTIAL | OCR/LLM trích xuất từ PDF/ảnh → `vouchers` (`invoice_in`). Không đọc XML HĐĐT (nguồn chuẩn). |
| Hoá đơn đầu ra | PARTIAL | `invoice_out`, kiểm `SELLER_IS_COMPANY`. Không đồng bộ từ hệ thống phát hành HĐĐT. |
| HĐ điều chỉnh / thay thế / huỷ | NOT IMPLEMENTED | Không có trường/quan hệ "HĐ gốc", "tính chất HĐ"; grep không thấy xử lý. |
| Chứng từ thanh toán | PARTIAL | Sao kê, cash_transactions, cash settlement có; **không** liên kết vào điều kiện khấu trừ VAT. |
| Dữ liệu tài khoản | PARTIAL | Bút toán đề xuất/posting; không có sổ cái 133/3331 theo kỳ. |
| Số còn được khấu trừ kỳ trước | NOT IMPLEMENTED | Không có bảng/biến nào. |
| MISA / HĐĐT | NOT IMPLEMENTED | Chỉ có dòng chữ ở UI nhà cung cấp "khi triển khai thật sẽ kéo từ ERP (Misa…)". |

### 3.2 Kiểm tra hoá đơn

| Hạng mục | Trạng thái | Bằng chứng |
|---|---|---|
| Trùng hoá đơn | **FAIL** | Khoá thiếu ký hiệu, không chuẩn hoá → từ chối nhầm/lọt trùng (TAX-H02). |
| Sai MST | PASS | `MST_FORMAT` (Error), `MST_CHECKSUM` (Warning) [VoucherValidator.cs:84-98](../../gateway/src/Aiq.Gateway/Vouchers/Validation/VoucherValidator.cs#L84-L98); có unit test. |
| Bên mua/bán là công ty | PASS | `BUYER_IS_COMPANY` / `SELLER_IS_COMPANY` (Error); cảnh báo khi chưa cấu hình MST công ty. |
| Sai ngày | PARTIAL | Chỉ chặn ngày tương lai (> +1 ngày). Không kiểm ngày lập so với ngày ký/ngày khai, không kiểm hoá đơn quá hạn kê khai. |
| Sai tiền hàng / thuế / tổng | PASS | `AMOUNT_SUM` (Error), `LINE_SUM` (Error), `VAT_LINE`/`VAT_AMOUNT` (Warning), dung sai 1 đ/dòng. |
| Thuế suất theo ngày hoá đơn | PARTIAL | Đúng cơ chế (QĐ 11), nhưng bảng thiếu các cửa sổ 8% 2022–06/2025 → báo Error nhầm và chặn chuyển trạng thái v2 (TAX-M01). Chỉ kiểm con số, không kiểm nhóm hàng có thuộc diện 8%/5% hay không. |
| HĐ điều chỉnh/thay thế/huỷ | NOT IMPLEMENTED | — |
| Không đủ điều kiện khấu trừ | **FAIL** | Xem 3.4. |
| Đối chiếu HĐ – chứng từ – sổ | PARTIAL | `JOURNAL_TOTAL` (Warning) so bút toán TK thanh toán với tổng HĐ; xác thực TCT do ADMIN chứng thực (PASS về kiểm soát). Không đối chiếu với sổ 133/3331. |

### 3.3 Tính thuế

| Hạng mục | Trạng thái | Bằng chứng |
|---|---|---|
| Tách giá–thuế 1 hoá đơn | PASS (có lưu ý) | VAT-01..03, 05, 06 PASS; VAT-04 FAIL làm tròn (float, không làm tròn đồng — TAX-M03). |
| Không chịu thuế / KKKNT | PARTIAL | Có trong bảng `vat_rates` (rate NULL) nhưng `calculate_vat` từ chối mọi giá trị ngoài {0,5,8,10}. |
| VAT đầu ra / đầu vào theo kỳ | NOT IMPLEMENTED | Không có truy vấn tổng hợp nào. |
| Đầu vào không được khấu trừ, phân bổ theo doanh thu chịu thuế | NOT IMPLEMENTED | — |
| Điều chỉnh tăng/giảm | NOT IMPLEMENTED | — |
| Chuyển kỳ sau / hoàn thuế | NOT IMPLEMENTED | Skill chỉ mô tả bằng lời. |
| VAT phải nộp | NOT IMPLEMENTED | — |

**Công thức hiện có:** chỉ `VAT = net × rate` / `net = gross/(1+rate)`.
**Công thức đúng (kỳ):** VAT phải nộp = VAT đầu ra − VAT đầu vào đủ điều kiện khấu trừ trong kỳ − số còn được khấu trừ kỳ trước chuyển sang ± điều chỉnh kỳ trước (phụ lục/khai bổ sung) — mỗi thành phần cần nguồn dữ liệu và rule riêng. Không có thành phần nào được triển khai.

### 3.4 Điều kiện khấu trừ

| Hạng mục | Trạng thái | Bằng chứng |
|---|---|---|
| Hoá đơn hợp pháp | PARTIAL | Tax verification gate (ADMIN chứng thực) chỉ là **điều kiện chi trả** đề nghị thanh toán, không phải điều kiện khấu trừ. |
| Phục vụ hoạt động chịu thuế | NOT IMPLEMENTED | Không có cờ/khai báo. |
| Thanh toán không dùng tiền mặt | **FAIL** | Ngưỡng 30tr (đúng: 20tr trước 01/07/2025; **5tr** từ 01/07/2025, giá có thuế, cộng gộp trong ngày — Luật 48/2024 Đ14.2). Chỉ là cờ rủi ro, chỉ áp dụng nhánh AP khi `payment_method` trích xuất từ HĐ = TM. |
| Trả chậm / trả góp | NOT IMPLEMENTED | Không theo dõi hạn thanh toán ↔ khấu trừ, không điều chỉnh giảm khi đến hạn mà chưa có chứng từ. |
| Expense claim | **FAIL** | "Có `invoice_no`" ⇒ VAT được khấu trừ, Nợ 1331 (TAX-C03). |

### 3.5 Kê khai

| Hạng mục | Trạng thái |
|---|---|
| Phân loại tháng/quý | NOT IMPLEMENTED (chỉ ghi chú "DT < 50 tỷ được khai quý" trong bảng hạn nộp) |
| Tờ khai 01/GTGT + phụ lục | NOT IMPLEMENTED |
| Đối chiếu tờ khai – sổ | NOT IMPLEMENTED |
| XML | NOT IMPLEMENTED |
| Trạng thái nộp / thông báo tiếp nhận | NOT IMPLEMENTED |
| Tờ khai bổ sung / phiên bản | NOT IMPLEMENTED |
| Hạn nộp | **FAIL** (TAX-H01: quý ngày 30; không dời ngày nghỉ) |

## 4. Kiểm soát & phân quyền (phần đã có)

- **Agent không thể kết luận hoá đơn hợp lệ:** `ProposeAsync` chỉ ghi `pending_review`; `RejectAsync` từ chối `actorType == "agent"` ([TaxVerification.cs:414-415](../../gateway/src/Aiq.Gateway/Vouchers/TaxVerification.cs#L414-L415)); `verified` chỉ từ `ConfirmAsync` của ADMIN với nguồn/ghi chú/xác nhận đã tra — **PASS**, có integration test `TaxVerificationIntegrationTests` (không chạy lại trong audit, xem coverage).
- **Tenant/department isolation:** mọi truy vấn của TaxVerification lọc `tenant_id AND department_id` — **PASS** (đọc mã).
- **Vô hiệu khi đổi dữ liệu:** `InvalidateIfIdentityChangedAsync` + fingerprint — **PASS** (đọc mã).
- **Kỳ đã khoá:** `PERIOD_LOCKED` (v2) — **PASS**, unit test.

## 5. Đề xuất (theo ưu tiên)

1. P0 — Sửa rule khấu trừ (TAX-C03): version theo ngày hoá đơn; dùng chứng từ thanh toán thực tế; kết quả ảnh hưởng số được khấu trừ.
2. P1 — Khoá chống trùng (TAX-H02); bổ sung cửa sổ 8% lịch sử (TAX-M01); sửa hạn nộp (TAX-H01).
3. P2 — Sổ/bảng kê VAT theo kỳ, chuyển kỳ, 01/GTGT + XML, trạng thái nộp, khai bổ sung, HĐ điều chỉnh/thay thế (TAX-H04); nhập XML HĐĐT.
