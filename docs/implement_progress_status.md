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
| 20 | Voice input (Speech-to-Text tiếng Việt) — Web Speech API | ✅ live | 504e447 | Option A; mic banner UX fixed b055ff9 |
| 21 | Serif font Vietnamese (Lora) — fix tone-mark decomposition | ✅ live | 3a9e97b | "Bă´t" → "Bắt" |
| 22 | Skill system Phase 1 — SKILL.md loader + 4 initial skills | ✅ live | c8a27bd | dental-expert, vietnamese-style, rag-citation, knowledge-curator |
| 23 | Skill system Phase 2 — Claude Agent SDK + /agent/run + 4 MCP tools | ✅ live | c8b263d | search_notes, get_note, save_note, fetch_url + url-ingest skill |
| 24 | Skill system Phase 3 — Agentic UI mode toggle + URL detection + trace | ✅ live | 0f81b97 | Mode pill in chat header, URL chip → switch agentic, collapsible trace |
| 25 | Skill system Phase 4 — authoring guide + architecture docs refresh | ✅ live | this PR | docs/skills_authoring.md, updated architecture, regenerated .docx |
| 26 | Qdrant top-K embeddings (replace full-context RAG) | 🔜 backlog | — | M6 milestone |
| 27 | URL crawler at-scale ingestion (multiple URLs in batch) | 🔜 backlog | — | Single URL ingest already works via /agent/run + url-ingest skill |
| 28 | CSV import for product catalogs / FAQs | 🔜 backlog | — | M7 |
| 29 | Notion / Confluence sync | 🔜 backlog | — | M7 |
| 30 | Per-user analytics dashboard (top questions, KB gaps) | 🔜 backlog | — | M10 |
| 31 | Mobile responsive + PWA | 🔜 backlog | — | M11 |
| 32 | Multi-tenant workspace (org boundary) | 🔜 backlog | — | M8 |
| 33 | CI/CD via GitHub Actions (build + deploy on tag) | 🔜 backlog | — | currently manual SSH |
| 34 | Accounting persona (Mức 1: skills + KB) | ⏳ planned | next | See docs/Ketoan_plan.md |

---

## Right-now priorities (3 tuần tới)

1. **Accounting persona Mức 1** — 4 SKILL.md kế toán + 8 note KB mẫu (theo docs/Ketoan_plan.md)
2. **M6 — Qdrant top-K** — current RAG dumps every note, không scale qua 200 notes
3. **Accounting Mức 2** — domain tools: calculate_vat / calculate_pit / lookup_account

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
