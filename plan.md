# Plan — Trợ lý Giám sát Hợp đồng

> Module mới cho AIQ Agentic AI. Đối tác yêu cầu tự động hóa quy trình quản lý hợp đồng.
> Tạo ngày 2026-06-25. Phiên bản v1.

---

## 1. Mục tiêu

Module **Trợ lý Giám sát Hợp đồng** với 3 chức năng cốt lõi:

1. **Trích xuất thông tin từ hợp đồng (PDF)** — upload → OCR + LLM parse → các trường: bên A/B, ngày ký, ngày hiệu lực, giá trị, deadline, điều khoản
2. **Tạo checklist từ điều khoản** — AI phân tích clause → đề xuất checklist items có due date + người phụ trách
3. **Theo dõi checklist + thông báo** — daily cron, nhắc deadline trước N ngày

Tham chiếu: workflow đối tác trong `docs/architecture/2_ai_hoadon_process.pdf` (xử lý hóa đơn 4 stages — cùng pattern).

---

## 2. Hệ thống AIQ đã có sẵn ~70% infra

| Yêu cầu | Đã có sẵn | Vị trí |
|---|---|---|
| OCR PDF → text | ✅ Đầy đủ | `agent/src/aiq_agent/ocr/` + `POST /ocr/pdf` |
| Render PDF → PNG | ✅ pypdfium2 | `agent/src/aiq_agent/ocr/pdf.py` |
| Background scheduler | ✅ Hangfire + Postgres storage | `gateway/src/Aiq.Gateway/Jobs/` |
| File staging volume | ✅ `/var/aiq/uploads` | `docker-compose.yml` |
| Domain extraction qua LLM | ✅ Skills + MCP tools | `agent/skills/`, `agent/src/aiq_agent/agentic/` |
| Semantic search clause | ✅ Qdrant + fastembed | `agent/src/aiq_agent/embeddings/` |
| Role-based access | ✅ super_admin / admin / user | Gateway authz |
| Domain UI page pattern | ✅ 5 pages `/accounting/*` | Copy structure |
| Save-to-KB modal | ✅ Shared component | `web/src/components/save-to-kb-button.tsx` |
| Audit log infra | ✅ Redis-backed | Tái dụng cho notification |

### Còn thiếu — cần build mới

| Hạng mục | Effort | Lý do |
|---|---|---|
| Schema `contracts`, `contract_clauses`, `checklist_items`, `notifications` | S | Notes table thiếu `status` / `due_date` / `assignee` |
| PDF binary upload UI | S | `bulk-md-upload.tsx` chỉ accept `.md` |
| Gateway CRUD + Repos cho 4 entity mới | M | Pattern copy từ `Notes/INoteRepository.cs` |
| Notification system | M | Greenfield — chưa có SMTP / Telegram / in-app |
| Contract skills + MCP tools | M | Theo pattern accounting đã có |
| 5 pages frontend `/contracts/*` | M | Copy structure `/accounting/*` |

---

## 3. Phases — 3 đợt

### Phase 1 — Foundation (1 tuần)

**Mục tiêu:** Upload PDF hợp đồng → OCR → AI trích xuất trường → lưu DB → list view.

**Schema mới** (`infra/postgres/init/07_contracts.sql`):

```sql
CREATE TABLE contracts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES users(id),
  title TEXT NOT NULL,
  party_a TEXT, party_b TEXT,
  signed_date DATE, effective_date DATE, end_date DATE,
  total_value NUMERIC(18,2),
  currency TEXT DEFAULT 'VND',
  source_pdf_path TEXT,
  raw_text TEXT,
  status TEXT DEFAULT 'active',  -- active / expired / terminated
  tags TEXT[] DEFAULT '{}'::text[],
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE contract_clauses (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
  ordinal INT NOT NULL,
  heading TEXT,
  body TEXT NOT NULL,
  has_deadline BOOLEAN DEFAULT FALSE,
  has_penalty BOOLEAN DEFAULT FALSE
);
```

**Backend:**
- Gateway: `ContractRepository` + `ContractEndpoints` CRUD
- Agent: skill `contract-extractor` (prompt extraction rules)
- Agent: MCP tool `extract_contract_fields(raw_text)` → structured JSON
- Agent: wrap `/ocr/pdf` để gọi extraction sau OCR

**Frontend:**
- `/contracts` — list page (status badges + search)
- `/contracts/upload` — PDF dropzone (multipart upload)
- `/contracts/[id]` — detail view (raw text + extracted fields + clauses)
- Header nav: "Hợp đồng" link cho admin / super_admin

### Phase 2 — Checklist (1 tuần)

**Mục tiêu:** Clauses → AI sinh checklist items với deadline + assignee → user check/uncheck.

**Schema bổ sung:**

```sql
CREATE TABLE contract_checklist_items (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
  clause_id UUID REFERENCES contract_clauses(id),
  title TEXT NOT NULL,
  description TEXT,
  due_date DATE,
  remind_days_before INT DEFAULT 7,
  assignee_email TEXT,
  status TEXT DEFAULT 'open',  -- open / in_progress / done / skipped
  completed_at TIMESTAMPTZ,
  completed_by UUID REFERENCES users(id)
);
```

**Backend:**
- Agent: skill `contract-checklist-builder` (rules: deadline → item, penalty → item, định kỳ thanh toán → recurring)
- Agent: MCP tool `generate_checklist(contract_id)` → list of items
- Gateway: `ChecklistRepository`, `POST /contracts/:id/generate-checklist`, `PATCH /checklist/:id`

**Frontend:**
- Trong `/contracts/[id]`: bảng checklist với checkbox + due date + assignee
- Button "AI sinh checklist"
- Filter: open / due-soon / overdue / done

### Phase 3 — Notification (3-5 ngày)

**Mục tiêu:** Daily cron check items due trong N days → notify.

**Schema:**

```sql
CREATE TABLE notifications (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES users(id),
  kind TEXT NOT NULL,  -- contract_due / checklist_overdue / system
  ref_type TEXT, ref_id UUID,
  title TEXT NOT NULL,
  body TEXT,
  read_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ DEFAULT NOW()
);
```

**Backend:**
- Hangfire recurring job (daily 07:00 VN): scan `contract_checklist_items` WHERE `status='open'` AND `due_date - remind_days_before <= today` → insert notifications
- Notification channels (chọn 1 hoặc nhiều):
  - **In-app** (always on, không cần infra ngoài)
  - Email SMTP (cần credentials)
  - Telegram bot (cần bot token)
  - Zalo OA (cần cert)

**Frontend:**
- Header: bell icon + badge số chưa đọc
- `/notifications` page list + mark as read
- `/contracts` list: badge "🔔 N items due soon"

---

## 4. 5 Quyết định cần chốt trước

1. **Notification channel ưu tiên** — In-app / Email / Telegram / Zalo (hoặc combo)
2. **Loại hợp đồng** — Generic VN / Lao động / Mua bán / Dịch vụ / Xây dựng / Khác
3. **Multi-tenant model** — Mỗi user có contract riêng vs shared org-wide
4. **PDF storage** — Volume `/var/aiq/uploads` (MVP) vs cloud (S3 / R2)
5. **Scope đợt này** — Full 3 phases (~3 tuần) vs Phase 1 trước (1 tuần)

---

## 5. Verification mỗi phase

**Phase 1:**
- Upload PDF hợp đồng lao động mẫu → raw text + extracted fields đầy đủ
- `/contracts` list hiện đúng số contracts
- Click vào contract → thấy clauses

**Phase 2:**
- Bấm "AI sinh checklist" → 5-15 items với due date hợp lý
- Check/uncheck → state persist
- Filter "due soon" → items deadline trong 7 ngày

**Phase 3:**
- Trigger cron qua Hangfire dashboard
- Header bell badge xuất hiện
- `/notifications` page có items
- Nếu chọn email → check inbox

---

## 6. Roadmap mở rộng (sau pilot)

- **Phase 4** — Multi-tenant workspace cho organization
- **Phase 5** — Contract templates + e-sign integration
- **Phase 6** — Analytics dashboard (deadline trend, KPI tuân thủ hợp đồng)
- **Phase 7** — Mobile app + push notification
- **Phase 8** — Agent-MCP refactor (theo `docs/architecture/kientruc.png`) tách Contract Server / Accounting Server / Knowledge Server

---

## 7. References

- Plan chi tiết kèm code paths: `C:\Users\tuong\.claude\plans\whimsical-weaving-dahl.md`
- Workflow đối tác (invoice): `docs/architecture/2_ai_hoadon_process.pdf`
- Architecture target: `docs/architecture/kientruc.png`
- AIQ docs hiện tại:
  - [`docs/architecture_structure.md`](docs/architecture_structure.md)
  - [`docs/implement_progress_status.md`](docs/implement_progress_status.md)
  - [`docs/roadmap.md`](docs/roadmap.md)
- Tracking: `progress-check.md` (cùng folder)
