# AIQ Agentic AI Platform

Nền tảng Agentic AI theo kiến trúc phân tầng (Presentation · Gateway · Runtime · Data) với Security xuyên suốt. Use case khởi đầu: pipeline OCR sửa lỗi tiếng Việt cho thư viện Cơ Đốc.

> Xem chi tiết kế hoạch tại [plan (3).md](./plan%20%283%29.md) và tiến độ tại [progress-check.md](./progress-check.md).

---

## Stack hiện tại (Phase 1 — Tuần 1)

| Thành phần | Image | Vai trò |
|------------|-------|---------|
| PostgreSQL + pgvector | `pgvector/pgvector:pg16` | Relational + backup vector index |
| Qdrant | `qdrant/qdrant:v1.12.4` | Vector DB chính (embeddings) |
| Redis | `redis:7-alpine` | Cache & queue backing |

Gateway (ASP.NET Core), Application/Agent runtime, và Observability sẽ thêm ở các tuần kế tiếp.

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

# 3. Khởi động hạ tầng
docker compose up -d

# 4. Kiểm tra health
docker compose ps
```

Sau khi container `aiq-postgres` `healthy`, schema khởi tạo trong `infra/postgres/init/` sẽ được apply tự động (chỉ chạy lần đầu khi volume trống).

---

## Endpoints local

| Service | URL / Port |
|---------|------------|
| PostgreSQL | `localhost:5432` (user/db trong `.env`) |
| Qdrant HTTP | http://localhost:6333 |
| Qdrant gRPC | `localhost:6334` |
| Redis | `localhost:6379` (cần password) |

Smoke test:

```bash
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
- [ ] **Tuần 2:** API Gateway (ASP.NET Core + JWT)
- [ ] **Tuần 3:** Agent skeleton (Qwen3 qua Ollama/MLX)
- [ ] **Tuần 4:** OCR pipeline test đầu tiên (PDF → extract → correct)

---

## License

TBD.
