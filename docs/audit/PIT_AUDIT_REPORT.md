# AIQ — Báo cáo audit quy trình Thuế TNCN (PIT)

Ngày audit: 2026-11-13 · Nhánh `feat/chung-tu-flow-v2` @ `b0d7aaf` · Kỳ pháp lý đối chiếu: 2025 và 2026.
Rủi ro: [TAX_RISK_REGISTER.md](TAX_RISK_REGISTER.md) · Test: [TAX_TEST_COVERAGE.md](TAX_TEST_COVERAGE.md).

## 1. Kết luận

**Mức sẵn sàng: KHÔNG sẵn sàng.** Quy trình TNCN **chưa được triển khai** — không có dữ liệu bảng lương, nhân viên chịu thuế, người phụ thuộc, khấu trừ, quyết toán. Thứ duy nhất tồn tại là công cụ `calculate_pit` (máy tính một người/một tháng), và công cụ này **tính sai cho kỳ 2026** vì dùng tham số pháp lý của kỳ 2025 (CRITICAL — TAX-C01).

## 2. Bản đồ thành phần

| Thành phần | Vị trí | Ghi chú |
|---|---|---|
| Engine | `calculate_pit`, `_compute_pit` — [accounting_tools.py:195-247, 312-383](../../agent/src/aiq_agent/agentic/accounting_tools.py#L195-L247) | Python, float, hằng số hardcode |
| Skill | [tax-calculation/SKILL.md:92-183](../../agent/skills/tax-calculation/SKILL.md#L92-L183) | Hướng dẫn LLM giải thích |
| UI | [tax-calc/page.tsx:213-300](../../web/src/app/(app)/accounting/tax-calc/page.tsx#L213-L300) | Gửi prompt tự do tới capability `accounting.tax` |
| Hạn nộp | `05/QTT-TNCN`, `02/QTT-TNCN`, `05/KK-TNCN` trong [accounting_tools.py:169-185](../../agent/src/aiq_agent/agentic/accounting_tools.py#L169-L185) | |
| Nhân viên | [IEmployeeRepository.cs:16-25](../../gateway/src/Aiq.Gateway/Employees/IEmployeeRepository.cs#L16-L25) | Chỉ phục vụ tạm ứng/chi phí |
| Bảng lương / payroll | **Không có** | `calculate_payroll`, `lookup_employee` chỉ là mã catalog demo không có handler ([seed_demo_agents.sql:24-25](../../scripts/seed_demo_agents.sql#L24-L25)) |
| DB | **Không có bảng nào** cho thu nhập, NPT, khấu trừ, chứng từ khấu trừ | |
| MISA | Không có | |

## 3. Đánh giá theo hạng mục

### 3.1 Thu thập dữ liệu — NOT IMPLEMENTED (toàn bộ)
Danh sách NV chịu thuế, HĐLĐ, thu nhập, phụ cấp, thưởng, khoản miễn thuế, giảm trừ, NPT, chứng từ khấu trừ, uỷ quyền quyết toán: không có trường/bảng/API nào. Hồ sơ nhân viên không có MST cá nhân, loại HĐ, tình trạng cư trú.

### 3.2 Phân loại đối tượng — NOT IMPLEMENTED
Không phân biệt cư trú/không cư trú, HĐ ≥ 3 tháng / < 3 tháng / không ký HĐ, thử việc, nhiều nguồn thu nhập. Engine ngầm định: cư trú, HĐ ≥ 3 tháng, thu nhập lương tháng.

### 3.3 Tính thuế

| Hạng mục | Trạng thái | Hiện tại | Đúng (kỳ 2026) | Test |
|---|---|---|---|---|
| Giảm trừ bản thân | **FAIL** | 11.000.000 | 15.500.000 (NQ 110/2025/UBTVQH15, từ kỳ 2026) | PIT-01 |
| Giảm trừ NPT | **FAIL** | 4.400.000 | 6.200.000 | PIT-03 |
| Biểu lũy tiến | **FAIL** | 7 bậc 5–35%, ngưỡng 5/10/18/32/52/80tr | 5 bậc 5/10/20/30/35%, ngưỡng 10/30/60/100tr (Luật 109/2025/QH15 Đ9) — **LRR** ngày áp dụng | PIT-02, 05, 07 |
| Biểu 7 bậc cho kỳ 2025 | PASS | đúng | đúng (quyết toán 2025 vẫn dùng NQ 954/2020) | PIT-06 |
| BH bắt buộc NLĐ 10,5% | PARTIAL | 10,5% trên min(gross, 46,8tr) | BHXH+BHYT 9,5% trần 20× lương cơ sở; BHTN 1% trần 20× LTTV vùng (Luật Việc làm 2025) — **lương đóng BH ≠ gross** | PIT-04 FAIL (−32.000) |
| Thu nhập miễn thuế | NOT IMPLEMENTED | — | ăn ca, làm thêm giờ phần chênh lệch, … | — |
| Thuế suất toàn phần | NOT IMPLEMENTED | — | Không cư trú 20%; khấu trừ 10% thu nhập vãng lai ≥ 2tr | — |
| Thuế đã khấu trừ / nộp thừa–thiếu | NOT IMPLEMENTED | — | — | — |
| Precision / làm tròn | PARTIAL | float, không làm tròn | VND nguyên | — |
| Kỳ hiệu lực rule | **FAIL** | hằng số không có ngày hiệu lực (ghi chú "2026" nhưng giá trị 2025) | rule có `effective_from/to`, `legal_reference`, `version` | — |

**Ví dụ sai lệch (probe, gọi trực tiếp handler):**

| Ca | Engine | Đúng kỳ 2026 | Chênh |
|---|---|---|---|
| Gross 30tr, 0 NPT, có BH | 1.627.500 ₫ | 635.000 ₫ | +992.500 ₫/tháng |
| Gross 50tr, 2 NPT, có BH | 3.407.200 ₫ | 1.215.400 ₫ | +2.191.800 ₫/tháng |
| Thu nhập tính thuế đúng bằng 10tr | 1.425.000 ₫ | 500.000 ₫ | +925.000 ₫/tháng |

Nguyên nhân: tham số pháp lý hardcode, không có cơ chế chọn rule theo kỳ (TAX-H07). Engine dùng ngưỡng giảm trừ cũ nên thu nhập tính thuế bị phóng đại, rồi áp biểu cũ có nhiều bậc cao hơn.

### 3.4 Quyết toán — NOT IMPLEMENTED
Không có quyết toán tháng/quý/năm, xác định cá nhân tự quyết toán/uỷ quyền, tổng hợp nhiều nơi, nghỉ việc, nộp thừa/thiếu, chứng từ khấu trừ, 05/QTT-TNCN + phụ lục.
Bảng hạn nộp: `02/QTT-TNCN` sai (31/03 thay vì ngày cuối tháng thứ 4 = 30/04, dời 04/05/2026 do nghỉ lễ — DL-06 FAIL); `05/KK-TNCN` "khai tháng nếu khấu trừ ≥ 100tr" là quy định cũ — **LRR** (từ NĐ 126/2020, kỳ khai TNCN khấu trừ theo kỳ khai VAT).

### 3.5 Đối chiếu — NOT IMPLEMENTED
Không có bảng lương, thanh toán lương (TK 334 chỉ dùng cho hoàn ứng chi phí), số thuế đã khấu trừ (TK 3335 chỉ có trong bảng tra tài khoản), tờ khai.

## 4. Kiến trúc / Agent

- Tính toán bằng hàm xác định (tốt), nhưng đường UI đi qua LLM và hiển thị câu trả lời LLM (TAX-H03). Skill dạy LLM tham số cũ, và ví dụ net→gross tự mâu thuẫn (TAX-M07) — LLM có thể "tính tay" theo skill nếu tool không được cấp (TAX-M08).
- Tool read-only, không ghi sổ, không nộp — PASS về giới hạn quyền.
- Không có bản ghi tính toán có thể tái lập.

## 5. Đề xuất

1. P0 — Rule store TNCN có version; nạp bộ 2025 và bộ 2026 (sau khi chuyên gia xác nhận ngày áp dụng biểu 5 bậc); sửa trần BH theo căn cứ đúng; dùng `Decimal`; test biên mọi ngưỡng.
2. P1 — Cập nhật skill; sửa hạn 02/QTT-TNCN.
3. P2 — Module payroll/TNCN: hồ sơ thuế NV (MST, cư trú, loại HĐ, NPT có hiệu lực theo tháng), bảng lương kỳ, khấu trừ tháng/quý, quyết toán năm, chứng từ khấu trừ, 05/QTT-TNCN, đối chiếu 334/3335 — hoặc tích hợp từ phần mềm lương/MISA nếu quyết định không tự xây.
