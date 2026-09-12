# Sprint 6 — AccAgent Integration Phase 1

> **Phiên bản:** v0.1 draft — 2026-09-12
> **Nguồn ý tưởng:** 2 tài liệu tham khảo anh Toàn share
> - Payin reconciliation flowchart (đối soát chiết khấu Fujiwa)
> - Invoice lifecycle workflow v1.0 (Lưu trữ & Đối chiếu HĐ tự động)
> **Đối chiếu spec:** `accagent-spec-v0.1-dac-ta-ky-thuat.md` — UC-02, UC-03, source S1/S2/S8
> **Chiến lược:** Hybrid Option B (đã bàn trong `plans/whimsical-weaving-dahl.md`) — giữ ledger internal, add song song 3 capability mới không phá pilot Việt Đăng.

---

## Mục tiêu

Bù 3 gap ưu tiên nhất mà 2 tài liệu tham khảo highlight — đều là **tự động hoá đầu nguồn ingestion** (nơi kế toán viên tốn nhiều thời gian nhất và cũng là chỗ dễ nhất để tạo giá trị đo được cho khách):

1. **B6.1 — Email ingestion (S8)**: kéo hoá đơn từ hộp thư chung, tự động tải PDF/XML/link → chạy pipeline OCR + extract như flow web upload
2. **B6.2 — XML HĐĐT parser (S2)**: khi có file `.xml` cấu trúc chuẩn cổng thuế, ưu tiên parse XML thay OCR (nguồn tin cậy hơn, không có noise, extract 100% chính xác) — tuân thủ nguyên tắc AccAgent #7 "nguồn tin cậy hơn thắng"
3. **B6.3 — eTax reconciliation**: so sánh bảng kê nội bộ vs export từ cổng thuế, cảnh báo HĐ có trong thuế nhưng chưa lưu (thiếu) hoặc số lệch

3 gap này không phá code UC1-UC5 hiện tại; đều là add-on layer trước OCR pipeline hoặc song song với UC3 AP dashboard.

---

## Non-goals Sprint 6

- **Không** thay Accept button bằng "export import file MISA/Fast" (thuộc AccAgent Option A, tách sprint riêng)
- **Không** replace `sim_contracts` mock bằng full contract extraction (UC-01 spec, thuộc B2 trong roadmap Option B)
- **Không** touch Google Drive/Sheet integration (tài liệu tham khảo dùng Drive vì chưa có SaaS backend — AIQ đã có MinIO + Postgres nên không cần)
- **Không** đụng bank statement MT940 parser (thuộc S3 — sprint sau)

---

## B6.1 Email Ingestion

### Kịch bản

Kế toán Việt Đăng có hộp thư `hoadon@vietdang.vn`. NCC (VNPT, MISA, FPT eInvoice) gửi HĐĐT về hộp thư này dạng:
- Attachment `.pdf` + `.xml` (phổ biến nhất)
- Link download từ portal HĐĐT (VNPT, Viettel)
- Body chứa mã tra cứu (số HĐ + mã bí mật)

Agent định kỳ (mặc định 5 phút) đọc hộp thư, filter email chưa xử lý, tải file → gọi voucher upload pipeline y hệt web flow.

### Deliverables

| # | Item | File / component |
|---|---|---|
| 1 | Schema: `sim_email_sources` (tenant_id, imap_host, imap_port, username, encrypted_password, folder, poll_interval_sec, is_active) | `infra/postgres/init/21_email_sources.sql` |
| 2 | Schema: `sim_email_messages` (id, source_id, message_id_header, from_addr, subject, received_at, status, voucher_ids[]) — dedup theo `message_id_header` | cùng file 21 |
| 3 | Repository interface + Dapper impl | `gateway/src/Aiq.Gateway/Emails/IEmailSourceRepository.cs` |
| 4 | IMAP client (Hangfire recurring job) — dùng MailKit library, poll từng source, download attachment + link | `gateway/src/Aiq.Gateway/Jobs/EmailIngestionJobRunner.cs` |
| 5 | Attachment classifier: PDF vs XML vs link body — route PDF → OCR pipeline, XML → B6.2 parser, link → HTTP fetch → classify lại | cùng file 4 |
| 6 | Endpoint quản lý sources | `gateway/src/Aiq.Gateway/Endpoints/EmailSourceEndpoints.cs` — CRUD + test connection |
| 7 | Admin UI trang cấu hình | `web/src/app/(app)/admin/email-sources/page.tsx` |
| 8 | Nav item "Nguồn email" trong admin section side-nav | `web/src/components/side-nav.tsx` |
| 9 | Credential encryption at rest — dùng DataProtection API sẵn có | `gateway/src/Aiq.Gateway/Crypto/*` (reuse) |

### Verification

- [ ] Setup fake IMAP local (dockerized greenmail hoặc mailhog), gửi email có attachment .pdf → job poll → thấy row `sim_email_messages` + voucher stub created
- [ ] Gửi cùng message 2 lần → chỉ 1 row (dedup theo Message-ID header)
- [ ] Test with real Việt Đăng mailbox (staging config, không đụng prod hộp thư)
- [ ] Failure mode: IMAP timeout → job retry với backoff; sau 3 lần fail → cảnh báo admin

### Effort

5-7 ngày solo dev

---

## B6.2 XML HĐĐT Parser

### Kịch bản

File XML từ cổng thuế/eInvoice có schema chuẩn (theo TT78/2021 và các phiên bản của VNPT/Viettel/MISA). Parse XML deterministic ăn đứt OCR:
- Không sai số (OCR đôi khi nhầm số 0/O, 5/S)
- Không cần LLM extract
- Nhanh (~50ms/file)
- Confidence = 1.0

### Deliverables

| # | Item | File |
|---|---|---|
| 1 | XML schema detector — sniff root element + namespace để chọn parser | `gateway/src/Aiq.Gateway/Extraction/EInvoiceXml/SchemaDetector.cs` |
| 2 | Parser cho schema VNPT (`<HDon>` root) | `gateway/src/Aiq.Gateway/Extraction/EInvoiceXml/VnptParser.cs` |
| 3 | Parser cho schema Viettel/eInvoice generic | `gateway/src/Aiq.Gateway/Extraction/EInvoiceXml/GenericParser.cs` |
| 4 | Parser cho MISA meInvoice | `gateway/src/Aiq.Gateway/Extraction/EInvoiceXml/MisaParser.cs` |
| 5 | Common output DTO: `EInvoiceExtractedData` (seller, buyer, items, VAT breakdown, signature valid?) | cùng namespace |
| 6 | Route trong `VoucherUploadAsync`: nếu content-type = `application/xml` hoặc filename `.xml` → skip OCR, gọi XML parser trực tiếp | `VoucherEndpoints.cs` (extend `UploadAsync`) |
| 7 | UI: badge "XML verified" trên voucher detail khi source là XML — highlight confidence 100% | `web/src/app/(app)/vouchers/[id]/page.tsx` |
| 8 | Tests: 6 fixture XML từ VNPT/Viettel/MISA (2 mỗi loại — 1 outbound, 1 inbound) | `gateway/tests/.../EInvoiceXmlTests/` |

### Verification

- [ ] Upload 3 XML samples (1 từ mỗi provider) → thấy `extracted_fields` đầy đủ + `ocr_confidence = 1.0`
- [ ] Upload XML corrupt → soft-fail với message rõ ràng, không crash job
- [ ] So sánh: cùng 1 hoá đơn upload PDF vs XML → data XML chính xác hơn PDF trên số tiền lẻ (case OCR đọc nhầm)
- [ ] Nếu có cả PDF + XML trong email attachment → ưu tiên XML (nguyên tắc AccAgent #7)

### Effort

7-10 ngày (schema variance lớn — cần thu thập fixture thực từ Việt Đăng)

---

## B6.3 eTax Reconciliation

### Kịch bản

Kế toán viên định kỳ (tháng/quý) export bảng kê HĐĐT từ cổng eTax hoặc MISA AMIS — file Excel/CSV chứa danh sách toàn bộ HĐ điện tử đã phát hành cho công ty theo MST. So sánh với bảng kê nội bộ (voucher table) → phát hiện:
- **HĐ khớp** (có cả 2 bên) — passthrough
- **HĐ thiếu** (có trong thuế, chưa có trong voucher table) — cảnh báo kế toán bổ sung, có thể do email chưa đến hoặc bỏ sót
- **HĐ lệch** (khớp số HĐ + NCC nhưng lệch tiền/ngày) — cảnh báo review

### Deliverables

| # | Item | File |
|---|---|---|
| 1 | Schema: `sim_tax_reconciliation_runs` (id, tenant_id, period_from, period_to, uploaded_file_key, status, summary_json, created_at, created_by) | `infra/postgres/init/22_tax_reconciliation.sql` |
| 2 | Schema: `sim_tax_reconciliation_items` (run_id, tax_doc_number, tax_seller_mst, tax_total, tax_document_date, matched_voucher_id, status: matched/missing/mismatched, delta_amount, notes) | cùng file 22 |
| 3 | Excel/CSV parser cho export eTax + MISA AMIS format | `gateway/src/Aiq.Gateway/Reconciliation/ETaxParser.cs` |
| 4 | Matching engine: chỉ số HĐ + MST NCC làm business key, tolerance ±1000đ cho tiền | `gateway/src/Aiq.Gateway/Reconciliation/ReconciliationEngine.cs` |
| 5 | Endpoint `POST /reconciliation/upload-tax-export` — nhận file, chạy match sync (nhanh <10s), trả `run_id` | `gateway/src/Aiq.Gateway/Endpoints/ReconciliationEndpoints.cs` |
| 6 | Endpoint `GET /reconciliation/runs/{id}` — summary + items list | cùng file 5 |
| 7 | UI page `/admin/reconciliation` — upload + dashboard 3 tabs (matched/missing/mismatched) với action button "Tạo voucher stub" cho HĐ thiếu | `web/src/app/(app)/admin/reconciliation/page.tsx` |
| 8 | Component ReconciliationSummary hiển thị: `x khớp / y thiếu / z lệch` với chart | `web/src/components/reconciliation-summary.tsx` |

### Verification

- [ ] Upload export eTax mẫu (Việt Đăng 1 tháng thật) → thấy summary + list items
- [ ] Case matched: 100% khớp → status matched, không action
- [ ] Case missing: có trong thuế, chưa có voucher → button "Tạo stub" → tạo voucher với `source_type='tax_reconciliation'`, `status='awaiting_document'`
- [ ] Case mismatched: khớp doc_number + mst nhưng lệch tiền > 1000đ → status mismatched, hiển thị delta
- [ ] Idempotent: upload cùng file 2 lần → chỉ 1 run mới, không nhân đôi items

### Effort

5-7 ngày

---

## Tổng effort Sprint 6

| Sprint | Effort | Tuần wall (solo) |
|---|---|---|
| B6.1 Email ingestion | 5-7 ngày | 1-1.5 |
| B6.2 XML parser | 7-10 ngày | 1.5-2 |
| B6.3 eTax reconciliation | 5-7 ngày | 1-1.5 |
| **Tổng** | **17-24 ngày** | **~4-5 tuần** |

---

## Thứ tự triển khai đề xuất

1. **B6.2 XML parser** làm trước (thấp nhất risk, cao nhất giá trị đo được: OCR sai vs XML đúng)
2. **B6.1 Email ingestion** làm sau (cần credentials Việt Đăng thật, dependency ngoài)
3. **B6.3 eTax reconciliation** làm cuối (cần data thật từ cổng thuế để tune matching)

---

## Rủi ro & Dependencies

| Risk | Impact | Mitigation |
|---|---|---|
| Việt Đăng không có hộp thư kế toán riêng | B6.1 bị block | Confirm với anh Toàn trước sprint |
| XML schema variance giữa các provider lớn | B6.2 blow scope | Bắt đầu với VNPT (phổ biến nhất), add provider sau |
| eTax portal không cho export automated | B6.3 phải manual upload | Chấp nhận manual upload, không tự động fetch cổng thuế |
| Credentials IMAP lưu trữ | Security | DataProtection + audit access |

---

## Ngoài phạm vi Sprint 6 (parking lot)

- Tra cứu XML tra cứu cổng thuế (chống trùng nhau khi NCC gửi HĐ giả) — cần credentials tenant lên eTax
- Auto-fetch bank statement (MT940/API BIDV/VCB) — thuộc S3, sprint riêng
- Payin reconciliation với discount classification (từ SVG flowchart) — thuộc UC-02 extension, cần Sprint 7

---

## Next steps

1. Anh review draft này
2. Confirm 3 câu hỏi:
   - Việt Đăng có hộp thư kế toán riêng cho HĐĐT chưa? (B6.1 blocker)
   - Anh Toàn có sample XML từ VNPT/Viettel/MISA để làm fixture không? (B6.2)
   - Việt Đăng có tài khoản eTax và tự export được không? (B6.3)
3. Nếu OK → tôi break B6.1/B6.2/B6.3 thành sprint từng phần với TodoWrite + PR riêng
