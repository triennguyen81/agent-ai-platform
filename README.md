# AIQ Agentic AI Platform

Nền tảng Agentic AI theo kiến trúc phân tầng (Presentation · Gateway · Runtime · Data) với Security xuyên suốt. Use case khởi đầu: pipeline OCR sửa lỗi tiếng Việt cho thư viện Cơ Đốc.

> Xem chi tiết kế hoạch tại [plan (3).md](./plan%20%283%29.md) và tiến độ tại [progress-check.md](./progress-check.md).

---

## Stack hiện tại (Phase 1 — Tuần 1–2, Phase 2 — Tuần 3 partial)

| Thành phần | Image / Stack | Vai trò |
|------------|---------------|---------|
| PostgreSQL + pgvector | `pgvector/pgvector:pg16` | Relational + backup vector index |
| Qdrant | `qdrant/qdrant:v1.12.4` | Vector DB chính (embeddings) |
| Redis | `redis:7-alpine` | Cache & queue backing |
| API Gateway | ASP.NET Core 9 (`gateway/`) | JWT auth, RBAC, rate limiting, health |
| Agent runtime | Python 3.12 + FastAPI (`agent/`) | LLM (Qwen3/Claude), tools, Planner-Executor-Critic |

OCR pipeline cụ thể và Observability sẽ thêm ở các tuần kế tiếp.

---

## Yêu cầu

- Docker Desktop / Docker Engine >= 24
- Docker Compose v2 (built-in `docker compose`)
- ~4 GB RAM free cho 3 service

---

## Khởi chạy nhanh

```bash
# 1. Clone
git clone https://github.com/tuongna247/agent_claude_sdk.git
cd agent_claude_sdk

# 2. Cấu hình env
cp .env.example .env
# Sửa các giá trị changeme_* trong .env

# 3. Khởi động hạ tầng + gateway
docker compose up -d --build

# 4. Kiểm tra health
docker compose ps
curl http://localhost:8080/healthz/ready
```

Sau khi container `aiq-postgres` `healthy`, schema khởi tạo trong `infra/postgres/init/` sẽ được apply tự động (chỉ chạy lần đầu khi volume trống). Gateway sẽ khởi động sau khi Postgres healthy.

---

## Endpoints local

| Service | URL / Port |
|---------|------------|
| Gateway | http://localhost:8080 |
| Agent | http://localhost:8001 |
| PostgreSQL | `localhost:5432` (user/db trong `.env`) |
| Qdrant HTTP | http://localhost:6333 |
| Qdrant gRPC | `localhost:6334` |
| Redis | `localhost:6379` (cần password) |

Smoke test:

```bash
# Gateway
curl http://localhost:8080/healthz/live
curl http://localhost:8080/healthz/ready

# Register + login flow
curl -X POST http://localhost:8080/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email":"demo@aiq.local","password":"changeme123","displayName":"Demo"}'

curl -X POST http://localhost:8080/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"demo@aiq.local","password":"changeme123"}'

# Agent (Planner-Executor-Critic, default tool=echo)
curl http://localhost:8001/healthz/ready
curl http://localhost:8001/tools
curl -X POST http://localhost:8001/chat \
  -H "Content-Type: application/json" \
  -d '{"prompt":"Echo hello to confirm tools work","tool":"echo","tool_arguments":{"message":"hi"}}'

# Postgres
docker exec -it aiq-postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "\dt"

# Qdrant
curl http://localhost:6333/collections

# Redis
docker exec -it aiq-redis redis-cli -a "$REDIS_PASSWORD" ping
```

---

## Cấu trúc thư mục

```
.
├── docker-compose.yml
├── .env.example
├── infra/
│   └── postgres/
│       └── init/
│           ├── 01_extensions.sql   # pgvector, pg_trgm, pgcrypto, uuid-ossp
│           └── 02_schema.sql       # users, roles, books, corrections, audit
├── gateway/                        # ASP.NET Core 9 API Gateway
│   ├── Aiq.Gateway.sln
│   ├── .dockerignore
│   └── src/Aiq.Gateway/
│       ├── Aiq.Gateway.csproj
│       ├── Program.cs              # composition root, JWT, rate limiting
│       ├── Configuration/JwtOptions.cs
│       ├── Auth/                   # JWT, BCrypt hasher, DTOs
│       ├── Data/                   # Npgsql + Dapper user repository
│       ├── Endpoints/              # /auth, /healthz
│       └── Dockerfile
├── agent/                          # Python 3.12 + FastAPI Agent runtime
│   ├── pyproject.toml              # base + [ocr] + [pgvector] + [dev] extras
│   ├── Dockerfile
│   ├── src/aiq_agent/
│   │   ├── main.py                 # FastAPI app + lifespan
│   │   ├── config.py               # pydantic-settings
│   │   ├── llm/                    # base + Ollama + Anthropic + factory
│   │   ├── tools/                  # function-calling registry
│   │   ├── orchestration/          # Planner-Executor-Critic
│   │   ├── ocr/                    # PDF → PaddleOCR → diacritic → pgvector
│   │   └── api/                    # /healthz, /chat, /tools, /ocr/pdf
│   └── tests/                      # pytest-asyncio unit tests
├── plan (3).md
├── progress-check.md
└── README.md
```

---

## Reset hạ tầng

```bash
docker compose down -v   # xoá volume — mất dữ liệu local
docker compose up -d
```

---

## Roadmap ngắn hạn

- [x] **Tuần 1:** Repo + Docker Compose (PostgreSQL + Qdrant + Redis) + schema baseline
- [x] **Tuần 2:** API Gateway prototype (ASP.NET Core 9 + JWT + RBAC + rate limiting)
- [x] **Tuần 3:** Agent skeleton (FastAPI + LLM abstraction + Planner-Executor-Critic + tool registry)
- [x] **Tuần 4:** OCR pipeline (PaddleOCR → diacritic correction → pgvector validate, end-to-end unit test)

---

## License

TBD.
