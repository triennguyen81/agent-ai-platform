# Use Case Specification
## UC-ACC-001: Xử lý chứng từ kế toán tự động bằng AI Agent (Hóa đơn bán hàng → Bút toán)

| Thuộc tính | Nội dung |
|---|---|
| **Mã Use Case** | UC-ACC-001 |
| **Tên Use Case** | Xử lý hóa đơn bán hàng và tự động hạch toán công nợ khách hàng |
| **Phiên bản** | 1.0 |
| **Ngày tạo** | 12/08/2026 |
| **Mức độ ưu tiên** | Cao |
| **Phạm vi** | Phân hệ Kế toán bán hàng & Công nợ phải thu (AR) |

---

## 1. Mô tả tổng quan (Brief Description)

Use case này mô tả quy trình AI Agent tự động xử lý hóa đơn bán hàng (file PDF/XML/ảnh scan) do người dùng tải lên hoặc nhận từ hệ thống hóa đơn điện tử. Agent thực hiện chuỗi hành động:

1. **Trích xuất** toàn bộ thông tin trên hóa đơn (OCR/parsing).
2. **Truy vấn** hồ sơ khách hàng và **toàn bộ công nợ hiện hành** của khách hàng đó (số dư TK 131, tuổi nợ, hạn mức tín dụng, lịch sử thanh toán).
3. **Truy vấn** thông tin bán hàng liên quan (đơn hàng, phiếu xuất kho, hợp đồng — nếu có).
4. **Đối chiếu 3 chiều** (3-way match): Hóa đơn ↔ Đơn hàng ↔ Phiếu giao/xuất kho.
5. **Đề xuất bút toán** hạch toán doanh thu và công nợ theo chuẩn kế toán Việt Nam (TT200/TT133).
6. **Trình duyệt** cho kế toán viên xác nhận trước khi ghi sổ (human-in-the-loop).

## 2. Tác nhân (Actors)

| Tác nhân | Loại | Vai trò |
|---|---|---|
| Kế toán viên (Accountant) | Chính | Tải chứng từ lên, xem xét và phê duyệt bút toán do Agent đề xuất |
| AI Accounting Agent | Hệ thống | Trích xuất dữ liệu, truy vấn công nợ, lập bút toán đề xuất |
| Kế toán trưởng (Chief Accountant) | Phụ | Phê duyệt các trường hợp vượt ngưỡng/ngoại lệ |
| Hệ thống ERP/Kế toán | Hệ thống | Cung cấp API tra cứu khách hàng, công nợ, đơn hàng; ghi nhận bút toán |
| Hệ thống HĐĐT (e-Invoice) | Hệ thống ngoài | Nguồn hóa đơn điện tử XML, xác thực với Tổng cục Thuế |

## 3. Điều kiện tiên quyết (Preconditions)

- PRE-1: Người dùng đã đăng nhập với quyền `Accounting.Voucher.Create`.
- PRE-2: Hóa đơn đầu vào ở định dạng hợp lệ: XML (HĐĐT chuẩn TT78/NĐ123), PDF, hoặc ảnh scan (JPG/PNG, ≥200 DPI).
- PRE-3: Hệ thống ERP đang hoạt động, các API sau khả dụng:
  - `GET /api/customers/{taxCode}` — hồ sơ khách hàng
  - `GET /api/customers/{id}/receivables` — công nợ chi tiết TK 131
  - `GET /api/sales-orders?customerId=&invoiceRef=` — đơn hàng/thông tin bán hàng
  - `POST /api/journal-entries/draft` — tạo bút toán nháp
- PRE-4: Danh mục tài khoản kế toán, thuế suất GTGT, kỳ kế toán mở đã được cấu hình.

## 4. Điều kiện sau (Postconditions)

**Thành công:**
- POST-1: Bút toán nháp (draft) được tạo, liên kết với chứng từ gốc, ở trạng thái `PendingApproval`.
- POST-2: Công nợ dự kiến của khách hàng được cập nhật cột "chờ ghi sổ".
- POST-3: Toàn bộ hành động của Agent được ghi audit log (trace từng bước reasoning + tool call).

**Thất bại:**
- POST-4: Không có bút toán nào được ghi sổ; chứng từ chuyển trạng thái `NeedsReview` kèm lý do.

## 5. Luồng sự kiện chính (Main Flow / Basic Flow)

| Bước | Tác nhân | Hành động |
|---|---|---|
| 1 | Kế toán viên | Tải hóa đơn bán hàng lên (hoặc hệ thống nhận tự động từ cổng HĐĐT) |
| 2 | AI Agent | **[Trích xuất]** Phân tích chứng từ, trích xuất: số hóa đơn, ký hiệu, ngày lập, MST người mua/bán, tên khách hàng, địa chỉ, danh sách hàng hóa/dịch vụ, số lượng, đơn giá, thành tiền, thuế suất GTGT, tiền thuế, tổng thanh toán, hình thức thanh toán, thời hạn thanh toán |
| 3 | AI Agent | **[Xác thực]** Kiểm tra tính hợp lệ: MST đúng định dạng, số học (thành tiền = SL × đơn giá, tổng = cộng tiền hàng + thuế), hóa đơn chưa tồn tại trong hệ thống (chống trùng) |
| 4 | AI Agent | **[Tra cứu khách hàng]** Gọi API tìm khách hàng theo MST. Lấy: mã KH, tên, nhóm KH, điều khoản thanh toán mặc định, hạn mức tín dụng |
| 5 | AI Agent | **[Lấy toàn bộ công nợ]** Gọi API công nợ, tổng hợp: số dư TK 131 hiện tại, chi tiết theo từng hóa đơn chưa thanh toán, tuổi nợ (aging 0–30/31–60/61–90/>90 ngày), nợ quá hạn, tỷ lệ sử dụng hạn mức tín dụng, lịch sử thanh toán 12 tháng |
| 6 | AI Agent | **[Lấy thông tin bán hàng]** Tìm đơn hàng/hợp đồng/phiếu xuất kho khớp với hóa đơn (theo số PO, mã KH, giá trị). Nếu có → thực hiện đối chiếu 3 chiều về mặt hàng, số lượng, đơn giá |
| 7 | AI Agent | **[Phân tích rủi ro]** Đánh giá: KH có vượt hạn mức tín dụng sau hóa đơn này không? Có nợ quá hạn >90 ngày không? → gắn cờ cảnh báo nếu có |
| 8 | AI Agent | **[Lập bút toán đề xuất]** Sinh bút toán theo TT200:<br>• Nợ TK 131 (chi tiết KH): Tổng thanh toán<br>• Có TK 511x: Doanh thu chưa thuế (chi tiết theo nhóm doanh thu)<br>• Có TK 3331: Thuế GTGT đầu ra<br>Kèm bút toán giá vốn nếu có phiếu xuất kho: Nợ TK 632 / Có TK 155, 156 |
| 9 | AI Agent | **[Trình bày]** Hiển thị cho kế toán viên: (a) dữ liệu trích xuất, (b) bảng tổng hợp công nợ KH trước & sau hóa đơn, (c) kết quả đối chiếu, (d) bút toán đề xuất, (e) cảnh báo rủi ro, (f) độ tin cậy (confidence score) từng trường |
| 10 | Kế toán viên | Xem xét, chỉnh sửa (nếu cần) và bấm **Phê duyệt** |
| 11 | Hệ thống ERP | Ghi sổ bút toán, cập nhật công nợ TK 131, đính kèm chứng từ gốc, khóa audit trail |
| 12 | AI Agent | Gửi thông báo hoàn tất; lưu feedback (các trường bị sửa) để cải thiện mô hình |

## 6. Luồng thay thế (Alternative Flows)

**A1 — Khách hàng chưa tồn tại trong hệ thống (rẽ nhánh tại bước 4)**
1. Agent không tìm thấy KH theo MST → tra cứu thông tin doanh nghiệp công khai theo MST để đối chiếu tên.
2. Agent đề xuất tạo hồ sơ KH mới (điền sẵn từ hóa đơn) → chờ kế toán viên xác nhận.
3. Sau khi tạo KH, quay lại bước 5 (công nợ = 0, chưa có lịch sử).

**A2 — Hóa đơn không có đơn hàng tham chiếu (bước 6)**
1. Không tìm thấy PO/hợp đồng khớp → Agent bỏ qua đối chiếu 3 chiều, ghi chú "Bán hàng không qua đơn hàng".
2. Tiếp tục bước 7, nhưng hạ mức tự tin và bắt buộc duyệt thủ công.

**A3 — Hóa đơn có chiết khấu thương mại / giảm giá**
1. Agent nhận diện dòng chiết khấu → bổ sung TK 5211/5213 vào bút toán (hoặc trừ trực tiếp doanh thu theo chính sách cấu hình).

**A4 — Hóa đơn ngoại tệ**
1. Agent lấy tỷ giá theo ngày hóa đơn (nguồn cấu hình: tỷ giá NHTM/BQLNH).
2. Bút toán ghi kép nguyên tệ + VND; ghi chú tỷ giá áp dụng.

**A5 — Kế toán viên chỉnh sửa bút toán (bước 10)**
1. Người dùng sửa tài khoản/số tiền → hệ thống kiểm tra cân đối Nợ = Có.
2. Ghi lại diff giữa đề xuất của Agent và bản duyệt cuối để đo chất lượng Agent.

## 7. Luồng ngoại lệ (Exception Flows)

| Mã | Điều kiện | Xử lý |
|---|---|---|
| E1 | OCR confidence < 85% ở trường trọng yếu (MST, tổng tiền) | Đánh dấu trường màu đỏ, yêu cầu người dùng nhập tay; không tự đề xuất bút toán |
| E2 | Hóa đơn trùng (số + ký hiệu + MST đã tồn tại) | Từ chối xử lý, hiển thị chứng từ gốc đã có, cảnh báo nghi vấn kê khai trùng |
| E3 | Sai số học trên hóa đơn (lệch > 1 đồng do làm tròn) | Cảnh báo, đề xuất giá trị đúng, yêu cầu xác nhận |
| E4 | KH vượt hạn mức tín dụng hoặc nợ quá hạn > ngưỡng | Bút toán chuyển luồng duyệt 2 cấp: Kế toán viên → Kế toán trưởng |
| E5 | API ERP timeout / lỗi | Retry 3 lần (exponential backoff); nếu vẫn lỗi → lưu hàng đợi, thông báo người dùng xử lý sau |
| E6 | Kỳ kế toán đã khóa sổ | Từ chối tạo bút toán vào kỳ đã khóa; đề xuất ghi vào kỳ hiện hành kèm ghi chú |
| E7 | Đối chiếu 3 chiều lệch (giá/số lượng khác đơn hàng) | Hiển thị bảng so sánh chênh lệch, yêu cầu quyết định thủ công |

## 8. Quy tắc nghiệp vụ (Business Rules)

- **BR-1:** Bút toán mặc định theo Thông tư 200/2014/TT-BTC (có thể cấu hình TT133 cho DN nhỏ và vừa):
  - Doanh thu bán hàng: `Nợ 131 / Có 511, Có 3331`
  - Giá vốn (nếu xuất kho): `Nợ 632 / Có 155, 156`
  - Chiết khấu thương mại: `Nợ 5211, Nợ 3331 / Có 131`
- **BR-2:** Agent **không bao giờ tự ghi sổ** — mọi bút toán phải qua phê duyệt của con người (human-in-the-loop).
- **BR-3:** Tổng Nợ = Tổng Có, sai lệch làm tròn tối đa 1 VND.
- **BR-4:** Thuế suất GTGT hợp lệ: 0%, 5%, 8%, 10%, KCT (không chịu thuế) — đối chiếu với danh mục hàng hóa.
- **BR-5:** Ngưỡng duyệt 2 cấp: hóa đơn > 500 triệu VND hoặc KH có nợ quá hạn > 90 ngày.
- **BR-6:** Mọi tool call của Agent phải được log: input, output, timestamp, latency — phục vụ audit.
- **BR-7:** Dữ liệu công nợ hiển thị cho Agent chỉ giới hạn trong phạm vi khách hàng liên quan đến chứng từ đang xử lý (nguyên tắc least privilege).

## 9. Yêu cầu dữ liệu (Data Requirements)

**9.1. Dữ liệu trích xuất từ hóa đơn:**
```
InvoiceNumber, InvoiceSerial, InvoiceDate, SellerTaxCode, BuyerTaxCode,
BuyerName, BuyerAddress, LineItems[{ItemName, Unit, Quantity, UnitPrice,
Amount, VatRate, VatAmount}], SubTotal, TotalVat, GrandTotal,
PaymentMethod, PaymentTerm, CurrencyCode, ExchangeRate
```

**9.2. Dữ liệu công nợ khách hàng (từ ERP):**
```
CustomerId, CustomerCode, CurrentBalance131, CreditLimit, CreditUsedPercent,
OpenInvoices[{InvoiceRef, DueDate, OriginalAmount, PaidAmount, Remaining}],
AgingBuckets{0-30, 31-60, 61-90, Over90}, OverdueAmount,
AvgDaysToPayLast12M, LastPaymentDate, PaymentTermDefault
```

**9.3. Dữ liệu bán hàng (nếu có):**
```
SalesOrderNo, ContractNo, DeliveryNoteNo, WarehouseIssueNo,
OrderedItems[], CogsAmount, SalesPersonId
```

**9.4. Bút toán đầu ra:**
```
JournalEntryDraft{ VoucherDate, VoucherNo, Description, Lines[{AccountCode,
SubAccount(CustomerCode), Debit, Credit, Description}], AttachmentRefs[],
CreatedByAgent, ConfidenceScore, RiskFlags[], Status=PendingApproval }
```

## 10. Yêu cầu phi chức năng (Non-functional Requirements)

| Mã | Yêu cầu | Chỉ tiêu |
|---|---|---|
| NFR-1 | Thời gian xử lý end-to-end 1 hóa đơn | ≤ 30 giây (P95) |
| NFR-2 | Độ chính xác trích xuất trường trọng yếu | ≥ 98% với XML, ≥ 95% với PDF, ≥ 90% với scan |
| NFR-3 | Audit trail | 100% tool call và reasoning step được log, lưu ≥ 10 năm |
| NFR-4 | Bảo mật | Mã hóa chứng từ at-rest (AES-256), phân quyền RBAC, không gửi dữ liệu KH ra ngoài phạm vi cho phép |
| NFR-5 | Khả năng xử lý đồng thời | ≥ 50 hóa đơn song song |

## 11. Sơ đồ luồng Agent (tham khảo kiến trúc)

```
[Hóa đơn upload/e-invoice webhook]
        │
        ▼
┌─────────────────────┐
│  Agent Orchestrator │
└─────────────────────┘
   │ Tool 1: extract_invoice(file)        → OCR/XML parser
   │ Tool 2: validate_invoice(data)       → rules engine
   │ Tool 3: get_customer(taxCode)        → ERP API
   │ Tool 4: get_receivables(customerId)  → ERP API (toàn bộ công nợ)
   │ Tool 5: get_sales_info(customerId)   → ERP API (đơn hàng/PXK)
   │ Tool 6: three_way_match(inv, po, dn) → matching engine
   │ Tool 7: propose_journal_entry(...)   → posting rules (TT200)
   │ Tool 8: create_draft_entry(...)      → ERP API (draft only)
        │
        ▼
[UI Review: Kế toán viên duyệt] ──► [Ghi sổ] ──► [Audit log + feedback]
```

## 12. Tiêu chí nghiệm thu (Acceptance Criteria)

- [ ] AC-1: Upload hóa đơn XML chuẩn TT78 → bút toán nháp đúng tài khoản, đúng số tiền trong ≤ 30s.
- [ ] AC-2: Màn hình review hiển thị đầy đủ: công nợ hiện tại, aging, hạn mức, bút toán đề xuất.
- [ ] AC-3: Hóa đơn trùng bị từ chối với thông báo rõ ràng (E2).
- [ ] AC-4: KH vượt hạn mức → luồng duyệt 2 cấp được kích hoạt (E4).
- [ ] AC-5: Không tồn tại bút toán nào được ghi sổ mà không có phê duyệt của con người (BR-2).
- [ ] AC-6: Audit log tái hiện được đầy đủ chuỗi hành động của Agent cho 1 chứng từ bất kỳ.
