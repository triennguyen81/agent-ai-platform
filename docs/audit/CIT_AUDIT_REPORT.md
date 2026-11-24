# AIQ — Báo cáo audit quy trình Thuế TNDN (CIT)

Ngày audit: 2026-11-13 · Nhánh `feat/chung-tu-flow-v2` @ `b0d7aaf` · Kỳ pháp lý đối chiếu: 2025–2026 (Luật 67/2025/QH15).
Rủi ro: [TAX_RISK_REGISTER.md](TAX_RISK_REGISTER.md) · Test: [TAX_TEST_COVERAGE.md](TAX_TEST_COVERAGE.md).

## 1. Kết luận

**Mức sẵn sàng: KHÔNG sẵn sàng.** Không có nguồn dữ liệu sổ cái cho TNDN; BCTC (kể cả dòng thuế TNDN) do LLM soạn từ ghi chú hoặc văn bản người dùng dán vào. `calculate_cit` là phép nhân một dòng với **thuế suất mặc định 20% và không kiểm tra điều kiện** — tính thừa cho doanh nghiệp đủ điều kiện 15%/17% theo Luật 67/2025 (CRITICAL — TAX-C02). Có một điểm tốt: chi phí trên đề nghị thanh toán đã tách cờ "không được trừ khi tính TNDN" riêng với VAT.

## 2. Bản đồ thành phần

| Thành phần | Vị trí | Ghi chú |
|---|---|---|
| Engine | `calculate_cit` — [accounting_tools.py:385-436](../../agent/src/aiq_agent/agentic/accounting_tools.py#L385-L436) | `taxable = revenue − expense − loss`, `tax = taxable × rate` |
| Skill | [tax-calculation/SKILL.md:72-90](../../agent/skills/tax-calculation/SKILL.md#L72-L90) | Bảng thuế suất không khớp Luật 67/2025 |
| UI tính thuế | [tax-calc/page.tsx:301-390](../../web/src/app/(app)/accounting/tax-calc/page.tsx#L301-L390) | Người dùng tự chọn 5/10/15/17/20 |
| BCTC | [reports/page.tsx](../../web/src/app/(app)/accounting/reports/page.tsx) | Prompt LLM, dữ liệu từ KB/dán tay; tên công ty mặc định hardcode |
| Chi phí không được trừ | `non_deductible_amount` — [036_expense_claim_vat_split.sql](../../gateway/src/Aiq.Gateway/Data/Migrations/036_expense_claim_vat_split.sql), [IExpenseClaimRepository.cs:394-399](../../gateway/src/Aiq.Gateway/Employees/IExpenseClaimRepository.cs#L394-L399) | Dòng không có hoá đơn → không được trừ |
| Sổ cái / CĐPS | **Không có** | Chỉ có `voucher_journal_suggestions` (source suggestion/posting/reversal) |
| Tài sản cố định / khấu hao / dự phòng / lãi vay | **Không có** | |
| Hạn nộp | `02/TNDN`, `03/TNDN` — [accounting_tools.py:157-168](../../agent/src/aiq_agent/agentic/accounting_tools.py#L157-L168) | |

## 3. Đánh giá theo hạng mục

### 3.1 Thu thập dữ liệu

| Hạng mục | Trạng thái | Ghi chú |
|---|---|---|
| Doanh thu BH&CCDV | PARTIAL | Hoá đơn đầu ra có trong `vouchers`, nhưng không có tổng hợp doanh thu theo kỳ/TK 511 |
| Doanh thu tài chính, thu nhập khác | NOT IMPLEMENTED | 515/711 chỉ xuất hiện trong bảng tra TK và cặp bút toán chênh lệch tỷ giá |
| Giá vốn, CP bán hàng, CP QLDN | PARTIAL | Chi phí từ đề nghị thanh toán ghi Nợ 641/642/627; không có giá vốn, không tổng hợp |
| Chi phí tài chính, lương, khấu hao, dự phòng, lãi vay | NOT IMPLEMENTED | |
| Điều chỉnh tăng/giảm TNCT | NOT IMPLEMENTED | |
| Lỗ chuyển từ kỳ trước | NOT IMPLEMENTED | Chỉ là tham số nhập tay `loss_carryforward` |

### 3.2 Chi phí được trừ

| Hạng mục | Trạng thái | Bằng chứng |
|---|---|---|
| Có hoá đơn, chứng từ | PARTIAL | Expense claim: không `invoice_no` ⇒ `non_deductible`. Không áp dụng cho chứng từ khác. |
| Liên quan SXKD | NOT IMPLEMENTED | |
| Điều kiện thanh toán (≥ 5tr không dùng tiền mặt, Luật 67/2025 + Luật GTGT 48/2024) | NOT IMPLEMENTED | Ngưỡng duy nhất trong mã là 30tr và chỉ dành cho VAT (TAX-C03) |
| Chi phí không được trừ / khống chế | NOT IMPLEMENTED | Cấu hình `GiftEntertainmentBudgetPct = 15%` (căn cứ đã bãi bỏ, không được dùng — TAX-L02) |
| Khấu hao, lãi vay (khống chế 30% EBITDA với GDLK), dự phòng, phúc lợi (≤ 1 tháng lương) | NOT IMPLEMENTED | |
| Chênh lệch kế toán – thuế | NOT IMPLEMENTED | |

### 3.3 Tính thuế

**Công thức hiện tại** ([accounting_tools.py:401-405](../../agent/src/aiq_agent/agentic/accounting_tools.py#L401-L405)):
```
taxable = revenue − expense − max(loss, 0)
tax     = taxable × rate_pct   (nếu taxable > 0)       rate_pct mặc định 20
```

**Công thức đúng:**
```
TNCT        = Doanh thu − Chi phí được trừ + Thu nhập khác
TNTT        = TNCT − Thu nhập miễn thuế − Lỗ được chuyển (≤ 5 năm liên tục, chỉ phần lỗ còn lại)
Thuế TNDN   = TNTT × thuế suất theo điều kiện − ưu đãi/miễn giảm
Phải nộp    = Thuế TNDN − số đã tạm nộp
```

| Hạng mục | Trạng thái | Bằng chứng |
|---|---|---|
| Thuế suất phổ thông 20% | PASS | CIT-04 |
| Thuế suất 15% (DT năm ≤ 3 tỷ) / 17% (> 3 – 50 tỷ) | **FAIL** | Không tự xác định; mặc định 20% → CIT-01, CIT-02 FAIL; chấp nhận 17% cho DT 200 tỷ → CIT-03 FAIL |
| Thuế suất ưu đãi (10%, …) và điều kiện | **FAIL** | Cho phép chọn tự do, không hồ sơ ưu đãi |
| Thu nhập miễn thuế | NOT IMPLEMENTED | |
| Chuyển lỗ | PARTIAL | Có trừ lỗ, không kiểm tra thời hạn 5 năm/số lỗ còn lại; lỗ âm bị bỏ qua (CIT-05 PASS) |
| Ưu đãi, miễn giảm | NOT IMPLEMENTED | |
| Tạm nộp quý, đối chiếu ≥ 80% | NOT IMPLEMENTED | Chỉ là ghi chú trong bảng hạn nộp |
| Điều chỉnh kế toán – thuế | NOT IMPLEMENTED | |
| Precision | PARTIAL | float |

### 3.4 Kê khai và quyết toán

| Hạng mục | Trạng thái | Ghi chú |
|---|---|---|
| Tạm nộp quý | NOT IMPLEMENTED; hạn nộp **FAIL** | Mục `02/TNDN` "Tờ khai tạm tính" theo quý là quy định cũ — từ NĐ 126/2020 chỉ tạm nộp, không nộp tờ khai quý (**LRR**). Hạn quý cố định ngày 30 (sai Q2/Q3/Q4). |
| Quyết toán năm, 03/TNDN + phụ lục | NOT IMPLEMENTED | Hạn 31/03 chỉ đúng với năm tài chính dương lịch (DL-08 PASS cho trường hợp đó) |
| Theo dõi chuyển lỗ, ưu đãi | NOT IMPLEMENTED | |
| Đối chiếu BCTC, sổ cái | **FAIL** | BCTC do LLM sinh từ KB/dán tay, không từ sổ (TAX-H06) |
| Tờ khai bổ sung, lịch sử | NOT IMPLEMENTED | |

## 4. Đề xuất

1. P0 — Rule thuế suất TNDN có điều kiện + version (Luật 67/2025 cho kỳ 2025 trở đi; luật cũ cho kỳ trước); bắt buộc input doanh thu kỳ trước; từ chối mức ưu đãi khi không có hồ sơ ưu đãi; `Decimal`.
2. P1 — Bỏ/khoá màn hình BCTC sinh bằng LLM khỏi luồng số liệu chính thức, hoặc gắn nhãn "bản nháp, không phải số liệu sổ sách".
3. P2 — Sổ cái + bảng CĐPS từ bút toán posting; module điều chỉnh chi phí không được trừ; theo dõi lỗ theo năm phát sinh; tạm nộp quý; 03/TNDN.
