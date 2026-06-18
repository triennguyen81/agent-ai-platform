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

**25 features đã ship** (qua Phase 1-4 Strategy B). Sẵn sàng cho **Accounting Mức 1** (kế hoạch trong [Ketoan_plan.md](Ketoan_plan.md)).

---

## Q3 milestones

- [x] **M1 — MVP infrastructure** (Docker stack, observability, deploy pipeline)
- [x] **M2 — AI chat** (Claude Haiku 4.5, PEC orchestrator, Markdown UI với thinking step)
- [x] **M3 — Personal KB** (Notes CRUD, RAG injection, bulk MD upload, save-from-chat)
- [x] **M4 — Multi-user** (Clerk invite-only, JWT bridge, chat history per user, thread namespacing)
- [x] **M5 — Roles & permissions** (super_admin / admin / user)
- [x] **M-Voice** — Speech-to-Text tiếng Việt (Web Speech API, MicButton 3 surfaces)
- [x] **M-Skills** — Strategy B done: skill loader + Claude Agent SDK + `/agent/run` + Agentic UI + authoring guide
- [ ] **M-Accounting** — Mức 1 (4 SKILL.md kế toán + 8 KB notes) ← **NEXT**
- [ ] **M6 — Scalable RAG** (Qdrant embeddings, top-K retrieval, dùng `pgvector` hoặc Qdrant)
- [ ] **M7 — Ingestion at scale** (URL crawler batch, CSV import, Notion sync — `/agent/run` đã single-URL được)

## Q4 milestones (tentative)

- [ ] **M8 — Multi-tenant workspace** (org / team boundary, billing)
- [ ] **M9 — Specialist personas** (chuyên gia domain: legal / med / dental / finance)
- [ ] **M10 — Analytics** (top câu hỏi, KB coverage gaps, user activity heatmap)
- [ ] **M11 — Mobile-friendly + PWA** (responsive layout, offline read)

---

## Right-now backlog (sorted by ROI)

| # | Task | Size | ROI rationale |
|---|---|---|---|
| 1 | Accounting persona Mức 1 (4 SKILL.md + 8 KB notes) | S | Đầu use case mới ngoài dental. Plan đầy đủ: [Ketoan_plan.md](Ketoan_plan.md) |
| 2 | Qdrant embeddings + top-K | L | RAG scale từ ~50 notes → unlimited |
| 3 | Accounting Mức 2 (tools: calculate_vat / pit / lookup_account / validator) | M | Sau Mức 1 pilot OK, AI thực sự tính chứ không chỉ tra cứu |
| 4 | URL crawler batch (multiple URLs) | M | Single URL đã work qua /agent/run; cần batch + dedupe |
| 5 | CSV import for catalogs | S | Sales/accounting workflow |
| 6 | Per-user analytics | M | Biết user hỏi gì, lỗ hổng KB ở đâu |
| 7 | CI/CD GitHub Actions | M | Replace manual SSH, enable rollback |
| 8 | Voice input — Option B (Whisper API) | M | Upgrade nếu A không đủ chất lượng |
| 9 | Voice input — Option C (PhoWhisper self-host) | L | Privacy + fine-tune cho thuật ngữ chuyên ngành |
| 10 | Skills Marketplace UI (M-Skills-2..5) | L | Browse/enable/version skills từ /admin/skills |

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
| 2026-06-12 | Voice input → Strategy A Web Speech API trước | $0, 30 phút wire; upgrade Whisper API sau nếu user phàn nàn |
| 2026-06-18 | Strategy B (Augment) cho Claude Agent SDK | Plain chat path giữ tốc độ; agentic chỉ kích hoạt khi cần multi-step |
| 2026-06-18 | Skills là file Markdown ngoài wheel | Sửa skill = restart container (giây), không cần re-wheel Python |
| 2026-06-18 | Tools factory bound JWT trong closure | Mỗi /agent/run request có tools riêng, không leak token cross-user |
| 2026-06-18 | Pin `prometheus-fastapi-instrumentator<8.0` | 8.0.0 crash FastAPI 0.115+ với `_IncludedRouter has no attribute path` |
| 2026-06-18 | Lora serif font với vietnamese subset | Fix decompose tone-mark trên Linux containers ("Bă´t" → "Bắt") |

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
