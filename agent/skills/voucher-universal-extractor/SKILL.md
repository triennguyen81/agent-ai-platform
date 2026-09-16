---
name: voucher-universal-extractor
description: |
  Meta-skill trích xuất fields từ mọi loại chứng từ VN — hoá đơn VAT bán/mua,
  phiếu thu/chi, uỷ nhiệm chi, sao kê ngân hàng, hoá đơn expense cá nhân,
  hợp đồng dịch vụ. Tự phát hiện loại chứng từ từ raw OCR text, chọn schema
  extraction phù hợp, xử lý số VN (dấu chấm/phẩy, đơn vị đồng/nghìn/triệu),
  chuẩn hoá tên bên mua/bán khớp master data. Kích hoạt khi user upload
  chứng từ hoặc yêu cầu trích thông tin từ document.
keywords:
  - trích chứng từ
  - trích xuất
  - extract voucher
  - OCR chứng từ
  - hoá đơn
  - phiếu thu
  - phiếu chi
  - uỷ nhiệm chi
  - sao kê
  - expense claim
  - hoàn ứng
  - hoàn công tác phí
  - reimburse
  - biên nhận
  - biên lai
  - invoice
  - receipt
  - document extraction
---

# Voucher Universal Extractor

## Nhiệm vụ

Từ raw OCR text của 1 chứng từ VN bất kỳ, trả về:
1. **`voucher_type`** — 1 trong 8 loại (xem bảng bên dưới)
2. **`fields`** — object các trường theo schema tương ứng
3. **`confidence`** — 0.0-1.0 mức tin cậy tổng thể

## 8 loại chứng từ + schema

### 1. `invoice_vat_out` — Hoá đơn VAT bán ra (đầu ra)
Công ty **mình là bên bán**. Buyer_mst khác MST của mình.

```json
{
  "voucher_type": "invoice_vat_out",
  "document_number": "00001234",
  "document_series": "AB/26E",
  "document_date": "2026-08-01",
  "seller_name": "Công ty ABC (mình)",
  "seller_mst": "0301234567",
  "buyer_name": "CTCP Khách hàng XYZ",
  "buyer_mst": "0100234567",
  "buyer_address": "...",
  "buyer_customer_code": null,        // sẽ enrich từ master data
  "payment_method": "CK",             // CK / TM / MIXED
  "payment_terms_days": 30,           // Net30, 0 nếu trả ngay
  "subtotal": 100000000,
  "vat_rate": 10,
  "vat_amount": 10000000,
  "total_amount": 110000000,
  "currency": "VND",
  "line_items": [{
    "ordinal": 1,
    "description": "Ghế nha khoa KaVo E80",
    "unit": "cái",
    "quantity": 1,
    "unit_price": 100000000,
    "subtotal": 100000000,
    "vat_rate": 10,
    "vat_amount": 10000000,
    "total_amount": 110000000
  }],
  "tags": ["ban-hang", "khach-lon"]
}
```

**Bút toán mặc định (agent sẽ dùng skill `journal-entry-formatter`):**
```
Nợ 131 (Khách XYZ)  110tr
    Có 5111         100tr
    Có 33311         10tr
Nợ 632  (giá vốn)   VVV
    Có 156          VVV
```

### 2. `invoice_vat_in` — Hoá đơn VAT mua vào (đầu vào)
Công ty **mình là bên mua**. Seller_mst khác MST của mình.

Schema giống #1 nhưng đảo `seller` ↔ `buyer`, thêm:
```json
{
  "voucher_type": "invoice_vat_in",
  "supplier_code": null,              // enrich từ master
  "expense_category": "material",     // material/office/travel/marketing/service
  ...
}
```

**Bút toán mặc định:**
```
Nợ 156/152/641/642  (theo expense_category)
Nợ 1331  VAT khấu trừ
    Có 331 (NCC)   nếu trả sau
    Có 111/112     nếu trả ngay
```

### 3. `receipt` — Phiếu thu (thu tiền vào quỹ)

```json
{
  "voucher_type": "receipt",
  "document_number": "PT-2026-08-001",
  "document_date": "2026-08-01",
  "payer_name": "Khách hàng ABC",
  "payer_type": "customer",           // customer / employee / other
  "reason": "Thanh toán HĐ 00001234",
  "linked_invoice_number": "00001234", // nếu có
  "amount": 110000000,
  "method": "TM",                     // TM / CK
  "cashier_name": "Nguyễn Văn A",
  "approver_name": "Trần Thị B"
}
```

**Bút toán:**
```
Nợ 111/112   amount
    Có 131 (nếu thu công nợ) hoặc 511/711 (nếu thu khác)
```

### 4. `payment` — Phiếu chi (chi tiền ra khỏi quỹ)

Schema giống #3 nhưng `payer_name` → `payee_name`, `payer_type` → `payee_type`.

**Bút toán:**
```
Nợ 331/641/642/141  (theo lý do chi)
    Có 111/112       amount
```

### 5. `bank_transfer` — Uỷ nhiệm chi (thanh toán qua NH)

```json
{
  "voucher_type": "bank_transfer",
  "document_number": "UNC-2026-08-005",
  "document_date": "2026-08-01",
  "from_account": "0011000123456 VCB",
  "to_account": "0301000987654 BIDV",
  "beneficiary_name": "CTCP NCC XYZ",
  "beneficiary_mst": "0100234567",
  "amount": 100000000,
  "currency": "VND",
  "content": "Thanh toan HD 00005678 thang 8",
  "fee": 22000,
  "fee_bearer": "sender"              // sender/receiver
}
```

**Bút toán:**
```
Nợ 331 (NCC)   amount
    Có 112     amount
Nợ 6425 (phí NH)  fee (nếu sender chịu)
    Có 112        fee
```

### 6. `bank_statement` — Sao kê ngân hàng (N dòng giao dịch)

```json
{
  "voucher_type": "bank_statement",
  "account_number": "0011000123456",
  "bank_name": "VCB",
  "period_from": "2026-08-01",
  "period_to": "2026-08-31",
  "opening_balance": 5000000000,
  "closing_balance": 5230000000,
  "transactions": [
    {
      "date": "2026-08-02",
      "description": "TT HD 00001234 KH ABC",
      "debit": 0,
      "credit": 110000000,
      "balance": 5110000000,
      "counterparty_name": "CTCP Khach hang ABC",
      "counterparty_account": "0301000111222",
      "linked_invoice": "00001234",
      "suggested_journal": "N112 / C131"
    }
  ]
}
```

**Bút toán:** 1 bút toán cho mỗi dòng transaction, dựa trên description parsing.

### 7. `expense_claim` — Đề nghị hoàn ứng của nhân viên

```json
{
  "voucher_type": "expense_claim",
  "document_number": "HC-2026-08-023",
  "document_date": "2026-08-05",
  "employee_name": "Trần Văn Đức",
  "employee_code": "NV-0042",         // enrich từ master
  "department": "Kinh doanh",
  "purpose": "Công tác Hà Nội gặp khách hàng XYZ",
  "trip_from": "2026-08-01",
  "trip_to": "2026-08-04",
  "items": [
    {
      "category": "airfare",           // airfare/hotel/meal/taxi/gift/other
      "description": "Vé máy bay SGN-HAN khứ hồi VietJet",
      "amount": 3500000,
      "receipt_attached": true,
      "receipt_type": "invoice_vat",
      "vendor_mst": "0100109106",      // optional — MST hoá đơn dòng này (nếu có), dùng để chống trùng + khớp NCC
      "invoice_no": "0012345",         // optional — số hoá đơn dòng này (nếu có)
      "document_date": "2026-08-01",   // optional — ngày hoá đơn dòng này (YYYY-MM-DD, nếu có)
      "vat_amount": 318182             // optional — phần VAT trong amount (nếu hoá đơn có tách VAT); để trống nếu không đọc được, KHÔNG tự suy ra bằng % cố định
    },
    {
      "category": "hotel",
      "description": "Khách sạn Melia 3 đêm",
      "amount": 5400000,
      "receipt_attached": true,
      "receipt_type": "invoice_vat",
      "vendor_mst": "0100234567",
      "invoice_no": "0007788",
      "document_date": "2026-08-02",
      "vat_amount": 490909
    }
  ],
  "total_claim": 8900000,
  "advance_taken": 5000000,           // tiền tạm ứng trước (TK 141)
  "reimbursement_needed": 3900000      // total - advance
}
```

**Bút toán:**
```
Nợ 6417/6428 (chi tiết theo category) 8.900.000
    Có 141 (tạm ứng NV)               5.000.000
    Có 111/112 (hoàn tiền)             3.900.000
```

### 8. `contract` — Hợp đồng (không phải chứng từ hạch toán, nhưng lấy điều khoản kế toán)

```json
{
  "voucher_type": "contract",
  "contract_number": "HD-2026-045",
  "contract_date": "2026-01-15",
  "party_a": "Công ty mình",
  "party_b": "CTCP Khách hàng XYZ",
  "party_b_mst": "0100234567",
  "contract_value": 1200000000,
  "currency": "VND",
  "payment_schedule": [
    {"milestone": "Ký hợp đồng", "percentage": 30, "amount": 360000000, "expected_date": "2026-01-20"},
    {"milestone": "Giao hàng 50%", "percentage": 40, "amount": 480000000, "expected_date": "2026-03-01"},
    {"milestone": "Nghiệm thu", "percentage": 30, "amount": 360000000, "expected_date": "2026-06-01"}
  ],
  "accounting_notes": {
    "revenue_recognition": "Theo tiến độ (percentage-of-completion)",
    "vat_rate": 5,
    "credit_terms_days": 30,
    "penalty_late_payment": "0.05% / ngày quá hạn",
    "warranty_period_months": 24
  }
}
```

**Không có bút toán riêng** — dùng để **enrich context** cho các invoice/receipt phát sinh sau này. Khi có invoice bán cho party_b + tương ứng milestone → tự flag "match với HĐ HD-2026-045 milestone 2".

## Quy trình phân loại

Agent xử lý raw OCR text theo thứ tự:

1. **Detect voucher_type** bằng keyword + layout hints:
   - Có "HOÁ ĐƠN GIÁ TRỊ GIA TĂNG" hoặc "GTGT" → invoice_vat_*
   - Có "PHIẾU THU" → receipt
   - Có "PHIẾU CHI" → payment
   - Có "UỶ NHIỆM CHI" hoặc "PAYMENT ORDER" → bank_transfer
   - Có "SAO KÊ" hoặc bảng nhiều dòng dr/cr → bank_statement
   - Có "ĐỀ NGHỊ HOÀN ỨNG" hoặc "EXPENSE CLAIM" → expense_claim
   - Có "HỢP ĐỒNG" + "SỐ" + "ĐIỀU" → contract
   - Nếu không rõ → trả voucher_type="unknown" + confidence=0.3

2. **Với invoice_vat**: nếu `seller_mst` = MST công ty mình → invoice_vat_out, ngược lại → invoice_vat_in. Nếu chưa biết MST công ty → hỏi lại hoặc dùng default.

3. **Extract fields** theo schema trên. Với số tiền:
   - Chuẩn hoá "1.234.567,89" (VN) → 1234567.89
   - "1,234,567.89" (US) → 1234567.89
   - "1.5 triệu" → 1500000
   - Suy ra `vat_amount` từ `subtotal × vat_rate/100` nếu thiếu

4. **Enrich từ master data (nếu có `master_context` trong prompt)**:
   - Match `buyer_mst`/`seller_mst`/`beneficiary_mst` với customer/supplier code
   - Match `employee_name` với employee_code
   - Match `linked_invoice_number` với voucher đã lưu
   - Match `contract_number` với contract đã lưu → lấy `credit_terms_days`, `payment_schedule`

5. **Đánh confidence**:
   - Tất cả required fields có → 0.9+
   - Thiếu ≤ 2 fields nhỏ → 0.7-0.9
   - Thiếu nhiều hoặc voucher_type không rõ → < 0.5

## Output format

**BẮT BUỘC** trả về 2 fenced JSON blocks liên tiếp, không kèm giải thích:

```json
{ "voucher_type": "...", "fields": { ... }, "confidence": 0.92 }
```

```json
[ { "entry_no": 1, "debit_account": "131", "credit_account": "5111", "amount": 100000000, "description": "...", "confidence": 0.9 } ]
```

Nếu voucher_type = "contract" → block 2 là `[]` (không phát sinh bút toán).

Nếu voucher_type = "unknown" → block 1 chỉ có `voucher_type` + `confidence`, không cần fields đầy đủ.
