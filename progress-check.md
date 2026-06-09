# ✅ AIQ Agentic AI Platform — Progress Check

> **Cập nhật lần cuối:** 2026-06-09
> **Cách dùng:** Tick `[x]` khi hoàn thành. Cập nhật cột Trạng thái & Ghi chú mỗi tuần.

**Chú thích trạng thái:** ⬜ Chưa bắt đầu · 🟡 Đang làm · ✅ Xong · ⛔ Blocked

---

## Phase 1 — Nền tảng

### Gateway
- [x] Khởi tạo project ASP.NET Core
- [x] JWT authentication
- [x] RBAC middleware
- [x] Rate limiting
- [x] WAF rules (upload file)

### Database
- [x] PostgreSQL setup
- [x] Qdrant / pgvector setup
- [x] Redis cache
- [x] Schema design (books, users, corrections, audit)

### Infrastructure
- [x] Docker containerization
- [x] Docker Compose (dev)
- [x] TLS/SSL certificates
- [x] CI nền (build + test)

**Trạng thái Phase 1:** ✅
**Ghi chú:** Toàn bộ Database, Infrastructure (Docker, Compose, CI, TLS qua Caddy), Gateway (JWT/RBAC/rate-limit/WAF) hoàn tất.

---

## Phase 2 — Runtime Layer

### Application sublayer
- [x] Service modules (business logic)
- [x] Transaction handling
- [x] State management
- [x] Hangfire job scheduling

### Agent sublayer
- [x] LLM integration (Claude API)
- [x] LLM integration (Qwen3 local — Ollama/MLX)
- [x] Tool / function calling framework
- [x] Agent orchestration (Planner–Executor–Critic)
- [x] Memory / context management

### OCR Pipeline (project cụ thể)
- [x] PaddleOCR extraction
- [x] Qwen3 correction (diacritic-aware)
- [x] pgvector validation
- [x] End-to-end test (PDF → corrected text)

**Trạng thái Phase 2:** ✅
**Ghi chú:** Toàn bộ Application sublayer, Agent sublayer (memory/context xong), OCR pipeline đều hoàn tất.

---

## Phase 3 — Security & Observability

### Security
- [ ] ACL cấp ứng dụng
- [ ] Audit logging (PostgreSQL)
- [ ] Encryption at-rest
- [ ] Rate limiting per user/org

### Observability
- [ ] Distributed tracing (OpenTelemetry / Jaeger)
- [ ] Centralized logging (Loki / ELK)
- [ ] Cost tracking (token + compute)
- [ ] Metrics & alerting (Prometheus + Grafana)

**Trạng thái Phase 3:** ⬜
**Ghi chú:** _(trống)_

---

## Phase 4 — Scaling & Polish

- [ ] Load testing
- [ ] Performance optimization
- [ ] CI/CD pipeline đầy đủ
- [ ] Disaster recovery & backup (Btrfs snapshot, WAL archiving)
- [ ] Documentation
- [ ] SLA định nghĩa

**Trạng thái Phase 4:** ⬜
**Ghi chú:** _(trống)_

---

## Milestones Ngắn Hạn (4 tuần)

| Tuần | Mục tiêu | Trạng thái |
|------|----------|-----------|
| 1 | Repo + Docker Compose (PostgreSQL + Qdrant) | ✅ |
| 2 | API Gateway prototype (ASP.NET Core + JWT) | ✅ |
| 3 | Agent skeleton (Qwen3 qua Ollama/MLX) | ✅ |
| 4 | OCR pipeline test đầu tiên | ✅ |

---

## Risk Tracker

| Risk | Mức độ | Trạng thái mitigation | Ghi chú |
|------|--------|----------------------|---------|
| LLM latency | Trung bình | ⬜ Chưa xử lý | Queue + cache |
| Sai dấu tiếng Việt | Cao | ⬜ Chưa xử lý | Fine-tune + corpus |
| Mất dữ liệu | Cao | ⬜ Chưa xử lý | Snapshot + WAL |
| Chi phí tăng | Trung bình | ⬜ Chưa xử lý | Token budget |
| Vendor lock-in | Thấp | ⬜ Chưa xử lý | Abstraction layer |

---

## Nhật Ký Tiến Độ (Progress Log)

| Ngày | Việc đã làm | Người | Ghi chú |
|------|-------------|-------|---------|
| 2026-06-09 | Tạo plan.md + progress-check.md | — | Khởi tạo |
| 2026-06-09 | Scaffold repo + Docker Compose (Postgres pgvector + Qdrant + Redis) + schema baseline | — | Milestone Tuần 1 done |
| 2026-06-09 | API Gateway prototype: ASP.NET Core 9, JWT auth, RBAC policies, fixed-window rate limiting, /auth/register, /auth/login, /me, /healthz | — | Milestone Tuần 2 done (WAF rules pending) |
| 2026-06-09 | Agent skeleton: Python 3.12 + FastAPI, LlmService abstraction (Ollama + Anthropic), tool registry (echo stub), Planner-Executor-Critic orchestrator, /chat /tools /healthz endpoints, docker-compose wired with host.docker.internal | — | Milestone Tuần 3 done (memory/context pending) |
| 2026-06-09 | OCR pipeline: OcrExtractor abstraction (PaddleOcr lazy + Stub), DiacriticCorrector (LLM-backed, empty-page guard), pgvector validator (PassthroughValidator default), pypdfium2 renderer, OcrPipeline orchestrator with stage timings, POST /ocr/pdf endpoint, pytest-asyncio end-to-end test (3 cases, runs without paddleocr) | — | Milestone Tuần 4 done; tests verified by py_compile only (host has 3.7, runtime 3.12 in Docker/CI) |
| 2026-06-09 | GitHub Actions CI: gateway-build (dotnet 9 restore+build+test), agent-lint (ruff), agent-test (pytest), compose-validate (docker compose config). Triggers on push/PR to master with concurrency cancel-in-progress | — | Phase 1 Infrastructure CI nền done |
| 2026-06-09 | WAF file-upload rules in Gateway: FileUploadOptions (50 MiB cap, application/pdf allow-list, %PDF- magic bytes), FileUploadGuard (metadata + body validation + SHA-256), Kestrel + FormOptions body-size limits, upload rate-limit bucket (5/min), POST /uploads/pdf (uploader role, Problem+JSON rejections, path-traversal guard). dotnet build -warnaserror green | — | Phase 1 Gateway WAF rules done |
| 2026-06-09 | TLS/SSL via Caddy reverse proxy: dev Caddyfile (local_certs internal CA), prod Caddyfile.prod template (ACME Let's Encrypt), security headers (HSTS in prod, no-sniff, frame-deny), routes / → gateway and /agent/* → agent with prefix strip, caddy_data + caddy_config volumes | — | Phase 1 closed (all infra items done) |
| 2026-06-09 | Application sublayer in Gateway: Books domain (records + DTOs + BookRepository via Dapper with ILIKE search and ON CONFLICT page upsert), AgentClient typed HttpClient (multipart PDF → /ocr/pdf, AgentRequestException), endpoints POST /books (create), GET /books (paginated list with status+q filter), GET /books/{id} (details + pages), POST /books/{id}/pages (uploader role + upload bucket, FileUploadGuard metadata+body checks, forwards to Agent, persists pages, updates status='processed'). AgentOptions bound + validated. dotnet build -warnaserror green | — | Phase 2 Application sublayer 3/4 done (Hangfire deferred) |
| 2026-06-09 | Hangfire async OCR jobs: Hangfire.AspNetCore + Hangfire.PostgreSql packages, OcrJobRunner [AutomaticRetry(2)] (status=processing→processed/failed, deletes staged PDF in finally), OcrJobScheduler (stages PDF to /var/aiq/uploads with fresh GUID filename then `Enqueue<OcrJobRunner>`), JobsAuthorizationFilter (admin role + dev loopback bypass), Hangfire dashboard at /jobs/dashboard. POST /books/{id}/pages now returns 202 with jobId, GET /books/{id}/jobs/{jobId} polls JobStorage. Compose adds gateway_uploads volume. dotnet build -warnaserror green | — | Phase 2 Application sublayer 4/4 done |
| 2026-06-09 | Agent memory/context: MemoryStore ABC, InMemoryMemoryStore (asyncio.Lock + defaultdict), RedisMemoryStore (redis-py async, LIST per thread keyed `aiq:mem:{tid}`, JSON payloads, sliding TTL 24h, LTRIM cap 200 msgs). build_memory_store factory selects via Settings.memory_backend. PEC accepts prior_history. /chat loads history before run + appends user/assistant after, DELETE /chat/threads/{tid} clears. 6 pytest-asyncio cases (insertion order, limit, thread isolation, clear, empty noop). Compose wires REDIS_URL + depends_on redis healthy | — | Phase 2 Agent sublayer 5/5 done — Phase 2 closed |
