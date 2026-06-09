# 📋 AIQ Agentic AI Platform — Kế Hoạch Triển Khai

> **Phiên bản:** v2
> **Cập nhật:** 2026-06-09
> **Trạng thái:** Planning

---

## 1. Tổng Quan Kiến Trúc

Hệ thống theo **kiến trúc phân tầng (layered architecture)** với cột Security xuyên suốt tất cả các tầng.

```
┌─────────────────────────────────────────┬──────────┐
│ Presentation (Web / Mobile client)       │          │
├─────────────────────────────────────────┤          │
│ Gateway (API Gateway · JWT · RBAC · WAF) │ Security │
├─────────────────────────────────────────┤  (cross- │
│ Runtime                                  │  layer)  │
│   ├── Application sublayer                │          │
│   └── Agent sublayer                      │          │
├─────────────────────────────────────────┤          │
│ Data & Infrastructure                    │          │
│   Vector DB · Relational DB · Object      │          │
│   store · Observability                   │          │
└─────────────────────────────────────────┴──────────┘
```

| Tầng | Vai trò | Thành phần chính |
|------|---------|------------------|
| **Presentation** | Giao diện người dùng | Web / Mobile client (TLS · CORS · CSP) |
| **Gateway** | Xác thực & định tuyến | API Gateway (JWT · RBAC · WAF) |
| **Runtime** | Xử lý core | Applications + Agents (Encryption · isolation) |
| **Data & Infra** | Lưu trữ & quan sát | DB, Vector DB, S3, Observability |
| **Security** | Bảo vệ toàn hệ (cross-layer) | TLS, encryption, audit logs |

---

## 2. Tech Stack Đề Xuất

```yaml
Presentation:
  - Next.js (dashboard, preview)
  - React components

Gateway:
  - ASP.NET Core
  - JWT + OIDC (IdentityServer / built-in auth)
  - Rate limiting & WAF rules

Runtime:
  Application sublayer:
    - Business logic, workflows
    - Hangfire (job scheduling)
  Agent sublayer:
    - LLM integration (Claude API / Qwen3 local)
    - Tool / function calling
    - Agent orchestration (Planner–Executor–Critic)

Data & Infrastructure:
  - PostgreSQL (relational)
  - Qdrant / pgvector (embeddings)
  - Redis (cache)
  - S3 / MinIO (object store)
  - OpenTelemetry + Grafana (observability)

Infrastructure:
  - Docker + Kubernetes / Docker Compose
  - Mac mini M4 → local inference
  - RunPod → fine-tuning batch
  - Contabo VPS → Gateway + PostgreSQL
  - Synology NAS → backup
```

---

## 3. Roadmap (4–6 tháng)

### Phase 1 — Nền tảng (Tháng 1–2)
- API Gateway (ASP.NET Core): JWT auth, RBAC middleware, rate limiting, WAF.
- Database: PostgreSQL, Qdrant/pgvector, Redis.
- Infrastructure: Docker containerization, K8s/Compose, TLS/SSL.

### Phase 2 — Runtime Layer (Tháng 3–4)
- **Application sublayer:** service modules, transaction handling, state management.
- **Agent sublayer:** LLM integration, tool/function calling, agent orchestration, memory/context management.

### Phase 3 — Security & Observability (Tháng 4–5)
- **Security:** ACL cấp ứng dụng, audit logging, encryption at-rest, rate limiting per user/org.
- **Observability:** distributed tracing (OpenTelemetry/Jaeger), centralized logging, cost tracking (token/compute), metrics & alerting (Prometheus + Grafana).

### Phase 4 — Scaling & Polish (Tháng 6)
- Load testing & optimization.
- CI/CD pipeline (GitHub Actions / GitLab CI).
- Disaster recovery & backup strategy.
- Documentation & SLA.

---

## 4. Áp Dụng Cho Project OCR Thư Viện Cơ Đốc

Mapping kiến trúc AIQ vào pipeline OCR sửa lỗi tiếng Việt:

```
Presentation : Next.js dashboard (upload PDF, xem tiến độ, review corrections)
Gateway      : ASP.NET Core (JWT, kiểm soát upload file)
Runtime
  Application: Book metadata, user/role management
  Agent      : PaddleOCR → Qwen3 correction → pgvector validation
Data         : PostgreSQL (metadata, corrections history)
               Qdrant (Vietnamese text embeddings)
               S3/MinIO (PDF gốc + correction logs)
```

Agent flow mẫu:

```csharp
public class OCRCorrectionAgent
{
    private readonly ILLMService _llm;
    private readonly IOCRService _ocr;
    private readonly IVectorStore _vectorDb;

    public async Task<CorrectionResult> CorrectPDF(string pdfPath)
    {
        var text      = await _ocr.ExtractAsync(pdfPath);
        var corrected = await _llm.CorrectVietnameseAsync(
            text, new[] { "diacritic_preservation", "context_aware" });
        var validated = await _vectorDb.ValidateAsync(corrected);
        return new CorrectionResult { Text = validated };
    }
}
```

---

## 5. Risk & Mitigation

| Risk | Mitigation |
|------|-----------|
| LLM latency | Queue (Hangfire) + caching qua pgvector |
| Sai dấu tiếng Việt | Fine-tune local model, giữ reference corpus |
| Mất dữ liệu | Btrfs snapshots trên NAS, PostgreSQL WAL archiving |
| Chi phí tăng vọt | Token budgeting, ưu tiên local inference, RunPod spot |
| Vendor lock-in | Abstraction (`ILLMService`), hỗ trợ nhiều model |

---

## 6. Bước Tiếp Theo (Ngắn Hạn)

1. **Tuần này:** Setup repo + Docker Compose (PostgreSQL + Qdrant).
2. **Tuần 2:** API Gateway prototype (ASP.NET Core + JWT).
3. **Tuần 3:** Agent skeleton (Qwen3 qua Ollama/MLX).
4. **Tuần 4:** OCR pipeline test đầu tiên (PDF → extract → correct).
