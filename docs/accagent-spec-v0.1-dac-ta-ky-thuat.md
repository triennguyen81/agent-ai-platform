# AccAgent · Nhóm xử lý chứng từ — Đặc Tả Kỹ Thuật

> **Phiên bản:** v0.1 — bản thảo
> **Nguồn:** Nhận từ user 2026-09-01, chưa merge vào implementation.
> **Đối tượng:** Đội phát triển.
> **Tài liệu song hành:** `accagent-spec-v0.1-quy-trinh-nghiep-vu.md`
> **Trạng thái so với implementation hiện tại:** xem `plans/whimsical-weaving-dahl.md` (Gap Analysis).

Ba luồng end-to-end: hợp đồng, phiếu thu công nợ, hoá đơn thanh toán. Agent dừng ở mức đề xuất — phần mềm kế toán vẫn là hệ thống ghi nhận chính thức.

**Phạm vi:** 3 usecase · 8 nguồn tích hợp

---

## Mục lục

1. Nguyên tắc kiến trúc
2. Kiến trúc hệ thống
3. Nguồn tích hợp
4. Mô hình dữ liệu
5. UC-01 Hợp đồng
6. UC-02 Phiếu thu
7. UC-03 Expense claim
8. Thuật toán ghép
9. Rule engine
10. Đề xuất bút toán
11. Trạng thái
12. Lỗi & hàng đợi
13. Bảo mật & audit
14. Yêu cầu phi chức năng
15. Thứ tự xây dựng

---

## 01 · Nguyên tắc kiến trúc

**Ràng buộc số một:** Agent không bao giờ ghi vào phần mềm kế toán. Đầu ra cuối cùng của mọi luồng là một đề xuất đã được người duyệt, kèm file import theo mẫu. Việc đưa số liệu vào sổ do kế toán viên thực hiện trên phần mềm. Không thiết kế API ghi, không tự động post.

- **Đọc rộng, ghi hẹp.** Hệ thống đọc từ nhiều nguồn (cổng thuế, ngân hàng, export phần mềm kế toán, HR) nhưng chỉ ghi vào cơ sở dữ liệu của chính nó.
- **Mọi đề xuất phải truy vết được.** Từ một dòng bút toán đề xuất phải đi ngược về được file chứng từ gốc, phiên bản trích xuất, và rule nào đã chạy.
- **Không có quyết định im lặng.** Mỗi lần agent chọn một khả năng, nó ghi lại độ tin cậy và căn cứ. Dưới ngưỡng thì đẩy sang người, không đoán.
- **Idempotent theo chứng từ.** Cùng một chứng từ nạp lại nhiều lần phải cho ra cùng một bản ghi, không nhân bản. Khoá tự nhiên là hash nội dung file cộng khoá nghiệp vụ.
- **Multi-entity từ đầu.** Mọi bảng mang org_id kể cả khi giai đoạn đầu chỉ chạy một pháp nhân.
- **Nguồn tin cậy hơn thắng.** XML hoá đơn điện tử luôn ghi đè kết quả OCR của cùng hoá đơn đó, không cần hỏi người dùng.

---

## 02 · Kiến trúc hệ thống

| Layer | Nội dung |
|---|---|
| **L6 Giao diện** | Web app · Zalo OA bot · Email inbound · Mobile claim |
| **L5 API Gateway** | Auth OIDC · RBAC · rate limit · webhook receiver |
| **L4 Orchestrator** | Định tuyến usecase · quản lý state machine · retry · escalation |
| **L3 Agent Runtime** | Contract Agent · Receivable Agent · Expense Agent · Extraction Service · Rule Engine · Matching Engine |
| **L2 Model & tri thức** | LLM trích xuất · OCR tiếng Việt · Embedding & vector store · Bộ nhớ định khoản |
| **L1 Dữ liệu & hạ tầng** | PostgreSQL · Object store chứng từ · Redis · Queue · Audit log append-only |
| **L0 Kết nối ngoài** | Cổng HĐĐT · Ngân hàng · Export phần mềm kế toán · HR · Email/Zalo |

### 3 agent nghiệp vụ

| Agent | Usecase | Đầu ra | Phụ thuộc |
|---|---|---|---|
| Contract Agent | UC-01 | Bản ghi hợp đồng, lịch thanh toán, mốc theo dõi | Extraction, cổng thuế, danh mục đối tác |
| Receivable Agent | UC-02 | Kết quả ghép, đề xuất bút toán thu, tuổi nợ | Matching Engine, Contract Agent, sao kê |
| Expense Agent | UC-03 | Bảng kê claim, đề xuất bút toán chi, cờ policy | Rule Engine, cổng thuế, HR, tạm ứng |

**3 service dùng chung:**
- Extraction Service (định tuyến XML / PDF / OCR / LLM)
- Rule Engine (validation và policy, cấu hình bằng dữ liệu chứ không hard-code)
- Journal Proposer (sinh bút toán đề xuất từ sự kiện nghiệp vụ đã duyệt)

---

## 03 · Nguồn tích hợp

| Mã | Nguồn | Phương thức | Chiều | Tần suất | Ghi chú kỹ thuật |
|---|---|---|---|---|---|
| S1 | Phần mềm kế toán | Export file (Excel/CSV) đặt vào thư mục hoặc upload | Đọc | Hằng ngày + on-demand | Không dùng API ghi. Cần chuẩn hoá mapping cột theo từng phần mềm |
| S2 | Cổng hoá đơn điện tử | Tài khoản tra cứu, tải XML/bảng kê theo kỳ | Đọc | Ngày 2 lần + on-demand | Rate limit thận trọng, cache theo mã hoá đơn, lưu XML gốc |
| S3 | Ngân hàng | File sao kê hoặc API nếu ngân hàng hỗ trợ | Đọc | Hằng ngày | Parser riêng cho từng ngân hàng, versioned. Ưu tiên Excel hơn PDF |
| S4 | Kho hợp đồng | Nội bộ hệ thống | Đọc/ghi | Realtime | Nguồn sự thật cho lịch thanh toán và mốc |
| S5 | Danh mục nội bộ | Đồng bộ từ S1 + nhập tay | Đọc/ghi | Hằng ngày | Đối tác, dự án, trung tâm chi phí, hệ thống tài khoản |
| S6 | Chính sách chi tiêu | Cấu hình trong hệ thống | Đọc/ghi | Realtime | Bảng rule có hiệu lực theo thời gian, có phiên bản |
| S7 | HR | API hoặc file đồng bộ | Đọc | Hằng ngày | Nhân viên, cấp bậc, bộ phận, trạng thái làm việc |
| S8 | Email / Zalo OA | IMAP inbound, webhook Zalo | Đọc/ghi | Realtime | Vừa là kênh nhận chứng từ vừa là kênh phát thông báo |

**Rủi ro tích hợp cần chốt sớm:** S2 và S3 quyết định phần lớn tiến độ. Nếu tài khoản tra cứu hoá đơn điện tử không có sẵn cho từng pháp nhân, toàn bộ nhánh xác thực hoá đơn của UC-03 phải chuyển sang dựa vào XML do nhà cung cấp gửi qua email — độ phủ thấp hơn nhiều.

---

## 04 · Mô hình dữ liệu

### Thực thể lõi

| Thực thể | Khoá tự nhiên | Trường chính | Quan hệ |
|---|---|---|---|
| `document` | org_id + content_hash | kênh nhận, loại, file gốc, trạng thái xử lý, thời điểm nhận | 1–n extraction |
| `extraction` | document_id + version | engine (xml/pdf/ocr/llm), payload JSON, confidence từng trường | n–1 document |
| `partner` | org_id + tax_code | tên, MST, địa chỉ, trạng thái hoạt động, mã đối tượng bên S1, điều khoản mặc định | 1–n hợp đồng, giao dịch |
| `contract` | org_id + contract_no | loại, hai bên, giá trị, thuế suất, tiền tệ, hiệu lực, retention, phạt, trạng thái, tiến độ | 1–n schedule, milestone, amendment |
| `contract_schedule` | contract_id + seq | mốc kích hoạt, tỷ lệ, số tiền, ngày dự kiến, số tiền đã thu, trạng thái | n–1 contract |
| `contract_milestone` | contract_id + code | tên mốc, hạn, trạng thái, chứng từ nghiệm thu | n–1 contract |
| `amendment` | contract_id + seq | loại thay đổi, delta giá trị, hiệu lực, ảnh hưởng schedule | n–1 contract |
| `invoice` | org_id + invoice_serial + invoice_no | chiều (ra/vào), đối tác, ngày, trước thuế, thuế, tổng, trạng thái cổng thuế, xml gốc | n–1 contract, 1–n allocation |
| `cash_txn` | org_id + bank_ref hoặc receipt_no | ngày, chiều, số tiền, tiền tệ, tài khoản, nội dung thô, đối tác suy luận | 1–n allocation |
| `allocation` | cash_txn_id + invoice_id | số tiền phân bổ, schedule_id, độ tin cậy, người xác nhận | bảng nối n–n |
| `advance` | org_id + advance_no | nhân viên, số tiền, dự án, hạn hoàn ứng, số đã quyết toán | 1–n claim |
| `expense_claim` | org_id + claim_no | người nộp, mục đích, kỳ, tổng đề nghị, tổng duyệt, trạng thái | 1–n claim_line |
| `claim_line` | claim_id + seq | invoice_id, tài khoản chi phí, cost center, dự án, trước thuế, thuế, kết quả rule | n–1 claim |
| `journal_proposal` | source_event_id | ngày, diễn giải, dòng nợ/có, tham chiếu chứng từ, trạng thái duyệt, người duyệt | 1–n journal_line |
| `rule_result` | subject_ref + rule_code | mức (pass/warn/block), thông điệp, dữ liệu đối chiếu | gắn vào mọi thực thể |
| `exception_item` | id | loại, đối tượng liên quan, lý do, người được giao, SLA, trạng thái | hàng đợi xử lý tay |
| `notification` | id | sự kiện, người nhận, kênh, nội dung, trạng thái gửi | — |
| `audit_event` | id | actor, hành động, đối tượng, trước/sau, thời điểm | append-only |

### Quy ước quan trọng

Số tiền lưu bằng số nguyên đơn vị nhỏ nhất của tiền tệ, kèm cột `currency` và `fx_rate`. **Không dùng kiểu float** ở bất kỳ đâu. Mọi bản ghi trích xuất giữ nguyên payload gốc, không ghi đè — chỉnh sửa của người dùng tạo phiên bản mới.

---

## 05 · UC-01 Hợp đồng

Luồng 8 bước:
1. **Ingest** — Email · upload · Zalo · thư mục
2. **Phân loại** — Bán / mua / thuê / vay / phụ lục
3. **Trích xuất** — LLM văn bản tự do, 24 trường
4. **Đối chiếu** — S2 MST · S5 đối tác · S4 trùng
5. **Sinh lịch** — Diễn giải điều khoản thanh toán
6. **Rule check** — So mẫu chuẩn, phát hiện thiếu
7. **Người duyệt** — Kế toán công nợ xác nhận
8. **Bàn giao** — Mã đối tượng · mã HĐ · lịch công nợ

### Payload trích xuất — hợp đồng bán ra

```json
{
  "doc_type": "contract_sales",
  "contract_no": "147/2026/HĐDV",
  "signed_date": "2026-08-15",
  "effective_from": "2026-08-15",
  "effective_to": "2026-12-31",
  "seller": { "name": "...", "tax_code": "0300000000" },
  "buyer":  { "name": "Công ty CP ABC", "tax_code": "0301234567",
              "rep": "Nguyễn Văn A", "title": "Giám đốc" },
  "amount_net": 1800000000, "vat_rate": 0.10,
  "amount_gross": 1980000000, "currency": "VND",
  "payment_terms": [
    { "seq":1, "trigger":"contract_signed",  "pct":0.30, "credit_days":30 },
    { "seq":2, "trigger":"acceptance_phase1", "pct":0.40, "credit_days":30 },
    { "seq":3, "trigger":"final_handover",    "pct":0.30, "credit_days":30 }
  ],
  "retention": { "pct":0.05, "release":"end_of_warranty", "months":12 },
  "late_penalty_daily": 0.0005,
  "acceptance_required": true,
  "_confidence": { "payment_terms":0.88, "retention":0.71, "late_penalty_daily":0.94 }
}
```

### Sinh lịch thanh toán

Mỗi phần tử `payment_terms` tạo một dòng `contract_schedule`. Dòng có `trigger = contract_signed` tính được ngày dự kiến ngay (ngày ký + credit_days). Dòng gắn mốc nghiệm thu để trạng thái `awaiting_milestone`, ngày dự kiến để trống cho tới khi mốc được ghi nhận.

### Rule áp dụng

| Mã rule | Kiểm tra | Mức |
|---|---|---|
| CT.DUP | Trùng số hợp đồng trong cùng pháp nhân | Chặn |
| CT.TAX | MST đối tác không tồn tại hoặc ngừng hoạt động | Chặn |
| CT.SUM | Tổng tỷ lệ các đợt thanh toán khác 100% | Chặn |
| CT.VAT | Giá trị sau thuế lệch giá trị trước thuế nhân thuế suất quá 1.000đ | Cảnh báo |
| CT.TPL | Điều khoản lệch mẫu chuẩn (phạt, bảo hành, retention) | Cảnh báo |
| CT.REF | Hợp đồng dẫn chiếu phụ lục chưa nhận được | Cảnh báo |
| CT.CONF | Độ tin cậy trường bất kỳ dưới 0,75 | Cảnh báo |
| CT.NEW | Đối tác chưa có trong danh mục | Thông tin |

**Không sinh bút toán:** Ký hợp đồng chưa phát sinh nghiệp vụ ghi sổ. UC-01 kết thúc bằng gói bàn giao dữ liệu quản trị, không gọi Journal Proposer. Ngoại lệ duy nhất là nhánh nghiệm thu (xem §10) — đó là sự kiện riêng.

---

## 06 · UC-02 Phiếu thu công nợ

Luồng 8 bước:
1. **Ingest** — Sao kê · phiếu thu · giấy báo Có
2. **Chuẩn hoá** — Parser theo ngân hàng
3. **Nhận diện** — Đối tác từ nội dung chuyển khoản
4. **Nạp bối cảnh** — Công nợ mở · lịch hợp đồng
5. **Ghép** — Sinh phương án, chấm điểm
6. **Phân nhánh** — Đủ / một phần / thừa / bất định
7. **Người duyệt** — Chỉ soát dòng điểm thấp
8. **Đầu ra** — Bút toán hoặc hành động tiếp

### Kết quả ghép

```json
{
  "txn_id": "GD-20260901-0034",
  "value_date": "2026-09-01", "amount": 594000000, "direction": "credit",
  "bank_account": "VCB 0071000xxxxxx",
  "raw_narrative": "CTY CP ABC TT DOT 1 HD 147/2026",
  "resolved_partner": { "id":"KH-0892", "score":0.97, "evidence":["name_token","contract_no"] },
  "allocations": [
    { "invoice_no":"00001234", "schedule_seq":1, "amount":594000000, "score":0.96 }
  ],
  "settlement_status": "paid_in_full",
  "residual": 0,
  "next_action": null
}
```

### 8 kết cục — khớp đúng một nhánh

| `settlement_status` | Điều kiện | Journal Proposer | `next_action` |
|---|---|---|---|
| `paid_in_full` | Tổng phân bổ = số tiền = dư nợ hoá đơn | Có | Đóng đợt, cập nhật tiến độ hợp đồng |
| `paid_partial` | Phân bổ < dư nợ hoá đơn | Có phần đã thu | Giữ đợt mở, đặt lịch nhắc, tính phạt nếu quá hạn |
| `overpaid` | Số tiền > tổng dư nợ khớp được | Có + treo phần thừa | Hỏi hoàn trả hay cấn trừ đợt sau |
| `prepayment` | Khớp đợt hợp đồng nhưng chưa có hoá đơn | Có dạng trả trước | Yêu cầu xuất hoá đơn |
| `partner_unknown` | Điểm nhận diện đối tác < ngưỡng | Không | Hàng đợi tra soát, yêu cầu tra soát ngân hàng |
| `no_match` | Có đối tác nhưng không khớp hoá đơn/đợt nào | Không | Hỏi kinh doanh xác định bản chất khoản thu |
| `reversed` | Giao dịch bị hoàn sau khi báo Có | Không | Đánh dấu huỷ, giữ nguyên công nợ, cảnh báo |
| `stale_unresolved` | Treo quá ngưỡng ngày cấu hình | Treo phải trả khác | Leo thang kế toán trưởng |

---

## 07 · UC-03 Hoá đơn thanh toán

Luồng 10 bước:
1. **Ingest** — Ảnh gộp · email · Zalo
2. **Tách** — Cắt từng chứng từ khỏi ảnh gộp
3. **Phân loại** — GTGT · bán lẻ · vé · biên lai
4. **Trích xuất** — XML > PDF > OCR
5. **Xác thực** — Cổng thuế: thật, trạng thái, bên mua
6. **Chống trùng** — Đã claim hoặc đã ghi sổ chưa
7. **Policy** — Định mức · cấp bậc · thời hạn
8. **Phân bổ** — Tài khoản · cost center · dự án
9. **Tạm ứng** — Đối chiếu, tính chênh lệch
10. **Định tuyến duyệt** — Bộ phận → kế toán → chi

### Bước 02 — tách ảnh gộp (dễ bị đánh giá thấp)

Người dùng chụp 5 hoá đơn xếp chồng trên mặt bàn, hoặc dán 3 hoá đơn vào một tờ A4 rồi chụp. Quy trình: phát hiện vùng chứng từ bằng phân đoạn ảnh, cắt và nắn phối cảnh từng vùng, chấm điểm chất lượng, vùng dưới ngưỡng thì yêu cầu chụp lại ngay tại app thay vì đẩy vào pipeline rồi hỏng ở bước sau.

### Kết quả xử lý một claim

```json
{
  "claim_no": "EC-2026-0451",
  "employee": { "id":"NV-0231", "name":"Trần Thị B", "dept":"Kinh doanh", "grade":"CV" },
  "purpose": "Công tác Hà Nội 20–22/08/2026", "project": "PRJ-014",
  "advance": { "no":"TU-2026-0088", "amount":5000000 },
  "lines": [
    { "seq":1, "invoice_no":"0012345", "vendor":"Vietnam Airlines",
      "net":3181818, "vat":318182, "gross":3500000,
      "account":"6417", "rules":["PASS"] },
    { "seq":2, "invoice_no":"0007788", "vendor":"KS Melia",
      "net":2727273, "vat":272727, "gross":3000000,
      "account":"6417", "rules":["EX.LIMIT_HOTEL"], "approved_gross":2400000 },
    { "seq":4, "doc_type":"receipt_no_invoice", "vendor":"Taxi",
      "net":450000, "vat":0, "gross":450000,
      "account":"6417", "rules":["EX.NO_INVOICE"], "tax_deductible":false },
    { "seq":5, "invoice_no":"0009900", "vendor":"NH Ngon",
      "gross":2000000, "rules":["EX.INVOICE_CANCELLED"], "status":"BLOCKED" }
  ],
  "totals": { "requested":9830000, "blocked":2000000, "capped":600000,
               "approved":7230000, "advance":5000000, "payable":2230000,
               "vat_deductible":616364, "non_deductible_cit":450000 },
  "outcome": "approved_partial"
}
```

### Ba kết cục — không có kết cục thứ tư

- `approved` hoặc `approved_partial` → gọi Journal Proposer
- `returned` → không sinh bút toán, claim về trạng thái chờ bổ sung kèm danh sách chính xác thiếu gì
- `pending_exception` → treo chờ duyệt ngoại lệ, không đi tiếp cho tới khi có quyết định của người có thẩm quyền

---

## 08 · Thuật toán ghép công nợ

Chia làm hai tầng độc lập: nhận diện đối tác, rồi phân bổ số tiền. Tầng hai chỉ chạy khi tầng một đạt ngưỡng.

### Tầng 1 — nhận diện đối tác từ nội dung chuyển khoản

| Tín hiệu | Trọng số | Cách tính |
|---|---|---|
| Số hợp đồng xuất hiện trong nội dung | 0.40 | Khớp regex mẫu số HĐ đã đăng ký trong kho hợp đồng |
| Số hoá đơn xuất hiện trong nội dung | 0.30 | Khớp với hoá đơn còn dư nợ |
| Tên đối tác (bỏ dấu, bỏ tiền tố loại hình) | 0.20 | Độ tương đồng chuỗi trên token đã chuẩn hoá |
| Số tài khoản người nộp đã từng dùng | 0.15 | Tra lịch sử giao dịch của đối tác |
| Số tiền khớp chính xác một dư nợ đang mở | 0.10 | Bằng đúng đến đơn vị đồng |

Điểm chuẩn hoá về thang 0–1. ≥ 0,85 tự động nhận. 0,60–0,85 đề xuất kèm yêu cầu xác nhận. < 0,60 chuyển `partner_unknown`. **Ngưỡng để trong cấu hình, không hard-code.**

### Tầng 2 — phân bổ vào hoá đơn và đợt hợp đồng

Sinh các phương án phân bổ rồi chọn phương án điểm cao nhất. Thứ tự ưu tiên khi sinh phương án:

1. Khớp chính xác một hoá đơn còn dư nợ đúng bằng số tiền
2. Khớp chính xác một đợt hợp đồng đúng bằng số tiền
3. Tổ hợp nhiều hoá đơn cộng lại đúng bằng số tiền — giới hạn tối đa 6 hoá đơn để tránh bùng nổ tổ hợp
4. Thanh toán một phần theo thứ tự hoá đơn cũ nhất trước, nếu hợp đồng không quy định thứ tự khác
5. Không phương án nào đạt ngưỡng → `no_match`

### Học từ hiệu chỉnh của người dùng

Mỗi lần kế toán sửa một phương án ghép, ghi lại cặp (mẫu nội dung chuyển khoản → đối tác) vào bộ nhớ riêng của pháp nhân đó. Lần sau gặp mẫu tương tự, tín hiệu này được cộng thêm trọng số 0.35. Đây là cơ chế đưa độ chính xác từ khoảng 70% lên trên 90% sau vài tháng vận hành, không cần huấn luyện lại mô hình.

---

## 09 · Rule engine

Toàn bộ validation và policy nằm trong bảng cấu hình có hiệu lực theo thời gian, không nằm trong code. Kế toán trưởng phải sửa được định mức mà không cần release.

```yaml
# Ví dụ định nghĩa rule chính sách chi tiêu
rule_code: EX.LIMIT_HOTEL
scope: expense_line
applies_when: expense_category == "luu_tru"
params:
  limit_by_grade: { NV: 1000000, CV: 1500000, TP: 2500000, GD: 4000000 }
  unit: per_night
outcome:
  on_exceed: CAP        # CAP | BLOCK | WARN | REQUIRE_EXCEPTION
  message_vi: "Vượt định mức lưu trú {limit}đ/đêm theo cấp {grade}"
effective_from: 2026-01-01
version: 3
```

| Nhóm rule | Ví dụ mã | Nguồn đối chiếu | Hệ quả điển hình |
|---|---|---|---|
| Hình thức & số học | FM.* | Nội tại chứng từ | Chặn nếu thiếu trường bắt buộc, lệch số học |
| Xác thực bên ngoài | TX.* | S2 cổng thuế | Chặn nếu hoá đơn đã huỷ, MST ngừng hoạt động, sai bên mua |
| Trùng lặp | DP.* | S1 + nội bộ | Chặn nếu hoá đơn đã claim hoặc đã ghi sổ |
| Chính sách chi tiêu | EX.* | S6 + S7 | Cắt về trần, yêu cầu duyệt ngoại lệ, hoặc chặn |
| Rủi ro thuế | RK.* | Nội tại + S1 | Cảnh báo tiền mặt từ 20 triệu, chi phí không được trừ |
| Hợp đồng | CT.* | S2 + S4 + thư viện mẫu | Chặn trùng số HĐ, cảnh báo lệch mẫu chuẩn |

**Nguyên tắc thông điệp lỗi:** Mọi rule sinh thông điệp tiếng Việt nói rõ sai gì và cần làm gì, kèm số liệu cụ thể.

---

## 10 · Journal Proposer

Nhận một sự kiện nghiệp vụ đã duyệt, trả về đề xuất bút toán. Không tự chạy — chỉ được gọi sau khi có `approval_event`. Ánh xạ tài khoản lấy từ bảng cấu hình theo pháp nhân, không hard-code số hiệu.

| Sự kiện | Nợ | Có | Ghi chú |
|---|---|---|---|
| Nghiệm thu mốc, xuất hoá đơn | 131 | 511 · 3331 | Kèm bút toán giá vốn 632 / 154·156 |
| Thu chuyển khoản đủ | 1121 | 131 | Chi tiết khách hàng và hoá đơn |
| Thu tiền mặt | 1111 | 131 | — |
| Thu trước khi có hoá đơn | 112 | 131 trả trước | Dùng 3387 nếu là doanh thu chưa thực hiện |
| Phần thu thừa | 112 | 3388 | Tách dòng riêng, không gộp |
| Chênh lệch tỷ giá khi thu | 635 | 515 | Theo chiều chênh lệch |
| Chi tạm ứng | 141 | 1111 · 1121 | Chi tiết người nhận |
| Quyết toán claim, có tạm ứng | 641·642·627 · 1331 | 141 | Chênh lệch sang 334 hoặc thu hồi 1111 |
| Quyết toán claim, không tạm ứng | 641·642·627 · 1331 | 334 · 3388 | Khi chi: 334·3388 / 1111·1121 |
| Bù trừ công nợ hai chiều | 331 | 131 | Cần biên bản bù trừ đính kèm |

**Cờ đi kèm mọi dòng chi phí:** Journal Proposer gắn thêm hai thuộc tính không phải bút toán nhưng bắt buộc phải mang theo: `vat_deductible` và `cit_deductible`. Chi phí không có hoá đơn vẫn được ghi nhận nhưng đánh dấu để loại khi quyết toán thuế thu nhập doanh nghiệp.

---

## 11 · Trạng thái

### `document` — chung cho cả ba usecase

```
received ──▶ classified ──▶ extracted ──▶ validated ──▶ proposed ──▶ approved ──▶ exported
    │             │              │             │              │
    └──▶ rejected └──▶ manual    └──▶ manual   └──▶ blocked    └──▶ returned
         (rác,        (không       (thiếu       (rule chặn)     (người duyệt
          trùng file)  phân loại    trường)                      trả lại)
                       được)
```

### `contract_schedule`
```
awaiting_milestone ──▶ due ──▶ partially_paid ──▶ settled
                        │            │
                        └──▶ overdue ┴──▶ written_off / disputed
```

### `expense_claim`
```
draft ──▶ submitted ──▶ dept_approved ──▶ finance_checked ──▶ approved ──▶ paid
             │               │                    │
             └──▶ returned ◀─┴────────────────────┘
                     │
                     └──▶ pending_exception ──▶ (quay lại finance_checked)
```

Mọi chuyển trạng thái ghi một `audit_event` với actor, thời điểm, và lý do.

---

## 12 · Lỗi & hàng đợi ngoại lệ

| Loại lỗi | Ứng xử | Ai thấy | SLA đề xuất |
|---|---|---|---|
| Nguồn ngoài không phản hồi (cổng thuế, ngân hàng) | Retry lũy thừa 3 lần, sau đó xếp hàng chờ, không đánh dấu chứng từ lỗi | Vận hành hệ thống | Tự phục hồi |
| Ảnh không đọc được | Trả về người nộp ngay tại kênh nhận, kèm hướng dẫn chụp lại | Người nộp | Tức thì |
| Không phân loại được chứng từ | Hàng đợi phân loại tay | Kế toán viên trực | 4 giờ làm việc |
| Trích xuất độ tin cậy thấp | Hiển thị cạnh ảnh gốc để người sửa nhanh, không loại bỏ | Kế toán viên trực | 1 ngày làm việc |
| Rule chặn | Không đi tiếp, thông báo người nộp kèm lý do cụ thể | Người nộp + kế toán | 1 ngày làm việc |
| Khoản thu không xác định | Hàng đợi tra soát, treo tối đa n ngày rồi leo thang | Kế toán công nợ | 3 ngày, leo thang 30 ngày |
| Xung đột dữ liệu với export kế toán | Dừng đồng bộ, cảnh báo, không tự ghi đè | Kế toán trưởng | Ngay |

**Không có fallback im lặng:** Khi một bước không chắc, hệ thống dừng và hỏi.

---

## 13 · Bảo mật & audit

- Phân quyền theo vai trò và phạm vi dữ liệu
- Chứng từ gốc bất biến — file nạp vào object store là write-once, đặt tên theo hash nội dung
- Audit log append-only cho: nạp chứng từ, mọi lần chạy rule, mọi hiệu chỉnh của người dùng, mọi phê duyệt, mọi lần xuất file import
- Che dữ liệu nhạy cảm trong prompt — số tài khoản ngân hàng, căn cước, thông tin lương chỉ gửi tới mô hình khi bước đó thực sự cần
- Tách khoá theo pháp nhân — thông tin đăng nhập cổng thuế và ngân hàng nằm trong kho bí mật riêng
- Ghi lại phiên bản prompt và model — mỗi bản ghi trích xuất lưu prompt hash và định danh model đã dùng

---

## 14 · Yêu cầu phi chức năng

| Hạng mục | Mục tiêu | Cách đo |
|---|---|---|
| Độ chính xác trường số tiền và thuế | > 99% | Đối chiếu mẫu ngẫu nhiên 200 chứng từ mỗi tháng |
| Tỷ lệ chứng từ đi thẳng không cần người chạm | > 70% | Đếm bản ghi không có `manual_edit` trước khi duyệt |
| Độ chính xác ghép công nợ tự động | > 90% sau 3 tháng | Tỷ lệ phương án ghép không bị người sửa |
| Thời gian xử lý một chứng từ có XML | < 10 giây | p95 từ lúc nhận tới trạng thái `validated` |
| Thời gian xử lý một claim 5 dòng ảnh chụp | < 90 giây | p95 toàn pipeline |
| Thông lượng cao điểm | 2.000 chứng từ/ngày | Test tải mô phỏng cuối tháng |
| Khả dụng | 99,5% giờ làm việc | Ngoài giờ cho phép bảo trì |
| Thời gian phục hồi khi nguồn ngoài chết | Không mất chứng từ | Chứng từ vẫn nhận được, chỉ hoãn bước xác thực |

---

## 15 · Thứ tự xây dựng

| Sprint | Nội dung | Tiêu chí hoàn thành |
|---|---|---|
| S1–S2 | Nền tảng: ingest đa kênh, object store, mô hình dữ liệu, audit log, khung rule engine | Nạp được chứng từ từ 3 kênh, lưu bất biến, tra cứu được |
| S3–S4 | UC-01 hợp đồng: trích xuất, sinh lịch thanh toán, rule CT.*, màn duyệt | 10 hợp đồng thật ra đúng lịch thanh toán, sai số bằng 0 sau khi người soát |
| S5–S6 | UC-02 phiếu thu: parser 5 ngân hàng, matching tầng 1 và 2, 8 nhánh kết cục | Ghép đúng trên 80% giao dịch của một tháng dữ liệu thật |
| S7–S9 | UC-03 claim: tách ảnh gộp, xác thực cổng thuế, policy engine, định tuyến duyệt | Chạy song song với quy trình cũ trong một chu kỳ, đối chiếu khớp |
| S10 | Journal Proposer, xuất file import, thông báo đa kênh | Kế toán import được không phải sửa tay |
| S11–S12 | Bộ nhớ học từ hiệu chỉnh, bảng theo dõi, tinh chỉnh ngưỡng | Đạt mục tiêu phi chức năng ở §14 |

### Ba câu hỏi phải trả lời trước sprint 1

1. Doanh nghiệp có tài khoản tra cứu hoá đơn điện tử không
2. Phần mềm kế toán đang dùng là gì và mẫu file import ra sao
3. Trung bình bao nhiêu chứng từ mỗi tháng

---

*Đặc Tả Kỹ Thuật AccAgent · bản thảo v0.1 · dùng kèm tài liệu Quy Trình Nghiệp Vụ AccAgent dành cho đội kế toán. Mọi thay đổi về bút toán ở §10 phải được đồng bộ giữa hai tài liệu.*
