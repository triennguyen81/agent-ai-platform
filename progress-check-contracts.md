# Progress Check — Trợ lý Giám sát Hợp đồng

> Checklist tracking module Hợp đồng. File platform-wide: [`progress-check.md`](progress-check.md).
> Đánh dấu `[x]` xong · `[~]` đang làm · `[ ]` chưa làm.
> Cập nhật cuối: 2026-06-25 — Phases 1+2+3 deployed cosmos.

**Status snapshot:**

- `e72bf58` Phase 1 — Foundation ✅
- `f0e71b4` Phase 2 — Checklist ✅
- `cebf063` Phase 3 — Notifications + cron ✅
- Pending: browser smoke test + pilot 1 tuần + docs

---

## 0. Quyết định cần chốt trước khi start

- [x] **Notification channel** — chọn: **in-app** (zero infra; có thể thêm email/Telegram sau)
- [x] **Loại hợp đồng** đối tác xử lý — generic VN business (extractor skill xử lý đa dạng)
- [x] **Multi-tenant model** — per-user (contracts.user_id FK)
- [x] **PDF storage** — volume `/var/aiq/uploads` (MVP)
- [x] **Scope đợt này** — Full 3 phases (commits e72bf58 → cebf063)

---

## Phase 1 — Foundation (1 tuần) ✅ commit `e72bf58`

### Schema & Migration

- [x] Tạo `infra/postgres/init/07_contracts.sql` với 2 bảng: `contracts`, `contract_clauses`
- [x] Migration idempotent (chạy được trên DB live)
- [x] Run trên cosmos: `docker exec aiq-postgres psql -U aiq -d aiq < 07_contracts.sql`

### Gateway (.NET)

- [x] `gateway/src/Aiq.Gateway/Contracts/ContractRecords.cs` — DTOs
- [x] `gateway/src/Aiq.Gateway/Contracts/IContractRepository.cs` — interface + impl Dapper
- [x] Register `IContractRepository` trong `Program.cs` DI
- [x] `gateway/src/Aiq.Gateway/Endpoints/ContractEndpoints.cs`:
  - [x] `GET /contracts` — list
  - [x] `POST /contracts` — create (transactional clauses)
  - [x] `GET /contracts/{id}` — detail with clauses
  - [x] `PATCH /contracts/{id}` — update
  - [x] `DELETE /contracts/{id}` — soft delete (status='terminated')
  - [ ] `POST /contracts/{id}/clauses` — add clause _(not built — clauses come bundled via create)_
- [x] Authorization: yêu cầu role `admin` hoặc `super_admin`
- [x] Gateway build pass: `dotnet build`

### Agent (Python)

- [x] `agent/skills/contract-extractor/SKILL.md` — extraction prompt rules
- [x] `agent/src/aiq_agent/agentic/contract_tools.py`:
  - [x] `@tool extract_contract_fields(raw_text)` → JSON với party_a/b, dates, amount, clauses[]
  - [x] `@tool save_contract(fields, user_token)` → POST gateway
- [x] Wire vào `agentic/tools.py` `build_tools()`
- [x] Update `runner.py` allowed_tools list
- [x] Python syntax check pass

### Web (Next.js)

- [x] `web/src/app/(app)/contracts/page.tsx` — list page
- [x] `web/src/app/(app)/contracts/[id]/page.tsx` — detail
- [x] `web/src/app/(app)/contracts/upload/page.tsx` — PDF dropzone
- [x] `web/src/app/api/contracts/route.ts` — proxy list/create
- [x] `web/src/app/api/contracts/[id]/route.ts` — proxy detail/update
- [x] `web/src/app/api/contracts/upload/route.ts` — multipart proxy
- [x] `web/src/lib/api.ts` — `gateway.listContracts/getContract/createContract` helpers
- [x] Header nav: thêm "Hợp đồng" link cho admin/super_admin (`web/src/app/(app)/layout.tsx`)

### Deploy + Smoke test

- [x] Commit + push
- [x] Cosmos: git pull + run migration + rebuild gateway/agent/web
- [x] Restart containers
- [ ] Upload PDF hợp đồng lao động mẫu _(user smoke test pending)_
- [ ] Verify: raw text hiển thị + extracted fields đầy đủ _(user smoke test pending)_
- [ ] Verify: `/contracts` list hiện 1 row _(user smoke test pending)_

---

## Phase 2 — Checklist (1 tuần) ✅ commit `f0e71b4`

### Schema — Phase 2

- [x] `infra/postgres/init/08_contract_checklist.sql`:
  - [x] Bảng `contract_checklist_items` với cột status / due_date / assignee_email
  - [x] Trigger auto-stamp `completed_at` khi status → done (và clear khi rời done)
- [x] Migration trên cosmos

### Gateway — Phase 2

- [x] `gateway/src/Aiq.Gateway/Contracts/IChecklistRepository.cs` (Dapper, ownership check)
- [x] Endpoints:
  - [x] `POST /contracts/{id}/checklist` — bulk insert (với `replaceExisting` flag thay vì generate riêng)
  - [x] `GET /contracts/{id}/checklist` — list items
  - [x] `PATCH /checklist/{itemId}` — update status / due_date / assignee
  - [x] `DELETE /checklist/{itemId}`
- [x] AI generation chạy qua `/api/contracts/[id]/checklist/generate` (web → agent.runTask)

### Agent — Phase 2

- [x] `agent/skills/contract-checklist-builder/SKILL.md` — rules cho mỗi loại clause
- [x] MCP tools `get_contract` + `save_checklist_items` (clause_ordinal → clause_id mapping)
- [x] Wire vào `tools.py` + `runner.py` allowed_tools

### Web — Phase 2

- [x] Trong `/contracts/[id]`: section "Checklist" với UI interactive (`ContractChecklist` client component)
- [x] Button "AI sinh checklist" (gọi generate endpoint)
- [x] Checkbox toggle + status dropdown + delete → PATCH/DELETE on change
- [x] Filter pills: Tất cả / Mở / Sắp hạn / Quá hạn / Hoàn tất với counts

### Smoke test — Phase 2

- [ ] Vào contract đã upload Phase 1 → bấm "AI sinh checklist" _(user)_
- [ ] Verify: 5-15 items với due date hợp lý _(user)_
- [ ] Check/uncheck → reload page → state persist _(user)_
- [ ] Filter "Due soon" hiển thị đúng _(user)_

---

## Phase 3 — Notification + Tracking (3-5 ngày) ✅ commit `cebf063`

### Schema — Phase 3

- [x] `infra/postgres/init/09_notifications.sql`: bảng `notifications` + unique `(user_id, kind, ref_id)` để dedupe cron re-runs

### Gateway — Hangfire job

- [x] `gateway/src/Aiq.Gateway/Jobs/ContractDeadlineJob.cs`
- [x] Recurring schedule: daily 07:00 VN (`RecurringJob.AddOrUpdate` với TZ `Asia/Ho_Chi_Minh`)
- [x] Logic: JOIN checklist_items + contracts → emit `checklist_overdue` / `checklist_due_soon` per item
- [x] Idempotent qua `ON CONFLICT (user_id, kind, ref_id) DO NOTHING`
- [ ] Test trigger manual qua `/jobs/dashboard` _(user — JobsOptions.DashboardPath cần set)_

### Gateway — Endpoints — Phase 3

- [x] `GET /notifications` — list cho user (with `?unread=true&limit=N`)
- [x] `PATCH /notifications/{id}/read` — mark as read
- [x] `GET /notifications/unread-count`
- [x] `POST /notifications/mark-all-read` (bonus)

### Notification channels (chọn theo Decision #1)

#### In-app (always on) ✅

- [x] Web: bell icon trong header với badge unread count (`NotificationBell` component)
- [x] `web/src/app/(app)/notifications/page.tsx` — list + mark as read
- [x] Poll unread count mỗi 60s

#### Email SMTP (nếu chọn) — chưa làm

- [ ] Cài SMTP provider (Brevo / SendGrid / Mailgun) — cần ENV `SMTP_*`
- [ ] Gateway: `IEmailSender` interface + impl
- [ ] Hangfire job nhánh: nếu user có email → send template

#### Telegram bot (nếu chọn) — chưa làm

- [ ] Tạo bot qua @BotFather, lấy token
- [ ] User binding: mỗi user nhập chat_id vào profile
- [ ] Gateway: HTTP POST → Telegram Bot API

#### Zalo OA (nếu chọn) — chưa làm

- [ ] Cấu hình ZNS — cần Zalo certificate
- [ ] Template message approval

### Smoke test — Phase 3

- [ ] Trigger Hangfire job qua dashboard (hoặc đợi 07:00 VN) _(user)_
- [ ] Verify: bell icon hiện badge ≥ 1 _(user)_
- [ ] Click → `/notifications` page list items _(user)_
- [ ] Mark as read → badge giảm _(user)_

---

## Deploy + Documentation

- [x] Commit + push từng phase riêng để rollback dễ (`e72bf58` / `f0e71b4` / `cebf063`)
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
