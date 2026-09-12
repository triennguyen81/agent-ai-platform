# Quy trình đối soát chiết khấu hợp đồng Fujiwa — toàn bộ trường hợp

> **Nguồn:** Anh Toàn share SVG interactive flowchart, 2026-09-12
> **Loại:** BA Process Diagram — Payin reconciliation
> **Phạm vi:** 4 loại chiết khấu (thương mại, thanh toán sớm, volume rebate, trả hàng)
> **Từ:** Nhận khoản thu → Bút toán kết sổ

---

## Overview

Flowchart mô tả pipeline agent tự động đối soát khoản thu ngân hàng với hợp đồng — xác định loại chiết khấu áp dụng và sinh bút toán phù hợp. Điểm mấu chốt: **agent phân loại đúng loại CK trước khi hạch toán** thay vì gộp mọi khoản chênh lệch vào "công nợ khác".

## Pipeline chính (5 bước)

### 1. Nhận khoản thu từ khách hàng
- **Nguồn:** Báo có ngân hàng / MT940 / API BIDV/VCB
- **Input:** ngày giao dịch, số tiền, mã KH tra cứu

### 2. Load hợp đồng & điều khoản CK
- Mã KH → HĐ → % CK / kỳ hạn / doanh số ngưỡng
- Đọc từ contract terms (`sim_contracts` extended)

### 3. Tính số tiền kỳ vọng
- **Công thức:** `Expected = Tổng HĐ − CK Thương mại − CK Thanh toán (nếu trả đúng hạn)`

### 4. So sánh: Số tiền nhận = kỳ vọng?
- **Nếu khớp** (`delta ≤ tolerance`) → nhánh xác nhận thu đúng
- **Nếu lệch** → nhánh phân tích chênh lệch → phân loại CK

### 5. Cập nhật sổ + đối chiếu cuối kỳ + báo cáo

---

## Nhánh khớp (right branch)

Xác nhận thu đúng → hạch toán:
```
Nợ 112 (Tiền gửi ngân hàng)
Có 131 (Phải thu khách hàng)
```
→ Xoá công nợ KH.

---

## Nhánh lệch (left branch) — phân loại 4 loại CK

Agent chạy classifier để xác định loại chênh lệch:

### Case A — CK Thanh toán sớm (TK 635)

**Điều kiện kiểm tra (3 checks):**
1. Đúng hạn? (trong window early payment)
2. Đúng tỷ lệ? (khớp % trong contract terms)
3. Đúng HĐ? (không phải nhầm HĐ khác)

**Nếu hợp lệ → bút toán:**
```
Nợ 112 (Tiền gửi NH — số thực thu)
Nợ 635 (Chi phí tài chính — phần CK)
Có 131 (Phải thu KH — số kỳ vọng)
```

### Case B — Volume Rebate (TK 521)

**Logic:**
- Tính doanh số luỹ kế (period-to-date) vs ngưỡng volume trong HĐ
- Nếu chạm/vượt ngưỡng → sinh rebate

**Bút toán:**
```
Nợ 521 (Giảm trừ doanh thu / rebate)
Có 131 hoặc 338 (Phải trả khác — nếu ghi nhận sau)
```

### Case C — Trả hàng / Điều chỉnh (HĐ âm)

**Kiểm tra:**
- BB trả hàng có không?
- HĐ gốc còn không?
- Đã nhập lại kho chưa?

**Bút toán:**
```
Nợ 521 (Hàng bán bị trả lại) hoặc 156 (Hàng tồn kho)
Có 131 (Phải thu KH) hoặc 112 (nếu đã hoàn tiền)
```

### Case D — Không xác định được (fallback)

**Quy trình leo thang:**
1. Tạm treo → Nợ 112 / Có 338 (Phải trả khác — chờ xử lý)
2. Liên hệ KH xác nhận
3. Sales hỗ trợ
4. BGĐ duyệt

**Sau khi xác nhận** → reverse tạm treo + hạch toán đúng loại.

---

## Merge & finalize (bottom section)

Tất cả 4 case + case khớp → hội tụ:

### Cập nhật sổ chi tiết TK 131
- Ghi nhận CK / đóng công nợ / ghi dư chờ xử lý

### Đối chiếu cuối kỳ
- Sổ 131 vs sao kê NH vs bảng tính rebate

### Báo cáo tổng hợp
- Đã đối soát / còn treo / quá hạn / CK chưa duyệt
- Gửi Sales + BGĐ + Kế toán trưởng

---

## Legend (nodes)

- 🟢 **Xanh lá** — Bút toán (đã confirm hạch toán)
- 🟢 **Xanh mint** — AI logic / xử lý
- 🟣 **Tím** — Volume rebate
- 🟠 **Cam** — Trả hàng / điều chỉnh
- 🔴 **Đỏ** — Không xác định / cần escalate
- 🔵 **Xanh dương** — Cập nhật sổ / báo cáo

## Ghi chú kỹ thuật

- Mỗi khoản thu trigger 1 pipeline
- Agent phân loại CK deterministic (rule engine), không dùng LLM ở classifier
- Escalation queue có SLA (mặc định 3 ngày làm việc)
- Tolerance mặc định: ±1000đ cho số tiền
