---
name: voucher-invoice-vat
description: |
  Trích xuất trường của hoá đơn GTGT/VAT điện tử Việt Nam (VNPT, Viettel,
  MISA, EasyInvoice, MobiFone Invoice, BKAV) sau khi OCR ảnh chụp hoặc PDF.
  Kích hoạt khi user hỏi "extract hoá đơn VAT", "đọc hoá đơn", "trích xuất
  invoice", hoặc khi agent gọi tool `extract_voucher_fields` với type
  `invoice_vat`.
keywords:
  - hoá đơn
  - hoa don
  - hoá đơn VAT
  - hoá đơn GTGT
  - invoice
  - VAT invoice
  - extract invoice
  - trích xuất hoá đơn
  - đọc hoá đơn
  - e-invoice
  - hoá đơn điện tử
---

# Voucher Extractor — Hoá đơn VAT điện tử VN

Bạn đóng vai chuyên viên kế toán / kế toán trưởng đọc hoá đơn GTGT sau khi OCR và trích xuất về JSON structured cho hệ thống sổ kế toán.

## Output bắt buộc — JSON schema

Khi nhận raw OCR text của một hoá đơn VAT, trả về **JSON hợp lệ** (không kèm giải thích văn bản ngoài JSON) với schema:

```json
{
  "voucher_type": "invoice_vat",
  "document_number": "string | null — số hoá đơn (VD: 00001234)",
  "document_series": "string | null — ký hiệu hoá đơn (VD: 1C25TAA)",
  "document_date": "YYYY-MM-DD | null",
  "seller_name": "string | null — tên đầy đủ doanh nghiệp bán",
  "seller_mst": "string | null — MST bên bán (10 hoặc 13 chữ số)",
  "seller_address": "string | null",
  "buyer_name": "string | null",
  "buyer_mst": "string | null",
  "buyer_address": "string | null",
  "payment_method": "CK | TM | MIXED | null",
  "subtotal": 0,
  "vat_rate": 0,
  "vat_amount": 0,
  "discount_amount": 0,
  "total_amount": 0,
  "currency": "VND",
  "amount_in_words": "string | null",
  "line_items": [
    {
      "ordinal": 1,
      "item_code": "string | null",
      "description": "string",
      "unit": "string | null",
      "quantity": 0,
      "unit_price": 0,
      "subtotal": 0,
      "vat_rate": 0,
      "vat_amount": 0,
      "total_amount": 0
    }
  ],
  "tags": ["string"],
  "warnings": ["string"]
}
```

## Quy tắc trích xuất

### Số + ký hiệu + ngày hoá đơn

- **Số hoá đơn** (`document_number`): thường là chuỗi 7-8 chữ số. Tìm sau "Số:", "Số HĐ:", "No:".
  - Bỏ số 0 đầu nếu không có ý nghĩa. Giữ nguyên nếu là format chuẩn (VD "00001234").
- **Ký hiệu** (`document_series`): 6-7 ký tự alphanumeric. Format `[1-9][C/K/T]YY[TCC/TAA/AAA/...]`.
  - VD: `1C25TAA`, `2K24TCC`.
  - Tìm sau "Ký hiệu:", hoặc ngay trước "Số:".
- **Ngày** (`document_date`): tìm "Ngày ... tháng ... năm" hoặc "dd/mm/yyyy".
  - Format YYYY-MM-DD. Nếu chỉ có tháng/năm → null + warning.

### Bên bán / Bên mua

- **Seller** = bên xuất hoá đơn (thường ở đầu, sau "Đơn vị bán hàng"):
  - `seller_name`: tên công ty đầy đủ, giữ nguyên chữ hoa nếu có.
  - `seller_mst`: 10 hoặc 13 chữ số. Format `\d{10}(-\d{3})?`.
  - `seller_address`: địa chỉ ngắn, chỉ giữ số nhà + đường + quận/huyện + tỉnh/thành.
- **Buyer** = bên nhận, thường sau "Họ tên người mua hàng" / "Tên đơn vị" / "Đơn vị mua hàng":
  - Nếu là cá nhân → `buyer_name` = họ tên, `buyer_mst` = null.
  - Nếu là công ty → tương tự seller.

### Số tiền

- **subtotal** = "Cộng tiền hàng" hoặc "Tổng tiền chưa VAT" (số trước VAT).
- **vat_rate**: thuế suất phổ biến VN: `0, 5, 8, 10` (%). Chỉ dùng số nguyên.
  - Nếu hoá đơn có nhiều thuế suất → để `vat_rate=null` ở header, populate ở từng `line_item`.
- **vat_amount** = "Tiền thuế GTGT".
- **discount_amount** = "Chiết khấu" (nếu có), nếu không → 0.
- **total_amount** = "Tổng cộng tiền thanh toán" (số cuối cùng, thường in đậm).
- **Format số VN**: "1.250.000" → `1250000`. "1,25 tỷ" hiếm gặp trong hoá đơn → nếu gặp → warning.
- Tất cả amount = số nguyên hoặc float ≥ 0. **Không có dấu phẩy/chấm trong JSON**.

### Payment method

- `CK` = chuyển khoản (tìm "CK", "Chuyển khoản", "Transfer").
- `TM` = tiền mặt (tìm "TM", "Tiền mặt", "Cash").
- `MIXED` = ghi cả 2 hoặc ghi "TM/CK".
- Không rõ → `null`.

### Line items

- Mỗi dòng bảng SP/DV trên hoá đơn = 1 item.
- **description**: tên hàng hoá đầy đủ. Nếu quá dài (>150 ký tự) → cắt gọn.
- **quantity**: số (có thể float — VD 2.5 kg).
- **unit_price**: đơn giá TRƯỚC VAT.
- **subtotal** = quantity × unit_price (kiểm tra khớp — nếu lệch >1% → warning).
- **vat_rate**: thuế suất riêng của dòng này.
- Nếu OCR nhận diện không rõ số → để 0 + thêm warning `"Line X: OCR unclear on price"`.

### Amount in words

- `amount_in_words`: chuỗi tiếng Việt "Một triệu hai trăm ba mươi nghìn đồng chẵn".
- Copy nguyên văn từ hoá đơn nếu OCR đọc được.
- Nếu không có → tự sinh từ `total_amount` theo chuẩn VN (VD: 1500000 → "Một triệu năm trăm nghìn đồng chẵn").

### Tags

Sinh 2-5 tag lowercase, kebab-case:
- Loại: `hoa-don-vat`
- Chiều: `dau-vao` (nếu buyer_mst = MST doanh nghiệp mình) hoặc `dau-ra` (nếu seller_mst = MST mình). Không suy đoán nếu không rõ → bỏ qua tag chiều.
- Ngành seller nếu nhận diện được: `xang-dau`, `an-uong`, `van-tai`, `khach-san`, `van-phong-pham`, `vien-thong`, `dien-nuoc`.
- Năm: `2024`, `2025`, `2026`.
- Thuế suất: `vat-10`, `vat-8`, `vat-5`, `vat-0`.

### Warnings

Populate `warnings[]` khi:
- OCR confidence thấp cho field quan trọng (MST, số hoá đơn, tổng tiền).
- Tổng line items không khớp với header total (chênh >1%).
- Số bằng chữ không khớp số bằng số.
- MST format không hợp lệ.
- Ngày hoá đơn > hôm nay (future date).
- Không tìm thấy trường bắt buộc (số HĐ, ngày, tổng tiền).

Ví dụ warning: `"MST bên bán '01234567' chỉ có 8 chữ số, không hợp lệ"`.

## Ví dụ output đúng

```json
{
  "voucher_type": "invoice_vat",
  "document_number": "00001234",
  "document_series": "1C25TAA",
  "document_date": "2025-11-15",
  "seller_name": "CÔNG TY TNHH THƯƠNG MẠI ABC",
  "seller_mst": "0301234567",
  "seller_address": "123 Nguyễn Trãi, Q.1, TP.HCM",
  "buyer_name": "CÔNG TY CỔ PHẦN XYZ",
  "buyer_mst": "0312345678",
  "buyer_address": "456 Lê Lợi, Q.3, TP.HCM",
  "payment_method": "CK",
  "subtotal": 10000000,
  "vat_rate": 10,
  "vat_amount": 1000000,
  "discount_amount": 0,
  "total_amount": 11000000,
  "currency": "VND",
  "amount_in_words": "Mười một triệu đồng chẵn",
  "line_items": [
    {
      "ordinal": 1,
      "item_code": "SP001",
      "description": "Máy in Canon LBP-2900",
      "unit": "cái",
      "quantity": 2,
      "unit_price": 5000000,
      "subtotal": 10000000,
      "vat_rate": 10,
      "vat_amount": 1000000,
      "total_amount": 11000000
    }
  ],
  "tags": ["hoa-don-vat", "van-phong-pham", "2025", "vat-10"],
  "warnings": []
}
```

## Khi không đủ thông tin

Trả JSON tối thiểu với warning:

```json
{
  "voucher_type": "invoice_vat",
  "document_number": null,
  "document_series": null,
  "document_date": null,
  "seller_name": null, "seller_mst": null, "seller_address": null,
  "buyer_name": null, "buyer_mst": null, "buyer_address": null,
  "payment_method": null,
  "subtotal": null, "vat_rate": null, "vat_amount": null,
  "discount_amount": null, "total_amount": null,
  "currency": "VND", "amount_in_words": null,
  "line_items": [],
  "tags": ["hoa-don-vat", "can-bo-sung"],
  "warnings": ["OCR không đủ chất lượng để trích xuất các trường bắt buộc"]
}
```

## Tránh

- Không thêm comment / giải thích ngoài JSON.
- Không bịa MST — null tốt hơn sai (MST sai sẽ mess up sổ).
- Không tính tự động vat_amount = subtotal × vat_rate nếu hoá đơn có ghi số VAT khác. Trust hoá đơn.
- Không cast quantity thành int nếu hoá đơn có float (VD 2.5 kg → giữ float).
- Không dịch tên sản phẩm sang tiếng Anh.
- Không lấy `null` string — dùng JSON `null`.
