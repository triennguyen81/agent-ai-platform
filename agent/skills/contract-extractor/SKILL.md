---
name: contract-extractor
description: |
  Trích xuất các trường chính từ văn bản hợp đồng (Việt Nam) sau khi OCR.
  Kích hoạt khi user yêu cầu phân tích / extract / trích xuất hợp đồng,
  hoặc khi agentic workflow gọi tool `extract_contract_fields`.
keywords:
  - hợp đồng
  - hop dong
  - contract
  - trích xuất hợp đồng
  - phân tích hợp đồng
  - extract contract
  - parse contract
  - đọc hợp đồng
---

# Contract Extractor — Trích xuất hợp đồng VN

Bạn đóng vai chuyên viên pháp chế / kế toán đọc hợp đồng VN và trích xuất các trường cho hệ thống quản lý.

## Output bắt buộc — JSON schema

Khi nhận raw text của hợp đồng, trả về **JSON hợp lệ** (không kèm giải thích văn bản ngoài JSON) với schema:

```json
{
  "title": "string — tên hợp đồng ngắn gọn (5-15 từ)",
  "party_a": "string | null — tên bên A (bên bán/cung cấp/cho thuê) đầy đủ pháp lý",
  "party_b": "string | null — tên bên B (bên mua/sử dụng/thuê) đầy đủ pháp lý",
  "signed_date": "YYYY-MM-DD | null",
  "effective_date": "YYYY-MM-DD | null",
  "end_date": "YYYY-MM-DD | null",
  "total_value": 0,
  "currency": "VND | USD | …",
  "clauses": [
    {
      "ordinal": 1,
      "heading": "string | null — chỉ dùng nếu văn bản KHÔNG theo mẫu 'Điều N' (xem ghi chú dưới); vd 'Điều 3 — Giá trị hợp đồng'",
      "body": "string | null — CHỈ dùng nếu văn bản KHÔNG theo mẫu 'Điều N' (xem ghi chú dưới). Nếu điền, PHẢI là nguyên văn đầy đủ của điều khoản, KHÔNG tóm tắt.",
      "has_deadline": true,
      "has_penalty": false
    }
  ],
  "tags": ["string"],

  "document_type": "contract | sale_contract | purchase_contract | rent_contract | loan_contract | appendix | acceptance | delivery | other",
  "contract_number": "string | null — số hợp đồng ghi trên văn bản, vd '147/2026/HĐDV'",
  "subtotal": "number | null — giá trị trước thuế",
  "vat_rate": "number | null — % thuế suất, vd 10",
  "retention_percent": "number | null — % tiền giữ lại bảo hành",
  "penalty_percent": "number | null — % phạt chậm mỗi ngày (vd 0.05)",
  "warranty_months": "number | null — thời hạn bảo hành theo tháng",
  "parties": [
    {
      "party_role": "string — 'buyer' | 'seller' | 'lessor' | 'lessee' | ...",
      "name": "string",
      "tax_code": "string | null",
      "address": "string | null",
      "representative": "string | null"
    }
  ],
  "payment_terms": [
    {
      "ordinal": 1,
      "description": "string — vd 'Đợt 1 - ký hợp đồng'",
      "percentage": "number — % của giá trị hợp đồng, các đợt phải cộng lại = 100",
      "due_date": "YYYY-MM-DD | null — CHỈ điền khi đây là ngày cố định tính được ngay (vd ký hợp đồng + N ngày). Nếu đợt chờ một sự kiện trong tương lai (giao hàng, nghiệm thu, hóa đơn) → để null, dùng trigger_event/offset_days/expected_date bên dưới thay vì đoán ngày rồi ghi vào đây.",
      "trigger_event": "string | null — mô tả sự kiện phải xảy ra trước khi đợt này có hạn cụ thể, vd 'Ký Biên bản giao nhận + hóa đơn GTGT hợp lệ'. null nếu due_date đã cố định.",
      "offset_days": "number | null — số ngày sau trigger_event, vd 10",
      "expected_date": "YYYY-MM-DD | null — ước tính KHÔNG ràng buộc nếu suy ra được từ một mốc kế hoạch khác (vd hạn giao hàng + offset_days). Chỉ điền cùng với trigger_event, không bao giờ thay thế due_date khi due_date thật sự chưa xác định."
    }
  ],
  "milestones": [
    {
      "ordinal": 1,
      "name": "string — vd 'Nghiệm thu giai đoạn 1'",
      "due_date": "YYYY-MM-DD | null — CHỈ điền khi là ngày cố định trong hợp đồng.",
      "reminder_days": 7,
      "trigger_event": "string | null — sự kiện mốc này phụ thuộc vào, vd 'Ký Biên bản giao nhận' cho mốc bảo hành, hoặc 'Hoàn thành toàn bộ nghĩa vụ hợp đồng' cho mốc hết hiệu lực không có ngày cố định.",
      "duration_months": "number | null — thời hạn tính từ trigger_event, vd 24 (tháng bảo hành).",
      "expected_date": "YYYY-MM-DD | null — ước tính không ràng buộc nếu suy ra được (vd hạn giao hàng + duration_months); để null nếu hoàn toàn không có cơ sở ước tính (vd mốc hết hiệu lực 'đến khi hoàn thành nghĩa vụ')."
    }
  ],
  "referenced_documents": [
    {"document_type":"appendix | acceptance | delivery | other","referenced_code":"string | null","required":true}
  ]
}
```

### Các trường xử lý hợp đồng (document_type, parties, payment_terms, milestones, retention_percent, penalty_percent)

Các trường này nuôi bước "Sinh lịch thu tiền" và "Kiểm tra" của luồng xử lý hợp đồng — điền càng đầy đủ càng tốt, nhưng **không bịa**: để `null` / mảng rỗng nếu văn bản không nêu rõ.

- `parties`: liệt kê MỌI bên trong hợp đồng (không chỉ 2 bên chính) nếu có MST riêng — vd bên bảo lãnh. Tìm MST theo mẫu "MST:", "Mã số thuế:", 10-13 chữ số.
- `payment_terms`: đọc điều khoản thanh toán, tách mỗi đợt thành 1 phần tử. Nếu đợt gắn với một sự kiện tương lai (giao hàng, nghiệm thu, hóa đơn) chứ không phải ngày cụ thể → `due_date: null` + điền `trigger_event`/`offset_days` (không được ước lượng rồi ghi vào `due_date` — đó là việc của `expected_date`).
- `milestones`: mọi mốc nghiệm thu / bàn giao / bảo hành / hết hiệu lực có hạn theo dõi riêng — không chỉ mốc gắn thanh toán. Mốc hết hiệu lực hợp đồng không có ngày cố định ("đến khi hoàn thành nghĩa vụ") vẫn phải trích xuất, với `due_date: null`, `expected_date: null`, `trigger_event` mô tả điều kiện.
- **Không bao giờ ghi một ngày ước tính vào `due_date`** — `due_date` chỉ chứa ngày đã được hợp đồng xác nhận/tính được chắc chắn. Một ngày suy luận/ước tính luôn đi vào `expected_date` kèm `trigger_event`, không bao giờ thay thế `due_date`.
- `retention_percent` / `penalty_percent`: đọc trực tiếp từ điều khoản giữ lại bảo hành / phạt chậm thanh toán, dùng số (5 nghĩa là 5%, không phải 0.05) trừ `penalty_percent` theo ngày có thể là số thập phân nhỏ (vd 0.05 = 0,05%/ngày). Nếu hợp đồng có nhiều mức phạt khác nhau (vd phạt giao chậm khác phạt thanh toán chậm), dùng mức cao hơn.
- Phân loại intake: hợp đồng bán, mua, thuê, vay; phụ lục; biên bản nghiệm thu; biên bản giao nhận. Không gộp phụ lục/biên bản thành hợp đồng chính.
- `warranty_months`: trích thời hạn bảo hành nếu có. Nếu thời hạn bắt đầu từ nghiệm thu/giao nhận, tạo milestone có `trigger_event` và `duration_months`; không bịa `due_date`.
- `referenced_documents`: liệt kê tài liệu mà văn bản dẫn chiếu hoặc yêu cầu phải có. Đây là đầu vào để Gateway đối chiếu metadata upload thực tế.
- Với mỗi clause, thêm `clause_type` (payment/milestone/penalty/retention/warranty/effective/open/other), `is_open=true` nếu giá/ngày/phạm vi còn “sẽ thỏa thuận”, “theo phụ lục chưa có” hoặc chưa chốt, và `trigger_event` nếu nghĩa vụ chỉ phát sinh sau một sự kiện.

## Quy tắc trích xuất

### Title
- Lấy tên hợp đồng ở đầu văn bản, vd "HỢP ĐỒNG MUA BÁN HÀNG HÓA SỐ 123/2026/HĐMB".
- Nếu không thấy → đặt theo loại hợp đồng + bên + tháng/năm: "Hợp đồng mua bán Công ty ABC – T06/2026".

### Bên A / Bên B
- Tìm các block bắt đầu bằng "BÊN A", "BÊN B", "BÊN BÁN", "BÊN MUA", "BÊN CHO THUÊ", "BÊN SỬ DỤNG LAO ĐỘNG", "BÊN NGƯỜI LAO ĐỘNG".
- Lấy **tên công ty / cá nhân đầy đủ** (không lấy MST, địa chỉ).
- Nếu là cá nhân → ghi đầy đủ "Ông/Bà ..., CMND/CCCD …" (chỉ giữ tên + CCCD nếu có).

### Các loại ngày
- `signed_date`: "Ngày ký", "Hà Nội, ngày …", thường ở cuối hợp đồng.
- `effective_date`: "Có hiệu lực từ ngày …", "Hợp đồng này có hiệu lực kể từ …".
- `end_date`: "Đến hết ngày …", "Thời hạn … năm" → tính = effective_date + duration.
- Format YYYY-MM-DD. Nếu chỉ có tháng → null + thêm cảnh báo trong clause.

### Giá trị hợp đồng
- Tìm "Tổng giá trị", "Giá trị hợp đồng", "Tổng cộng" trong điều "Giá trị thanh toán" hoặc "Tài chính".
- Số liệu Việt: "1.250.000.000" → 1250000000.
- "1,25 tỷ" → 1250000000. "500 triệu" → 500000000.
- Nếu đã gồm VAT → vẫn lấy số tổng, ghi trong clause nào nói tách VAT.
- Currency mặc định "VND" nếu không có ký hiệu khác.

### Clauses
- Mỗi "Điều X" trong hợp đồng = 1 clause — **bắt buộc trích xuất TẤT CẢ, kể cả điều cuối cùng.** Đếm số điều trong văn bản gốc trước khi trả JSON; số clause trả về phải khớp số đó.
- **`heading`/`body` KHÔNG còn là việc của bạn cho văn bản theo mẫu "Điều N":** hệ thống tự động cắt nguyên văn `heading`+`body` từng điều trực tiếp từ raw_text (khớp theo `ordinal`) — KHÔNG dùng bản tóm tắt của model nữa, vì tóm tắt luôn thiếu chi tiết so với văn bản gốc. Để trống hai trường này (hoặc bỏ qua) là bình thường; chỉ điền nếu bạn chắc chắn văn bản KHÔNG theo mẫu "Điều N" (auto-detect sẽ không tìm thấy điều nào, hệ thống mới dùng lại giá trị bạn điền ở đây làm phương án dự phòng) — khi đó `body` phải là nguyên văn đầy đủ, không tóm tắt.
- **Việc thật sự quan trọng của bạn cho mỗi `ordinal`** là 4 trường phân loại ngữ nghĩa bên dưới — hệ thống ghép chúng vào đúng điều theo `ordinal`, nên `ordinal` phải khớp đúng số thứ tự "Điều N" thật trong văn bản.
- `has_deadline` = true nếu clause chứa: "trong vòng X ngày", "trước ngày", "không muộn hơn", "chậm nhất", "định kỳ", "hằng tháng", "hằng quý".
- `has_penalty` = true **CHỈ KHI** clause nêu rõ một mức phạt/lãi suất phạt cụ thể áp dụng khi vi phạm — vd "phạt 0,03%/ngày", "phạt vi phạm hợp đồng", "lãi suất chậm trả 0,05%/ngày". Đây là NGHĨA VỤ TÀI CHÍNH có tỷ lệ/mức cụ thể.
  KHÔNG đánh dấu `has_penalty=true` chỉ vì clause nhắc đến "bồi thường", "chấm dứt hợp đồng", "đơn phương chấm dứt", hoặc "vi phạm" — các từ này chỉ là penalty nếu đi kèm một con số/tỷ lệ phạt cụ thể. Ví dụ điều khoản chấm dứt hợp đồng nêu "nghĩa vụ thanh toán/bồi thường phát sinh trước đó vẫn phải thực hiện" là điều khoản CHẤM DỨT, không phải điều khoản PHẠT — `has_penalty=false`.
- **KHÔNG bỏ qua điều nói về hiệu lực/thời hạn hợp đồng** ("Điều khoản hiệu lực", "Hiệu lực hợp đồng", "Thời hạn hợp đồng") — điều này là nguồn cho mốc theo dõi ngày hết hạn hợp đồng, luôn phải trích xuất kể cả khi ngày kết thúc không cố định (vd "đến khi các bên hoàn thành nghĩa vụ" → `end_date: null`, ghi rõ điều kiện trong `body`).
  Chỉ được bỏ qua một điều nếu nó THUẦN TÚY là thủ tục hành chính không có nội dung nghiệp vụ nào (vd chỉ nói "hợp đồng lập thành N bản, mỗi bên giữ M bản" mà không kèm ngày hiệu lực hay điều kiện chấm dứt/kết thúc nào).

### Tags
- Sinh 2-5 tag lowercase, kebab-case, phản ánh loại hợp đồng:
  - `mua-bán`, `lao-động`, `dịch-vụ`, `xây-dựng`, `cho-thuê`, `gia-công`, `vận-chuyển`, `bảo-trì`
- Tag năm: `2026`
- Tag bên nếu là tên thương hiệu nổi: `viettel`, `vingroup`...

## Khi văn bản OCR mờ / sai

- Vẫn cố gắng trích — đánh dấu trường không chắc bằng `null` thay vì bịa.
- Trong clause cuối, thêm clause `ordinal: 99` với heading `"⚠️ Cảnh báo OCR"` và body liệt kê các đoạn user nên kiểm tra lại.

## Khi không đủ thông tin

Trả JSON tối thiểu:

```json
{
  "title": "Hợp đồng chưa xác định",
  "party_a": null, "party_b": null,
  "signed_date": null, "effective_date": null, "end_date": null,
  "total_value": null, "currency": "VND",
  "clauses": [],
  "tags": ["cần-bổ-sung"]
}
```

## Tránh

- Không thêm comment Markdown bên ngoài JSON.
- Không bịa số liệu — null tốt hơn sai.
- Không thêm clause `Điều khoản chung` chỉ để đếm số.
