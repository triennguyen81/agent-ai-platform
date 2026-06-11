# Architecture Overview

> **Status:** Reflects the Phase 1–4 implementation as of 2026-06-10.
> When the topology changes, update this document in the same PR.

---

## Layered architecture

```
┌────────────────────────────────────────────────────────────────┬──────────┐
│ Presentation (future — Next.js dashboard)                       │          │
├────────────────────────────────────────────────────────────────┤          │
│ Caddy reverse proxy · TLS termination · WAF headers · /agent/* │          │
│ routing                                                          │          │
├────────────────────────────────────────────────────────────────┤          │
│ Gateway — ASP.NET Core 9 (gateway/)                              │          │
│  ├── JWT auth + RBAC policies + per-user rate limiting          │ Security │
│  ├── File-upload guard (size + MIME + magic bytes)              │  cross-  │
│  ├── Books CRUD + Hangfire OCR job scheduler                    │  cutting │
│  └── Audit middleware + structured JSON logs + OTel + Prom      │          │
├────────────────────────────────────────────────────────────────┤          │
│ Agent — Python 3.12 + FastAPI (agent/)                          │          │
│  ├── LLM provider abstraction (Ollama / Anthropic)              │          │
│  ├── Tool / function-calling registry                           │          │
│  ├── Planner-Executor-Critic orchestrator                       │          │
│  ├── OCR pipeline (PaddleOCR → diacritic correction → pgvector) │          │
│  ├── Conversation memory (in-memory / Redis)                    │          │
│  └── OTel tracing + Prom counters + LLM cost recorder           │          │
├────────────────────────────────────────────────────────────────┤          │
│ Data & Infrastructure                                            │          │
│  Postgres (pgvector) · Qdrant · Redis · MinIO/S3 (future)       │          │
│  Prometheus · Loki + Promtail · Grafana                         │          │
└────────────────────────────────────────────────────────────────┴──────────┘
```

| Layer | Responsibility | Implementation |
|-------|---------------|----------------|
| Presentation | UI for end-users (uploaders, reviewers) | Next.js (planned) |
| Gateway | Edge routing, auth, validation, job scheduling | ASP.NET Core 9 |
| Agent | LLM-backed reasoning, OCR pipeline | Python 3.12 + FastAPI |
| Data | Relational, vector, cache, blob | Postgres 16 + Qdrant 1.12 + Redis 7 + MinIO/S3 |
| Observability | Traces, metrics, logs, dashboards | OpenTelemetry + Prometheus + Loki + Grafana |
| Security | Cross-cutting controls | JWT, RBAC, ACL, audit log, rate limiting, encryption |

---

## Request flow — Book upload + OCR

```
Client                    Caddy             Gateway              Hangfire        Agent             Postgres
  │                         │                  │                    │              │                  │
  │ POST /books/{id}/pages  │                  │                    │              │                  │
  ├────────TLS+JWT─────────►│──────────────────►│                    │              │                  │
  │                         │                  │ FileUploadGuard    │              │                  │
  │                         │                  │ (size, MIME, %PDF) │              │                  │
  │                         │                  │ BookAccessControl  │              │                  │
  │                         │                  │ (sub == uploader?) │              │                  │
  │                         │                  │ Stage PDF on disk  │              │                  │
  │                         │                  │ Enqueue OCR job ──►│              │                  │
  │ 202 Accepted + jobId    │◄──────────────────│                    │              │                  │
  │                         │                  │                    │ Run OCR ─────►│                  │
  │                         │                  │                    │              │ Render PDF       │
  │                         │                  │                    │              │ PaddleOCR        │
  │                         │                  │                    │              │ LLM correct      │
  │                         │                  │                    │              │ pgvector verify  │
  │                         │                  │                    │◄──── result──│                  │
  │                         │                  │                    │ Upsert pages ├─────────────────►│
  │                         │                  │                    │ status=processed                 │
  │                         │                  │                    │              │                  │
  │ GET /books/{id}/jobs/{jobId}               │                    │              │                  │
  ├────────────────────────►│──────────────────►│ Hangfire JobStorage│              │                  │
  │ 200 + state             │◄──────────────────│                    │              │                  │
```

Key decisions:

- The Gateway never blocks on the LLM. Synchronous OCR (≥ minutes for large
  PDFs) would saturate Kestrel; Hangfire owns the long job.
- The PDF is staged to disk under a fresh GUID. Original filename never
  touches storage — defeats a class of path-injection bugs.
- `BookRepository.UpsertPagesAsync` uses `ON CONFLICT DO UPDATE`, so re-runs
  of the same job are idempotent. Hangfire's `[AutomaticRetry(2)]` relies on
  this.

## Request flow — Chat with memory

```
Client                Gateway/Caddy            Agent                 Redis              LLM (Ollama/Anthropic)
  │                       │                      │                     │                       │
  │ POST /agent/chat      │                      │                     │                       │
  │  {prompt, thread_id}  │                      │                     │                       │
  ├──────────────────────►│ /agent/* strip ─────►│                     │                       │
  │                       │                      │ memory.get(tid)────►│                       │
  │                       │                      │◄── prior history ───│                       │
  │                       │                      │ PEC: planner ──────────────────────────────►│
  │                       │                      │ executor (tool?)    │                       │
  │                       │                      │ critic ───────────────────────────────────►│
  │                       │                      │ memory.append(tid, user+assistant)─►│       │
  │ 200 + final_answer    │                      │                     │                       │
  │  + steps + history_n  │                      │                     │                       │
  │◄──────────────────────│◄─────────────────────│                     │                       │
```

`RecordedLlmService` wraps the chosen provider in `main.py`'s lifespan, so
every Planner / Executor / Critic call increments
`aiq_llm_tokens_total{provider, model, direction}`. Operators see real cost
in Grafana within 15 seconds of the call landing.

## Data flow — Postgres schema highlights

```
auth/users ──< user_roles >── auth/roles
                │
                │ uploaded_by
                ▼
            books ───<──< book_pages ───<──< corrections
                                                │
                                                │ reviewed_by
                                                ▼
                                            auth/users

embeddings (vector(1024)) — backup of Qdrant; rebuilds the collection from
truth if Qdrant storage is lost (see disaster-recovery.md).

audit_log — written by AuditMiddleware on every state-changing request.
```

Indexes (`infra/postgres/init/03_indexes.sql`):

- `idx_books_uploaded_by` — partial WHERE NOT NULL — backs ACL lookup
- `idx_books_status_created_at` — backs paginated list filter
- `idx_corrections_page_id` — review queue lookups
- `idx_embeddings_vector_cosine` — IVFFlat lists=100 for nearest-neighbour

## Cross-cutting concerns

| Concern | Where it lives |
|---------|----------------|
| Tracing context propagation | W3C `traceparent` via OTel SDK (gateway HttpClient ↔ agent FastAPI) |
| Metrics | `/metrics` on gateway:8080 and agent:8000, scraped every 15s by Prometheus |
| Logs | stdout JSON (gateway), stdout text (agent), tailed by Promtail → Loki |
| Audit | `audit_log` table — fire-and-forget from `AuditMiddleware` |
| Cost tracking | `aiq_llm_tokens_total` counter via `RecordedLlmService` decorator |
| Secrets | env vars in dev, Vault/KMS in prod (see `encryption-at-rest.md`) |
| Backup | `infra/backup/*.sh` driven by cron/systemd-timer |

## What's intentionally NOT here yet

- **Presentation layer**: no Next.js dashboard. Clients consume the JSON API
  directly until Phase 5.
- **Multi-region**: single compose stack. Cross-region replication will land
  with the production deployment kit.
- **OIDC/SSO**: JWT bearer only. Federated auth is Phase 5.
- **MinIO/S3**: `gateway_uploads` volume holds raw PDFs; object storage
  swap-in is planned but not wired.

---

_Last verified: 2026-06-10. Bump this line when you touch the diagram._
