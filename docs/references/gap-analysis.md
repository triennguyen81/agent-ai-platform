# Gap Analysis — Tài liệu tham khảo vs AIQ Accounting hiện tại

> **Ngày:** 2026-09-12
> **Nguồn tham khảo:**
> - [payin-reconciliation-flowchart.md](payin-reconciliation-flowchart.md) — quy trình đối soát chiết khấu Fujiwa
> - [invoice-lifecycle-workflow.md](invoice-lifecycle-workflow.md) — quy trình lưu trữ + đối chiếu HĐ tự động
> **Đối chiếu:** hệ thống AIQ Accounting sau Sprint 5 (HITL approval queue)

---

## TL;DR

- 2 tài liệu tham khảo là workflow **automation-first** thiết kế trên stack Make/n8n + Google Workspace + IMAP
- AIQ hiện tại là **standalone SaaS** với backend riêng (Postgres + MinIO + Hangfire) — khác architecture nhưng giải quyết cùng bài toán
- **~50% capability đã có**, ~30% cần thêm connectors, ~20% cần thiết kế lại flow
- 3 gap ưu tiên nhất đã được draft thành [../sprint-6-accagent-integration-spec.md](../sprint-6-accagent-integration-spec.md)

---

## Ma trận so sánh chi tiết

### Nhóm 1 — Ingestion (nhận chứng từ)

| Capability | Tài liệu tham khảo | AIQ hiện tại | Gap |
|---|---|---|---|
| Nhận HĐ qua email hộp thư chung | ✅ IMAP + shared mailbox | ❌ Chỉ upload web | **Cao** — Sprint 6 B6.1 |
| Nhận PDF attachment | ✅ Auto download | ❌ Manual upload | Bù cùng B6.1 |
| Nhận XML HĐĐT | ✅ Parse XML deterministic | ❌ Chỉ OCR PDF/image | **Cao** — Sprint 6 B6.2 |
| Nhận link tra cứu HĐĐT | ✅ HTTP fetch + parse | ❌ Không có | Trung — sprint sau |
| Nhận sao kê ngân hàng MT940 | ⚠ Payin flow input | ❌ Chỉ OCR UNC image | Trung — sprint sau (S3 AccAgent) |
| Upload web trực tiếp | Không đề cập | ✅ Đã có (fix 217df8a) | OK |

### Nhóm 2 — Extraction (trích xuất dữ liệu)

| Capability | Tài liệu tham khảo | AIQ hiện tại | Gap |
|---|---|---|---|
| OCR PDF/image | ✅ AI OCR/Parser | ✅ RapidOCR + PaddleOCR + Claude extract | OK |
| Trích số HĐ / NCC / MST / VAT / tổng | ✅ | ✅ `voucher.extracted_fields` | OK |
| XML parser cho HĐĐT | ✅ Cấu trúc chuẩn | ❌ | **Cao** — Sprint 6 B6.2 |
| Line items detail | Không nhấn mạnh | ✅ `voucher.line_items` | AIQ mạnh hơn |
| Confidence score | Không đề cập | ✅ `ocr_confidence` | AIQ mạnh hơn |

### Nhóm 3 — Storage & Metadata

| Capability | Tài liệu tham khảo | AIQ hiện tại | Gap |
|---|---|---|---|
| Object storage | Google Drive folder tree | MinIO S3 key-value | Khác design — AIQ scalable hơn |
| Cấu trúc HOA_DON/YYYY-MM/NCC/ | ✅ Semantic path | ❌ UUID key layout | Không cần — dùng metadata + `/vouchers` filter |
| Rule đặt tên `[Số]_[NCC]_[YYYYMMDD]` | ✅ | ❌ Giữ `original_filename` | Optional — dùng khi export |
| Bảng kê Google Sheet | ✅ | ❌ Dùng `/vouchers` page + PostgreSQL | Khác design |
| Business key **Số HĐ + MST NCC** cho dedup | ✅ Explicit | ⚠ Chỉ `voucher_id` UUID (idempotent theo file) | **Cao** — cần thêm unique constraint |

### Nhóm 4 — Reconciliation (đối chiếu)

| Capability | Tài liệu tham khảo | AIQ hiện tại | Gap |
|---|---|---|---|
| Đối chiếu bảng kê nội bộ vs eTax | ✅ AI compare | ❌ Chưa có | **Cao** — Sprint 6 B6.3 |
| Cảnh báo HĐ thiếu / lệch | ✅ AI alert (đỏ) | ⚠ `risk_flags[]` — không cross-check với thuế | Sprint 6 B6.3 |
| Match theo doc_number + MST | ✅ Explicit | ❌ | Sprint 6 B6.3 |
| Tolerance ±1000đ | Không đề cập | ⚠ Có trong AR match (implicit) | Cần config expose |
| Cross-check với cổng HĐĐT tra cứu | ⚠ Không tự động (kế toán export) | ❌ | Cần S2 AccAgent (sprint sau) |

### Nhóm 5 — Payin reconciliation (đối soát khoản thu)

| Capability | Tài liệu tham khảo | AIQ hiện tại | Gap |
|---|---|---|---|
| Load HĐ + điều khoản CK (% / kỳ hạn / ngưỡng doanh số) | ✅ Contract terms | ⚠ `sim_contracts` mock 4 rows, không có terms | **Cao** — cần contract schema mở rộng |
| Tính expected = Tổng HĐ − CK TM − CK TT | ✅ | ❌ | **Cao** — cần expected calc engine |
| Phân loại 4 loại CK | ✅ Rule engine deterministic | ❌ | **Cao** — cần classifier |
| Bút toán CK thanh toán sớm Nợ 635 | ✅ | ❌ Chỉ có Nợ 112/Có 131 | Trung — extend journal generator |
| Bút toán volume rebate Nợ 521 | ✅ | ❌ | Trung — cần cumulative sales tracker |
| Bút toán trả hàng HĐ âm | ✅ | ❌ | Trung |
| Tạm treo Có 338 khi không xác định | ✅ | ⚠ HITL Sprint 5 có framework | Sprint 5 extend được |

### Nhóm 6 — Human-in-the-loop

| Capability | Tài liệu tham khảo | AIQ hiện tại | Gap |
|---|---|---|---|
| Exception queue (scan mờ, format lạ) | ✅ Kế toán handle manual | ⚠ `ocr_unavailable` status | Cần route thành queue riêng |
| Escalation Sales / BGĐ | ✅ Explicit | ⚠ Sprint 5 HITL admin queue | Extend cho Sales role |
| Kế toán confirm định kỳ | ✅ Explicit | ⚠ Accept button (real-time) | Khác flow |
| Approval workflow | ⚠ Implicit | ✅ Sprint 5 HITL với 2-step confirm | AIQ mạnh hơn |

### Nhóm 7 — Export & Output

| Capability | Tài liệu tham khảo | AIQ hiện tại | Gap |
|---|---|---|---|
| Tra cứu theo NCC + khoảng thời gian | ✅ AI search | ⚠ `/vouchers` filter (chưa hoàn thiện) | Trung — cần thêm filter |
| Export folder ZIP + xlsx cho bank | ✅ | ❌ | Trung — cần export endpoint |
| Link chia sẻ | ✅ Drive share URL | ❌ | Trung — cần signed URL |
| Báo cáo tổng hợp (đã đối soát / còn treo / quá hạn) | ✅ | ⚠ Có dashboard nhưng chưa cover 3 status | Cần enrichment |
| Gửi Sales + BGĐ + Kế toán trưởng | ✅ | ❌ Chưa có email notification | Sprint 5.5 optional |

---

## Kết luận & Recommendation

### Điểm mạnh AIQ so với 2 tài liệu tham khảo

1. **Scalable architecture** — Postgres + MinIO + Hangfire khoẻ hơn Google Workspace stack cho >100 tenant
2. **Multi-tenant với data isolation** — mỗi tenant có ledger riêng, khác với "1 Google Drive folder cho cả công ty"
3. **HITL approval queue** (Sprint 5) — mạnh hơn "manual review Google Sheet"
4. **Detailed line items + confidence score** — Sheet chỉ có total
5. **RBAC & audit trail** — chưa thấy trong 2 tài liệu tham khảo

### Điểm yếu AIQ so với 2 tài liệu tham khảo

1. **Không có email ingestion** — kế toán phải upload từng file thủ công
2. **Không có XML parser** — mọi HĐĐT đều đi qua OCR (chậm + kém chính xác)
3. **Không có eTax reconciliation** — không phát hiện HĐ thiếu / lệch
4. **Không có contract terms** — không tính expected amount, không phân loại CK
5. **Không có export folder cho bank** — kế toán phải tự nén ZIP

### Ưu tiên roadmap

- **Sprint 6 (đề xuất)** — [../sprint-6-accagent-integration-spec.md](../sprint-6-accagent-integration-spec.md) — bù 3 gap top: email + XML + eTax reconciliation
- **Sprint 7** — payin reconciliation với 4-CK classifier (mở rộng UC-02)
- **Sprint 8** — export folder + bank package + email notification
- **Backlog** — MT940 bank statement parser, tra cứu XML cổng thuế realtime

### Câu hỏi cần confirm với anh Toàn

1. Hệ thống 2 tài liệu tham khảo mô tả có phải là hệ thống anh Toàn đang xây / tư vấn cho khách khác không? (Để hiểu context)
2. Việt Đăng có phải cũng sẽ dùng flow này, hay đây chỉ là reference cho khách nhỏ hơn?
3. Nếu Việt Đăng cần 3 gap này (email + XML + eTax) — timeline mong muốn?
