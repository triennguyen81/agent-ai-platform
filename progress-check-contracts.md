# Progress Check — Trợ lý Giám sát Hợp đồng

> Checklist tracking module Hợp đồng. File platform-wide: [`progress-check.md`](progress-check.md).
> Đánh dấu `[x]` xong · `[~]` đang làm · `[ ]` chưa làm.
> Cập nhật cuối: 2026-06-25 (start).

---

## 0. Quyết định cần chốt trước khi start

- [ ] **Notification channel** — chọn 1+: in-app / email / Telegram / Zalo
- [ ] **Loại hợp đồng** đối tác xử lý — generic / lao động / mua bán / dịch vụ / xây dựng
- [ ] **Multi-tenant model** — user riêng vs shared org
- [ ] **PDF storage** — volume `/var/aiq/uploads` (MVP) vs cloud (S3 / R2)
- [ ] **Scope đợt này** — Full 3 phases (~3 tuần) vs Phase 1 trước (1 tuần)

---

## Phase 1 — Foundation (1 tuần)

### Schema & Migration

- [ ] Tạo `infra/postgres/init/07_contracts.sql` với 2 bảng: `contracts`, `contract_clauses`
- [ ] Migration idempotent (chạy được trên DB live)
- [ ] Run trên cosmos: `docker exec aiq-postgres psql -U aiq -d aiq < 07_contracts.sql`

### Gateway (.NET)

- [ ] `gateway/src/Aiq.Gateway/Contracts/ContractRecords.cs` — DTOs
- [ ] `gateway/src/Aiq.Gateway/Contracts/IContractRepository.cs` — interface + impl Dapper
- [ ] Register `IContractRepository` trong `Program.cs` DI
- [ ] `gateway/src/Aiq.Gateway/Endpoints/ContractEndpoints.cs`:
  - [ ] `GET /contracts` — list
  - [ ] `POST /contracts` — create
  - [ ] `GET /contracts/{id}` — detail with clauses
  - [ ] `PATCH /contracts/{id}` — update
  - [ ] `DELETE /contracts/{id}` — soft delete (status='terminated')
  - [ ] `POST /contracts/{id}/clauses` — add clause
- [ ] Authorization: yêu cầu role `admin` hoặc `super_admin`
- [ ] Gateway build pass: `dotnet build`

### Agent (Python)

- [ ] `agent/skills/contract-extractor/SKILL.md` — extraction prompt rules
- [ ] `agent/src/aiq_agent/agentic/contract_tools.py`:
  - [ ] `@tool extract_contract_fields(raw_text)` → JSON với party_a/b, dates, amount, clauses[]
  - [ ] `@tool save_contract(fields, user_token)` → POST gateway
- [ ] Wire vào `agentic/tools.py` `build_tools()`
- [ ] Update `runner.py` allowed_tools list
- [ ] Python syntax check pass

### Web (Next.js)

- [ ] `web/src/app/(app)/contracts/page.tsx` — list page
- [ ] `web/src/app/(app)/contracts/[id]/page.tsx` — detail
- [ ] `web/src/app/(app)/contracts/upload/page.tsx` — PDF dropzone
- [ ] `web/src/app/api/contracts/route.ts` — proxy list/create
- [ ] `web/src/app/api/contracts/[id]/route.ts` — proxy detail/update
- [ ] `web/src/app/api/contracts/upload/route.ts` — multipart proxy
- [ ] `web/src/lib/api.ts` — `gateway.listContracts/getContract/createContract` helpers
- [ ] Header nav: thêm "Hợp đồng" link cho admin/super_admin (`web/src/app/(app)/layout.tsx`)

### Deploy + Smoke test

- [ ] Commit + push
- [ ] Cosmos: git pull + run migration + rebuild gateway/agent/web
- [ ] Restart containers
- [ ] Upload PDF hợp đồng lao động mẫu
- [ ] Verify: raw text hiển thị + extracted fields đầy đủ
- [ ] Verify: `/contracts` list hiện 1 row

---

## Phase 2 — Checklist (1 tuần)

### Schema

- [ ] `infra/postgres/init/08_contract_checklist.sql`:
  - [ ] Bảng `contract_checklist_items` với cột status / due_date / assignee_email
- [ ] Migration trên cosmos

### Gateway

- [ ] `gateway/src/Aiq.Gateway/Contracts/IChecklistRepository.cs`
- [ ] Endpoints:
  - [ ] `POST /contracts/{id}/generate-checklist` — trigger AI
  - [ ] `GET /contracts/{id}/checklist` — list items
  - [ ] `PATCH /checklist/{itemId}` — update status / due_date / assignee
  - [ ] `DELETE /checklist/{itemId}`

### Agent

- [ ] `agent/skills/contract-checklist-builder/SKILL.md` — rules cho mỗi loại clause
- [ ] MCP tool `generate_checklist(contract_id, user_token)` → POST gateway

### Web

- [ ] Trong `/contracts/[id]`: tab "Checklist" với table interactive
- [ ] Button "AI sinh checklist" (gọi generate-checklist endpoint)
- [ ] Checkbox + due date picker + assignee input → PATCH on change
- [ ] Filter: Open / Due soon (≤7d) / Overdue / Done

### Smoke test

- [ ] Vào contract đã upload Phase 1 → bấm "AI sinh checklist"
- [ ] Verify: 5-15 items với due date hợp lý
- [ ] Check/uncheck → reload page → state persist
- [ ] Filter "Due soon" hiển thị đúng

---

## Phase 3 — Notification + Tracking (3-5 ngày)

### Schema

- [ ] `infra/postgres/init/09_notifications.sql`: bảng `notifications`

### Gateway — Hangfire job

- [ ] `gateway/src/Aiq.Gateway/Jobs/ContractDeadlineJob.cs` (copy pattern từ `OcrJobRunner.cs`)
- [ ] Recurring schedule: daily 07:00 VN (`RecurringJob.AddOrUpdate`)
- [ ] Logic: SELECT items WHERE status='open' AND due_date - remind_days_before <= today → INSERT notifications
- [ ] Test trigger manual qua `/jobs/dashboard`

### Gateway — Endpoints

- [ ] `GET /notifications` — list cho user
- [ ] `PATCH /notifications/{id}/read` — mark as read
- [ ] `GET /notifications/unread-count`

### Notification channels (chọn theo Decision #1)

#### In-app (always on)

- [ ] Web: bell icon trong header với badge unread count
- [ ] `web/src/app/(app)/notifications/page.tsx` — list + mark as read
- [ ] Poll unread count mỗi 60s (hoặc SSE/websocket sau)

#### Email SMTP (nếu chọn)

- [ ] Cài SMTP provider (Brevo / SendGrid / Mailgun) — cần ENV `SMTP_*`
- [ ] Gateway: `IEmailSender` interface + impl
- [ ] Hangfire job nhánh: nếu user có email → send template

#### Telegram bot (nếu chọn)

- [ ] Tạo bot qua @BotFather, lấy token
- [ ] User binding: mỗi user nhập chat_id vào profile
- [ ] Gateway: HTTP POST → Telegram Bot API

#### Zalo OA (nếu chọn)

- [ ] Cấu hình ZNS — cần Zalo certificate
- [ ] Template message approval

### Smoke test

- [ ] Trigger Hangfire job qua dashboard
- [ ] Verify: bell icon hiện badge ≥ 1
- [ ] Click → `/notifications` page list items
- [ ] Mark as read → badge giảm
- [ ] (Nếu email) check inbox

---

## Deploy + Documentation

- [ ] Commit + push từng phase riêng để rollback dễ
- [ ] Update `docs/architecture_structure.md` — thêm Contracts section + Mermaid
- [ ] Update `docs/implement_progress_status.md` — thêm rows cho Phase 1-3 contracts
- [ ] Update `docs/roadmap.md` — đánh dấu M-Contracts done
- [ ] Regenerate `docs/bao_cao_tien_do_aiq.docx` (gửi sếp)
- [ ] Tạo `docs/contract_admin_guide.md` — hướng dẫn admin pilot

---

## Pilot 1 tuần (sau khi deploy Phase 1+2+3)

### Admin/đối tác test

- [ ] Upload 5 PDF hợp đồng thật → verify extraction accuracy
- [ ] AI sinh checklist cho từng contract → review item nào sai/thiếu
- [ ] Set due date thực tế cho 5 items → đợi cron trigger
- [ ] Verify notification đến đúng channel

### Đánh giá

- [ ] Extraction accuracy ≥ 90% các trường chính (party, dates, amount)?
- [ ] Checklist generation tạo items hữu ích, không spam?
- [ ] Notification đúng deadline?
- [ ] UX có cần polish (bulk action, export...)?
- [ ] Sẵn sàng cho team đối tác dùng production?

---

## Risks + Mitigation

| Risk | Mitigation |
|---|---|
| OCR sai (PDF scan mờ, chữ ký nét) | Fallback: cho user edit raw_text trước khi extract |
| LLM hallucination khi tạo checklist (item bịa) | Cite clause_id; user review trước khi commit |
| Cron job miss khi deploy/restart | Hangfire có retry + dashboard; manual trigger luôn được |
| Data nhạy cảm (lương, MST) | PDF stay trong VPS; per-user audit log |
| Notification spam (10 items overdue → 10 mail) | Bundle notifications theo user/day; mỗi email tổng hợp |

---

## Reference quick links

- Plan đầy đủ: [`plan.md`](plan.md)
- Implementation paths: `C:\Users\tuong\.claude\plans\whimsical-weaving-dahl.md`
- Workflow đối tác: `docs/architecture/2_ai_hoadon_process.pdf`
- Architecture target: `docs/architecture/kientruc.png`
- Platform-wide progress: [`progress-check.md`](progress-check.md)
- AIQ status hiện tại: [`docs/implement_progress_status.md`](docs/implement_progress_status.md)
- AIQ roadmap: [`docs/roadmap.md`](docs/roadmap.md)
- Accounting checklist pattern (mẫu format): [`docs/ketoan_checklist.md`](docs/ketoan_checklist.md)
