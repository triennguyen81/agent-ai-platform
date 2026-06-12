# Implementation Progress Status — AIQ Agentic AI

> Snapshot trạng thái implement đến ngày 2026-06-12.
> Mỗi feature ghi rõ: ✅ live · ⏳ planned · 🔜 backlog · ❌ blocked.

---

## Feature checklist

| # | Tính năng | Trạng thái | Commit / PR | Ghi chú |
|---|---|---|---|---|
| 1 | Docker Compose full stack (postgres + pgvector, redis, qdrant, loki, grafana, prometheus, promtail, web, gateway, agent) | ✅ live | initial | 10 services, `agentic-net` external network |
| 2 | Gateway JWT auth — register, login, /me, refresh logic | ✅ live | initial | Dapper + Npgsql, JWT 7d TTL |
| 3 | Anthropic Claude Haiku 4.5 LLM wiring | ✅ live | initial | `claude-haiku-4-5-20251001` |
| 4 | PEC orchestrator (Planner-Executor-Critic) | ✅ live | initial | Used when tool requested; bypassed for plain chat |
| 5 | Notes domain — CRUD, tags (TEXT[]), per-user isolation | ✅ live | initial | `StringArrayTypeHandler` for Dapper ↔ Postgres |
| 6 | Cosmos VPS deploy (NPM + Cloudflare proxied DNS) | ✅ live | initial | `ports: []` override on cosmos |
| 7 | Domain `cattuong.ai` + 3 subdomains live | ✅ live | — | app, api, agent · CF orange-clouded |
| 8 | Clerk invite-only OAuth + JWT shared-secret bridge | ✅ live | — | `/auth/external-link` accepts `additionalRoles` |
| 9 | Dental knowledge seed (14 sample notes) | ✅ live | seed-dental-notes.sql | $body$…$body$ dollar-quoting |
| 10 | RAG v1 — inject user notes into chat as context | ✅ live | bcc9a37 | Cap 60K chars per turn |
| 11 | Agent prompt limit 8K → 200K + nicer error UI | ✅ live | 50e0a6d | RAG was hitting 8K cap |
| 12 | Markdown rendering + `<thinking>` block + code blocks | ✅ live | 1712445 | react-markdown + remark-gfm + Copy button |
| 13 | Bulk `.md` upload (drag multiple files) | ✅ live | ecacb7b | YAML frontmatter aware, per-file retry |
| 14 | Save-from-chat (📑 button on assistant bubbles) | ✅ live | ecacb7b | Modal pre-fills `**Câu hỏi:** Q\n\nA` |
| 15 | Admin role management (cấp / gỡ admin) — single-tier | ✅ live | ecacb7b | Replaced by #19 |
| 16 | Chat history sidebar + auto-restore on page load | ✅ live | 3fc6ce6 | SCAN MATCH on Redis |
| 17 | Per-user thread namespacing | ✅ live | 3fc6ce6 | `<userId>:<threadId>` prefix |
| 18 | Thread title fix (skip KB augmentation in stored prompt) | ✅ live | this PR | Separate `knowledge_context` side-channel field |
| 19 | Roles: `super_admin` / `admin` / `user` (3 tiers) | ✅ live | this PR | super_admin manages roles · admin contributes KB · user consumes |
| 20 | Qdrant top-K embeddings (replace full-context RAG) | 🔜 backlog | — | M6 milestone |
| 21 | URL crawler ingestion (paste list of URLs → notes) | 🔜 backlog | — | M7 milestone |
| 22 | CSV import for product catalogs / FAQs | 🔜 backlog | — | M7 |
| 23 | Notion / Confluence sync | 🔜 backlog | — | M7 |
| 24 | Per-user analytics dashboard (top questions, KB gaps) | 🔜 backlog | — | M10 |
| 25 | Mobile responsive + PWA | 🔜 backlog | — | M11 |
| 26 | Multi-tenant workspace (org boundary) | 🔜 backlog | — | M8 |
| 27 | CI/CD via GitHub Actions (build + deploy on tag) | 🔜 backlog | — | currently manual SSH |

---

## Right-now priorities (3 tuần tới)

1. **Đóng #18 + #19** (this PR) — title bug + role refactor → roles operations sạch sẽ
2. **M6 — Qdrant top-K** — current RAG dumps every note, không scale qua 200 notes
3. **M7 — URL crawler ingestion** — biggest "transfer info fast" win sau bulk MD

---

## Known issues / tech debt

- **Old threads keep ugly titles** — Redis data, không migrate ngược. Sẽ phai dần khi user mở thread mới
- **Legacy roles** `reviewer / uploader / viewer` vẫn còn trong DB sau #19 — không gây hại, không xoá để tránh foreign-key vỡ
- **Web build dùng `--legacy-peer-deps`** — React 19 ecosystem chưa update peer deps. Bỏ được khi packages catch up
- **Manual deploy via SSH** — không có rollback tự động. M-future: GitHub Actions
- **PEC orchestrator chưa dùng cho chat** — chỉ kích hoạt khi có `tool` field. Future: dùng PEC cho complex multi-step queries

---

## Definition of Done

Mỗi feature trong bảng trên được coi là ✅ live khi đạt tất cả:

- [x] Code merged to `master`
- [x] Build pass trên cosmos
- [x] Container restart + smoke-test 200 OK
- [x] Visible/usable trong UI prod (nếu là user-facing)
- [x] Docs updated (file này + roadmap.md)
