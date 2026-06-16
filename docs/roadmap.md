# Roadmap — AIQ Agentic AI

> Roadmap ngắn gọn để team cùng align. Không phải log từng commit — đó là `implement_progress_status.md`.

---

## Where we are today

Live tại **`cattuong.ai`** với MVP đầy đủ:
- 3 subdomains (app / api / agent) qua Cloudflare Proxied → NPM → Docker containers trên Contabo Cosmos VPS
- Chat AI dùng Claude Haiku 4.5 với RAG từ user notes
- Knowledge base có CRUD, bulk MD upload, save-from-chat
- Multi-user qua Clerk invite, JWT bridge với Gateway
- Chat history sidebar + auto-restore

**17 features đã ship, 2 đang fix trong PR hiện tại** (title bug + role refactor).

---

## Q3 milestones

- [x] **M1 — MVP infrastructure** (Docker stack, observability, deploy pipeline)
- [x] **M2 — AI chat** (Claude Haiku 4.5, PEC orchestrator, Markdown UI với thinking step)
- [x] **M3 — Personal KB** (Notes CRUD, RAG injection, bulk MD upload, save-from-chat)
- [x] **M4 — Multi-user** (Clerk invite-only, JWT bridge, chat history per user, thread namespacing)
- [ ] **M5 — Roles & permissions** (super_admin / admin / user) ← **THIS PR**
- [ ] **M6 — Scalable RAG** (Qdrant embeddings, top-K retrieval, dùng `pgvector` hoặc Qdrant)
- [ ] **M7 — Ingestion at scale** (URL crawler, CSV import, Notion sync)

## Q4 milestones (tentative)

- [ ] **M8 — Multi-tenant workspace** (org / team boundary, billing)
- [ ] **M9 — Specialist personas** (chuyên gia domain: legal / med / dental / finance)
- [ ] **M10 — Analytics** (top câu hỏi, KB coverage gaps, user activity heatmap)
- [ ] **M11 — Mobile-friendly + PWA** (responsive layout, offline read)

---

## Right-now backlog (sorted by ROI)

| # | Task | Size | ROI rationale |
|---|---|---|---|
| 1 | Voice input STT tiếng Việt — Option A Web Speech API | S | Chuyên gia bận, gõ chậm; voice tăng tốc nhập tri thức. Chi tiết: [stt_voice_input_decision.md](stt_voice_input_decision.md) |
| 2 | Qdrant embeddings + top-K | L | RAG scale từ ~50 notes → unlimited |
| 3 | URL crawler ingestion | M | "Quickly transfer info" use case |
| 4 | CSV import for catalogs | S | Sales-team workflow |
| 5 | Per-user analytics | M | Biết user hỏi gì, lỗ hổng KB ở đâu |
| 6 | CI/CD GitHub Actions | M | Replace manual SSH, enable rollback |
| 7 | Voice input — Option B (Whisper API) | M | Upgrade nếu A không đủ |
| 8 | Voice input — Option C (PhoWhisper self-host) | L | Privacy + fine-tune cho thuật ngữ chuyên ngành |

S = ≤1 day · M = 2–5 days · L = 1+ week

---

## Decisions log

| Date | Decision | Why |
|---|---|---|
| 2026-06-10 | Chọn Anthropic Haiku 4.5 thay vì Ollama local | Cost / latency tốt nhất ở quy mô MVP, không cần GPU |
| 2026-06-10 | Clerk thay vì self-hosted Auth | Magic-link + invite-only OOTB, giảm code auth tự viết |
| 2026-06-11 | Single Redis cho memory (không Postgres) | TTL'd, đủ dùng tới ~10K daily active threads |
| 2026-06-11 | pgvector ext bật sẵn nhưng chưa wire | Sẵn cho M6, không tốn gì thêm khi để extension on |
| 2026-06-11 | Bulk MD upload thay vì paste single | Bootstrapping KB nhanh hơn 50× |
| 2026-06-12 | 3-tier roles (super_admin / admin / user) | User explicit: nhập liệu vs tiêu thụ vs quản trị |
| 2026-06-12 | Giữ legacy roles `reviewer/uploader/viewer` trong DB | Tránh foreign-key cũ vỡ, không gây hại |

---

## Known issues / debt

- Old chat threads keep ugly title sau title-fix (Redis data, không migrate ngược)
- Legacy Postgres roles vẫn còn → tạp nhưng safe
- Web build cần `--legacy-peer-deps` cho React 19
- Manual SSH deploy → có rủi ro rollback chậm
- RAG full-context cap 60K chars → đủ tới ~200 notes, vỡ ở ~500

---

## Definition of Done (per item)

Một item chuyển từ ⏳ planned → ✅ live khi đạt:

- [x] Code merged to `master`
- [x] Deployed to `cattuong.ai` (cosmos VPS)
- [x] Smoke test 200 OK + screenshot trong commit/PR description
- [x] Docs updated (`implement_progress_status.md` + nếu architectural thì cập nhật `architecture_structure.md`)
- [x] Hai bảng (progress + roadmap) đồng bộ

---

## How we work

- **Branch model**: `master` deploy thẳng (no staging). Hot-fixes go straight in.
- **Reviews**: optional cho MVP; mandatory khi có collaborator thứ 2 join
- **Communication**: doc files này là source of truth, đừng dựa vào memory hay chat thoáng qua
- **Cadence**: review roadmap + progress mỗi 2 tuần
