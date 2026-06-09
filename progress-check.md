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
- [ ] WAF rules (upload file)

### Database
- [x] PostgreSQL setup
- [x] Qdrant / pgvector setup
- [x] Redis cache
- [x] Schema design (books, users, corrections, audit)

### Infrastructure
- [ ] Docker containerization
- [ ] Docker Compose (dev)
- [ ] TLS/SSL certificates
- [ ] CI nền (build + test)

**Trạng thái Phase 1:** 🟡
**Ghi chú:** Database xong (Tuần 1). Gateway prototype xong (Tuần 2) trừ WAF rules.

---

## Phase 2 — Runtime Layer

### Application sublayer
- [ ] Service modules (business logic)
- [ ] Transaction handling
- [ ] State management
- [ ] Hangfire job scheduling

### Agent sublayer
- [x] LLM integration (Claude API)
- [x] LLM integration (Qwen3 local — Ollama/MLX)
- [x] Tool / function calling framework
- [x] Agent orchestration (Planner–Executor–Critic)
- [ ] Memory / context management

### OCR Pipeline (project cụ thể)
- [ ] PaddleOCR extraction
- [ ] Qwen3 correction (diacritic-aware)
- [ ] pgvector validation
- [ ] End-to-end test (PDF → corrected text)

**Trạng thái Phase 2:** 🟡
**Ghi chú:** Agent sublayer skeleton xong (Tuần 3). Application sublayer và OCR pipeline pending.

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
| 4 | OCR pipeline test đầu tiên | ⬜ |

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
