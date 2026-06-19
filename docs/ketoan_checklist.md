# Checklist tiến độ Trợ lý Kế toán

> File này để bạn (chủ doanh nghiệp / kế toán trưởng) theo dõi tiến độ triển khai.
> Đánh dấu `[x]` khi xong, `[~]` đang làm, `[ ]` chưa làm.
> Cập nhật lần cuối: 2026-06-18 cuối session.

---

## Mức 1 — Skills + KB (zero-code)

### Backend skills

- [x] `accounting-vn-style/SKILL.md` — terminology VAS, định dạng số VN, xưng hô
- [x] `journal-entry-formatter/SKILL.md` — format bút toán 2 dòng Nợ-Có
- [x] `tax-calculation/SKILL.md` — VAT/TNDN/TNCN + breakdown
- [x] `compliance-citation/SKILL.md` — trích dẫn TT/NĐ/Luật
- [x] Skills tự kích hoạt theo keyword (live trên cosmos)
- [x] Smoke test: "Mua hàng 110tr VAT 10%" → format chuẩn TT200

### KB notes (8 file mẫu trong `docs/accounting_seed/`)

> ⚠️ **Cần upload thủ công qua https://app.cattuong.ai/knowledge → Bulk upload Markdown**

- [ ] **Bạn upload:** Note 1 — Hệ thống tài khoản TT200
- [ ] **Bạn upload:** Note 2 — Biểu thuế VAT 2026
- [ ] **Bạn upload:** Note 3 — Biểu thuế TNCN 2026
- [ ] **Bạn upload:** Note 4 — Thuế TNDN 2026
- [ ] **Bạn upload:** Note 5 — Template định khoản 20 nghiệp vụ
- [ ] **Bạn upload:** Note 6 — Deadline tờ khai 2026
- [ ] **Bạn upload:** Note 7 — Quy trình kế toán cuối tháng
- [ ] **Bạn upload:** Note 8 — Quy định hoá đơn điện tử TT78

### Review nội dung notes (kiểm tra trước khi upload)

- [ ] Kiểm tra **mức VAT 8%** còn hiệu lực theo NQ hiện hành (có thể hết hạn)
- [ ] Kiểm tra **lương cơ sở** 2.340.000 (có cập nhật?)
- [ ] Kiểm tra **thuế suất TNDN ưu đãi** ngành CNTT / khu công nghệ cao
- [ ] Bổ sung note đặc thù DN (vd hệ thống TK chi tiết, bảng giá dịch vụ)

---

## Mức 2 — Domain tools (7 tools)

- [x] `calculate_vat(amount, rate, inclusive)` — tách giá / VAT / tổng
- [x] `calculate_pit(gross, dependents, include_si)` — TNCN 7 bậc + breakdown
- [x] `calculate_cit(revenue, expense, loss, rate_pct)` — TNDN năm
- [x] `lookup_account(code)` — 60+ TK TT200 embedded
- [x] `check_filing_deadline(form_code, period)` — 8 mã form
- [x] `journal_entry_validator(lines)` — tổng Nợ = tổng Có + TK đúng
- [x] `import_excel_ledger(file_url, sheet_name)` — CSV/TSV/XLSX
- [x] Tools live trong `/agent/run` endpoint
- [x] Smoke test: validator phát hiện sai số 1₫

---

## Mức 3 — UI chuyên dụng (5 pages frontend)

- [x] `/accounting` — landing với 5 cards
- [x] `/accounting/tax-calc` — VAT / TNCN / TNDN tabs
- [x] `/accounting/deadline` — countdown dashboard 4 màu
- [x] `/accounting/journal` — bảng nhập + AI suggest
- [x] `/accounting/invoice` — paste hoá đơn → extract + suggest
- [x] `/accounting/reports` — BCKQHĐKD / BCĐKT / LCTT + download .md
- [x] Role gate: chỉ admin / super_admin truy cập được
- [x] Nav "Kế toán" trong header

---

## Mức 3 polish — bổ sung sau session đầu

### Save flow (closing the loop) ← _đang làm trong session tiếp theo_

- [ ] `SaveToKnowledgeButton` thành shared component (đang dùng riêng trong chat-room)
- [ ] `/accounting/tax-calc` → button "Lưu kết quả tính vào KB"
- [ ] `/accounting/journal` → button "Lưu bút toán vào KB" (sau khi Validate ✓)
- [ ] `/accounting/invoice` → button "Lưu hoá đơn + bút toán vào KB"
- [ ] `/accounting/reports` → button "Lưu báo cáo vào KB" (cạnh Download)

### Tích hợp tools nâng cao

- [ ] `/accounting/reports` auto-search KB bằng `search_notes` để có data thật
- [ ] `/accounting/journal` lưu lịch sử bút toán → list view "10 bút toán gần nhất"
- [ ] `/accounting/invoice` lưu hoá đơn đã processed → "10 hoá đơn gần nhất"

---

## Admin

- [ ] `/admin/skills` — page liệt kê 9 skills active (super_admin)
- [ ] `/admin/audit` — log các tool call gần nhất
- [ ] `/admin/analytics` — top câu hỏi, KB gap, user activity

---

## Hạn chế MVP cần wire (Phase 4+)

### OCR thật cho `/accounting/invoice`

- [ ] Decision doc: PaddleOCR (đã chạy trong agent stack) vs Whisper-V vs cloud OCR
- [ ] Wire endpoint `POST /ocr/invoice` → return Markdown text
- [ ] Update invoice page: thêm file upload bên cạnh textarea
- [ ] Test với 5-10 hoá đơn thật

### Push notification deadline

- [ ] Decision: email / Slack / Telegram / Zalo
- [ ] Background job (Hangfire / cron) chạy mỗi sáng kiểm tra deadline 7 ngày tới
- [ ] User preferences: chọn loại form muốn nhận nhắc
- [ ] Test với 1 deadline thật

### Excel sổ sách → KB

- [ ] `/accounting/journal` upload XLSX → parse rows → preview → save mỗi row thành note
- [ ] Hoặc bulk import từ template Excel có sẵn (mẫu sổ cái MISA / Fast)

### CSV import

- [ ] `/knowledge` bulk import từ CSV (mỗi hàng = 1 note)
- [ ] Mapping cột → field: title / content / tags

---

## Pilot 1 tuần (sau khi upload 8 notes)

### Kế toán viên test các flow

- [ ] Hỏi 10 câu kế toán cơ bản qua chat → đánh giá citation đúng?
- [ ] Tính lương cho 5 nhân viên thật qua `/accounting/tax-calc`
- [ ] Nhập 5 bút toán mua hàng vào `/accounting/journal` → validate
- [ ] Sinh BCKQHĐKD Q3/2025 với số liệu thật qua `/accounting/reports`
- [ ] Note 3 điều **tệ nhất** của UX
- [ ] Note 3 điều **hữu ích nhất**

### Đánh giá tổng

- [ ] AI có trả lời sai pháp luật không?
- [ ] Chất lượng citation TT/NĐ có đúng?
- [ ] Tốc độ phản hồi (chat ~1s, agent ~10-30s) chấp nhận được?
- [ ] Tính bảo mật data (lương, MST) — có thoải mái?
- [ ] Sẵn sàng cho team kế toán dùng hằng ngày?

---

## Roadmap mở rộng (sau pilot OK)

### M6 — Scalable RAG (Qdrant top-K)

- [ ] Wire OpenAI text-embedding-3-small (~$0.02/1M tokens)
- [ ] Tạo collection Qdrant `aiq_notes`
- [ ] On note save → generate embedding → store
- [ ] On chat → query top-K thay vì full-context
- [ ] Test: tăng KB lên 500 notes, latency phải giữ <1s

### Multi-tenant workspace (M8)

- [ ] Schema: thêm `workspace_id` vào users / notes
- [ ] UI: super_admin tạo workspace, mời user vào
- [ ] Mỗi workspace có KB + skills + roles riêng
- [ ] Billing per workspace

### Mobile + PWA (M11)

- [ ] Responsive layout cho tất cả pages
- [ ] PWA manifest + service worker
- [ ] Offline read cho KB (cache last 50 notes)

---

## Quy ước theo dõi

- `[x]` — Đã xong
- `[~]` — Đang làm
- `[ ]` — Chưa làm
- Khi bạn đánh dấu xong 1 mục, có thể edit trực tiếp file này trong VSCode
- Cập nhật cuối mỗi session: ghi "Cập nhật lần cuối: YYYY-MM-DD" ở top file

---

## Quick links

- Plan đầy đủ: [Ketoan_plan.md](Ketoan_plan.md) · [ketoan_plan.docx](ketoan_plan.docx)
- 8 notes mẫu: [accounting_seed/](accounting_seed/)
- Skills authoring: [skills_authoring.md](skills_authoring.md)
- Progress chung: [implement_progress_status.md](implement_progress_status.md)
- Roadmap chung: [roadmap.md](roadmap.md)
