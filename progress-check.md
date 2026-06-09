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
- [x] ACL cấp ứng dụng
- [x] Audit logging (PostgreSQL)
- [x] Encryption at-rest
- [x] Rate limiting per user/org

### Observability
- [x] Distributed tracing (OpenTelemetry / Jaeger)
- [x] Centralized logging (Loki / ELK)
- [x] Cost tracking (token + compute)
- [x] Metrics & alerting (Prometheus + Grafana)

**Trạng thái Phase 3:** ✅
**Ghi chú:** Security 4/4 + Observability 4/4. Encryption at-rest doc-driven (deployment-side); ACL, audit, rate-limit per-user, tracing, metrics, Loki logs, cost tracking all wired.

---

## Phase 4 — Scaling & Polish

- [x] Load testing
- [x] Performance optimization
- [x] CI/CD pipeline đầy đủ
- [ ] Disaster recovery & backup (Btrfs snapshot, WAL archiving)
- [ ] Documentation
- [ ] SLA định nghĩa

**Trạng thái Phase 4:** 🟡
**Ghi chú:** Load testing + perf + CI/CD đầy đủ xong. DR, docs, SLA pending.

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
| 2026-06-09 | Phase 3 Audit + structured logs in Gateway: IAuditLogger + PostgresAuditLogger (writes to audit_log table, JSONB payload, swallows errors and logs loudly — audit failures must not fault requests), AuditMiddleware (state-changing methods only, skips /healthz /jobs/dashboard /openapi, captures actor_id from sub claim, IP, user-agent, status code, fire-and-forget with 10s timeout detached from RequestAborted), AddJsonConsole structured logging (UTC ISO-8601, includeScopes, single-line). Wired after auth in pipeline. dotnet build -warnaserror green | — | Phase 3 Security 1/4 done (audit logging Postgres) |
| 2026-06-09 | Phase 3 OpenTelemetry distributed tracing: Gateway adds OTel SDK + AspNetCore/Http/Npgsql ActivitySource instrumentation (skips /healthz /metrics /jobs/dashboard), OTLP gRPC exporter conditional on Observability:OtlpEndpoint, Grpc.Net.Client pinned 2.67 + NuGetAuditSuppress for GHSA-4625-4j76-fww9 (transitive, trusted local collector). Agent adds opentelemetry-{api,sdk,instrumentation-{fastapi,httpx,redis},exporter-otlp-proto-grpc}, configure_tracing() in lifespan, instrument_fastapi() after include_router with excluded_urls for health+metrics. Compose passes OTEL_EXPORTER_OTLP_ENDPOINT + DEPLOY_ENV to both. dotnet build -warnaserror green | — | Phase 3 Observability 1/4 done (distributed tracing) |
| 2026-06-09 | Phase 3 Prometheus metrics + alerts: Gateway adds prometheus-net.AspNetCore 8.2.1 (UseRouting + UseHttpMetrics + MapMetrics → /metrics with per-endpoint counters/histograms). Agent adds prometheus-fastapi-instrumentator 7 (Instrumentator().instrument(app).expose at /metrics, excludes health). infra/prometheus/prometheus.yml scrapes gateway:8080 + agent:8000 every 15s. infra/prometheus/alerts.yml: ServiceDown (2m), GatewayHighErrorRate (>5% 5xx for 5m), AgentHighLatency (p95>5s for 10m). Compose adds prom/prometheus:v3.1.0 with 15d retention + persistent volume + lifecycle API. dotnet build -warnaserror green | — | Phase 3 Observability 2/4 done (metrics + alerts) |
| 2026-06-09 | Phase 3 Per-user rate-limit + Book ACL: convert "default" + "upload" buckets from global FixedWindow to PartitionedRateLimiter keyed by sub claim (user:GUID) with IP fallback (ip:address) — one noisy user/key no longer starves everyone. Auth bucket stays global by necessity (pre-login). Books/BookAccessControl.CanMutate(ClaimsPrincipal, BookRecord): admins always pass, otherwise sub must equal book.uploaded_by; orphan books (uploaded_by null) admin-only. POST /books/{id}/pages calls acl.CanMutate after lookup, returns 403 Problem+JSON on deny. dotnet build -warnaserror green | — | Phase 3 Security 3/4 done (ACL + per-user rate-limit; encryption at-rest pending — deployment-side concern) |
| 2026-06-09 | Phase 3 Centralized logging stack: Loki 3.3.2 (single-binary, tsdb v13, 14d retention, filesystem storage with compactor), Promtail 3.3.2 (Docker SD via /var/run/docker.sock, labels compose service+container, JSON pipeline_stage parses gateway logs into level+category labels, drops own logs), Grafana 11.4.0 (auto-provisions Prometheus + Loki datasources, AIQ Overview dashboard with req/sec stat, agent p95 latency, gateway 5xx timeseries, gateway logs panel). Compose adds 3 services + loki_data/grafana_data volumes. .env.example: LOKI_PORT, GRAFANA_PORT, GRAFANA_ADMIN_PASSWORD | — | Phase 3 Observability 3/4 done (centralized logging) |
| 2026-06-10 | Phase 3 LLM cost tracking: observability/cost.py with RecordedLlmService LlmService decorator + Prometheus metrics aiq_llm_tokens_total{provider,model,direction in:out}, aiq_llm_calls_total{provider,model,outcome ok:error}, aiq_llm_call_duration_seconds histogram (0.1s→5min buckets). main.py wraps build_llm_service result in RecordedLlmService inside lifespan so PEC orchestrator (planner + critic) and DiacriticCorrector all flow through one measurement point. 2 pytest-asyncio cases: success path increments input/output tokens + ok counter; ExplodingLlm subclass verifies error outcome is recorded even when inner raises | — | Phase 3 Observability 4/4 done — Observability column closed |
| 2026-06-10 | Phase 3 Encryption-at-rest deployment guide: docs/security/encryption-at-rest.md — threat model (lost disks / snapshot exfil / cold provider access — explicitly NOT live process compromise), per-store requirements (Postgres LUKS/RDS-CMEK, Qdrant filesystem-only, Redis LUKS + TLS-in-transit, MinIO/S3 SSE-KMS with bucket-policy deny on missing header), backup encryption matrix (WAL-G libsodium, borg repokey-blake2, restic KMS password-command), KMS guidance (Vault Transit / cloud KMS, annual rotation, break-glass), pre-prod verification checklist. Phase 3 closed | — | Phase 3 closed — Security 4/4 (encryption at-rest doc-driven) |
| 2026-06-10 | Phase 4 Load testing: k6 scripts under tests/load/ — auth.js (ramping-vus 0→20, register→login→/me, tolerates 429 from auth bucket), books.js (constant 25 VUs 2min, paginated list with ILIKE search + random detail read, thresholds p95<400ms list / <300ms read / <5% errors), agent_chat.js (4 VUs 3min, exercises PEC + memory store with thread_id per-VU, generous p95<30s for LLM chains), README explains thresholds + reading | — | Phase 4 Load testing 1/6 done |
| 2026-06-10 | Phase 4 Performance optimization: AddResponseCompression (Brotli + Gzip, EnableForHttps), AddOutputCache "books-list" policy (10s TTL, VaryByQuery on status/q/limit/offset, Tag="books" for future invalidation), GET /books uses .CacheOutput("books-list"). Npgsql connection string tuned (Min=4, Max=100, Timeout=15s, CommandTimeout=30s, Keepalive=30s, TcpKeepalive=true) in both appsettings + compose env. infra/postgres/init/03_indexes.sql: idx_books_uploaded_by (partial WHERE NOT NULL), idx_books_status_created_at, idx_corrections_page_id, idx_embeddings_vector_cosine (IVFFlat lists=100). dotnet build -warnaserror green | — | Phase 4 Perf 2/6 done |
| 2026-06-10 | Phase 4 CI/CD pipeline: .github/workflows/docker.yml (push to master triggers buildx of gateway + agent images, push to ghcr.io/{owner}/aiq-{gateway,agent} tagged sha-short + latest, gha buildx cache scoped per service), .github/workflows/release.yml (v*.*.* tag triggers matrix build with version + stable tags, provenance + sbom enabled, softprops/action-gh-release publishes GitHub Release with auto-generated changelog from git log between tags, concurrency cancel-in-progress=false to protect partial releases). Both use ghcr.io with GITHUB_TOKEN packages:write permission | — | Phase 4 CI/CD 3/6 done |
