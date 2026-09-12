# Quy Trình Lưu Trữ & Đối Chiếu Hoá Đơn Tự Động

> **Nguồn:** Anh Toàn share PDF v1.0 (6/2026), 2026-09-12
> **Loại:** BA Process Diagram — AI Document Processing
> **Phạm vi:** Hoá đơn đầu vào (mua vào — AP invoice)
> **Workflow:** 4 giai đoạn, AI-Assisted, Phòng KT-TC

---

## Overview

Workflow tự động hoá **AP invoice lifecycle** từ lúc NCC gửi email đến lúc kế toán export hồ sơ cho ngân hàng/kiểm toán. AI xử lý 80% các bước routine; kế toán viên xác nhận + xử lý exception.

## Actors & Nodes

| Icon | Actor | Vai trò |
|---|---|---|
| 🏢 | Nhà cung cấp | Phát hành HĐĐT |
| 📧 | Email kế toán | Hộp thư chung nhận HĐ |
| 🤖 | AI Agent | Tự động xử lý (Make/n8n/Custom) |
| 📁 | Google Drive | Lưu trữ (folder HOA_DON) |
| 🏛 | Cơ quan thuế | eTax / Cổng HĐĐT — nguồn đối chiếu |
| 👤 | Kế toán | Xác nhận / kiểm soát / yêu cầu |

---

## BƯỚC 1 — Tiếp Nhận & Lưu Trữ

### 1.1 Phát hành HĐĐT
- NCC phát hành HĐ qua VNPT / MISA / FPT eInvoice
- Gửi về email kế toán công ty
- Attachment: PDF + XML + link tải

### 1.2 Email Gateway
- Hộp thư chung: `ketoan@company.vn`, `hoadon@company.vn`
- Có thể cấu hình nhiều địa chỉ theo nhóm NCC hoặc loại chi phí

**Cấu trúc email chuẩn:**
- Subject/body chứa thông tin NCC
- Đính kèm `.pdf` / `.xml`
- Hoặc link tải từ hệ thống HĐĐT
- AI quét tự động mỗi X phút

### 1.3 AI Agent xử lý
1. **Đọc email & tải hoá đơn** — kết nối IMAP, download attachment
2. **Trích xuất dữ liệu** (OCR/Parser) — số HĐ, ngày, NCC, MST, tổng, VAT, mã hàng
3. **Đặt tên & phân loại file** — áp dụng rule

### 1.4 Rule đặt tên & folder

**Cấu trúc thư mục:**
```
HOA_DON/
 └─ 2026-06/                    (theo tháng)
   └─ CONG_TY_A/                (theo NCC)
     └─ 01GTKT0_CTYABC_20260605.pdf
```

**Rule đặt tên file:**
```
[TênGốcHĐ]_[TênNCC]_[YYYYMMDD]
```

### 1.5 Lưu Drive
- Upload file vào đúng folder theo rule

---

## BƯỚC 2 — Lập Bảng Kê

### 2.1 Cập nhật Google Sheet
- AI ghi thông tin HĐ vào Sheet theo thứ tự ngày tháng
- Mỗi dòng = 1 hoá đơn

**Cột bắt buộc:**
- Số hoá đơn
- Ngày HĐ
- Tên NCC
- MST NCC
- Tổng tiền
- Thuế VAT
- Mã hàng / diễn giải
- Link file Drive
- Trạng thái đối chiếu

### 2.2 Kiểm tra trùng lặp (AI validation)
- Business key: **Số HĐ + MST NCC**
- Tránh ghi trùng trong bảng kê

### 2.3 Kế toán kiểm tra bảng kê (định kỳ)
- Bổ sung diễn giải
- Phân loại tài khoản kế toán
- Confirm dữ liệu

---

## BƯỚC 3 — Đối Chiếu Thuế

### 3.1 Nguồn thuế
- Kế toán xuất bảng kê HĐĐT từ cổng **eTax** hoặc **MISA AMIS**
- Upload lên Drive / Sheet

### 3.2 AI đối chiếu tự động
- So sánh bảng kê nội bộ vs bảng kê thuế
- 3 outcome:
  - ✅ **HĐ khớp** (có cả 2 bên)
  - ⚠️ **HĐ thiếu** (có thuế, chưa lưu)
  - ❌ **HĐ lệch** (sai số / ngày / tiền)

### 3.3 Cảnh báo & xử lý

**AI alert:**
- Đánh dấu màu đỏ trong Sheet
- Gửi thông báo cho kế toán

**Kế toán xử lý:**
- Liên hệ NCC bổ sung HĐ còn thiếu
- Xác nhận trường hợp lệch có lý do hợp lệ

### 3.4 Exception (scan mờ, format lạ)
- Kế toán xử lý thủ công
- Upload bổ sung vào Drive + Sheet

---

## BƯỚC 4 — Tra Cứu & Xuất Hồ Sơ

### 4.1 Yêu cầu từ kế toán
- Nhập lệnh: **Tên NCC + khoảng thời gian**
- AI xử lý và trả kết quả trong vài giây

### 4.2 AI Search & Export
1. Lọc toàn bộ HĐ phù hợp từ Sheet + Drive
2. Tạo folder mới:
   ```
   XUAT_HO_SO/
    └─ [NCC]_[TuNgay]_[DenNgay]/
      ├─ Toàn bộ HĐ lọc được
      ├─ Bảng kê tổng hợp .xlsx
      └─ Link chia sẻ cho bank
   ```
3. Tạo link chia sẻ

### 4.3 Output
- Kế toán nhận link Drive
- Kiểm tra lần cuối
- Cung cấp cho ngân hàng / kiểm toán / BGĐ

---

## Phân loại node (legend)

- 🟢 **AI tự động** — không cần confirm
- 🟢 **Lưu trữ / Output** — kết quả nghiệp vụ
- ⚪ **Hệ thống / Email** — infrastructure
- 🟠 **Nguồn ngoài / Thuế** — không control được
- 🔵 **Kế toán xác nhận** — human-in-the-loop
- ⚫ **Bên ngoài / NCC** — không control được
- 🔴 **Cảnh báo / Rủi ro** — exception

## Ghi chú kỹ thuật

- **Tool gợi ý:** Make.com · n8n · Google Apps Script
- **Trigger:** mỗi HĐ mới → 1 workflow tự động
- **Auth:** Email shared mailbox cần OAuth2 với Drive API
- **Format ưu tiên:** XML HĐĐT có cấu trúc chuẩn → AI parse chính xác hơn PDF

---

## Điểm quan trọng

1. **Business key = Số HĐ + MST NCC** — dùng cho dedup, không phải hash file
2. **XML > PDF** — nếu có cả 2, ưu tiên XML (nguyên tắc "nguồn tin cậy hơn thắng")
3. **eTax là ground truth** — sổ nội bộ phải khớp với cổng thuế
4. **Storage = Drive folder tree** theo tháng → NCC → file (khác với AIQ dùng MinIO key-value)
5. **Bảng kê = Google Sheet** (khác với AIQ dùng Postgres + `/vouchers` page)
