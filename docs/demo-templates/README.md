# Demo Templates — Chứng từ mẫu để test

6 template HTML mô phỏng chứng từ VN thực tế, khớp với `sim_customers` / `sim_suppliers` / `sim_employees` đã seed trong DB.

## Cách dùng

1. Mở file `.html` bằng browser (Chrome/Edge/Firefox)
2. **Cách A — Screenshot PNG:** Windows: `Win + Shift + S` → chọn vùng hoá đơn → paste vào Paint → save PNG
3. **Cách B — Print PDF:** `Ctrl + P` → chọn "Save as PDF" → save
4. **Cách C — Chrome extension "Full Page Screenshot":** bấm F12 → devtools console → `document.body.style.zoom = 1.0` → Cmd+Shift+P → "Capture full size screenshot"
5. Upload file PNG/PDF vào `/vouchers/upload`

## 6 template

| # | File | Case demo | Buyer MST | Số tiền | Điểm kỳ vọng |
|---|------|-----------|-----------|---------|--------------|
| 01 | `01-hoa-don-vat-nha-xinh-30tr.html` | Case 1: KH cũ đã có công nợ | 0311234567 (Nha Xinh) | 31.5M | Panel AR hiện: current 187.5M → 219M, dùng 44%, aging 367.5M quá hạn 1-30d |
| 02 | `02-hoa-don-vat-bv-rhm-650tr.html` | Case 3: L2 approval | 0311999001 (BV RHM) | 651M | Badge "Cần duyệt cấp 2" đỏ. 4 flags: > 500M + credit > 80% + would_breach + has_overdue |
| 03 | `03-hoa-don-vat-dong-a-15tr.html` | Case 1b: KH cũ chưa có công nợ | 0311234571 (Đông Á) | 15M | Panel AR: current 0 → 15M, dùng 5%, aging trống, no flags |
| 04 | `04-phieu-thu-nha-xinh.html` | Case Phiếu thu | (từ KH PK-001) | 183.75M | Voucher type=receipt, ghi Nợ 111/112 Có 131 |
| 05 | `05-uy-nhiem-chi-kavo.html` | Case UN chi ngoại tệ | (bên nhận KaVo DE) | 30k EUR | Voucher type=bank_transfer, ghi Nợ 331 Có 112 + CLTG |
| 06 | `06-de-nghi-hoan-ung.html` | Case Hoàn ứng nhân viên | (Trần Văn Đức NV-0007) | 6.35M | Voucher type=expense_claim, Nợ 6417 Có 141 + 111 |

## Case 4 (duplicate — E2)

Không cần template riêng. Sau khi upload template 01 (Nha Xinh 30tr) và có `voucherId`:
1. Vào Postgres: `UPDATE vouchers SET document_number='0001101', seller_mst='0311234567', document_date='2026-07-12' WHERE id=<voucherId>;`  
   (giả lập đây là hoá đơn cũ)
2. Upload lại template 01 → agent extract sẽ ra số 0001234, không trùng
3. **Test đúng cách:** upload template 01 → sau khi extract xong, sửa số HĐ trong `sim_ar_ledger` 00001101 thành 0001234 → upload lại template 01 → **409 Conflict**

Hoặc dễ hơn: đổi số trong template 01 thành `0001101` (khớp sim_ar_ledger đã có), upload → duplicate detected ngay lần đầu.

## Case 5 (KH chưa có — A1)

Sửa template 03: đổi buyer MST từ `0311234571` sang bất kỳ số nào không có trong sim_customers, ví dụ `9999999999`. Upload → panel amber "KH chưa có trong hệ thống".

## Tips khi screenshot

- Không zoom browser (giữ 100%) để OCR dễ đọc
- Full HD 1920x1080 monitor cho ảnh sắc nét
- Nếu OCR trích không đúng số VN "1.234.567" → chỉnh sang format "1,234,567" (mỹ) trong template rồi rerun

## Nếu cần convert HTML → PNG tự động

Trên cosmos có Chromium sẵn (Playwright container). Có thể chạy:
```bash
# Local (nếu có Chromium/Chrome):
chromium --headless --screenshot=out.png --window-size=1200,1600 file:///path/to/template.html

# Hoặc dùng wkhtmltopdf → wkhtmltoimage:
wkhtmltoimage --width 1200 template.html output.png
```

## Sync với sim data hiện có

Tất cả MST trong template khớp với `sim_customers`:
- 0311234567 = PK-001 Nha Xinh (đang có công nợ 187.5M, hạn mức 500M, quá hạn 1 hoá đơn)
- 0311999001 = BV-001 Bệnh viện RHM TW (đang có 1.25 tỷ, hạn mức 2 tỷ)
- 0311234571 = PK-005 Đông Á (chưa có công nợ)
- Ghế KaVo Estetica E80 = SKU `GHE-KAVO-E80` trong sim_products (giá 350M, VAT 5%)
- Máy X-quang Sirona CBCT = SKU `XRAY-SIRONA-CBCT` (giá 650M)
- Trần Văn Đức NV-0007 = trong sim_employees, ở phòng Kinh doanh, outstanding_advance 5tr
