# Architecture Structure — AIQ Agentic AI

> Snapshot kiến trúc hệ thống đang chạy tại `cattuong.ai` (production trên Contabo Cosmos VPS).
> Cập nhật lần cuối: 2026-06-18 (Phase 1-3 Strategy B Claude Agent SDK + Skills).

---

## 1. Service topology

```mermaid
flowchart TD
    user[User browser]
    cf[Cloudflare Proxied]
    npm[Nginx Proxy Manager :443]

    user --> cf --> npm

    subgraph cosmos[Contabo Cosmos VPS · 161.97.78.34]
        npm -->|app.cattuong.ai| web[aiq-web · Next.js 15]
        npm -->|api.cattuong.ai| gateway[aiq-gateway · ASP.NET Core 9]
        npm -->|agent.cattuong.ai| agent[aiq-agent · FastAPI / Python 3.12]

        web -->|HTTP| gateway
        web -->|HTTP| agent

        gateway -->|JWT mint| pg[(aiq-postgres · pgvector)]
        gateway --> redis[(aiq-redis)]
        agent --> redis
        agent --> anthropic[Anthropic API · Claude Haiku 4.5]
        agent --> skills[/app/skills/<name>/SKILL.md/]
        agent --> claudecli[claude CLI subprocess<br/>Phase 2 Agent SDK]
        claudecli --> anthropic

        pg --- qdrant[(aiq-qdrant · backlog M6)]

        prom[aiq-prometheus] --> agent
        prom --> gateway
        prom --> web
        promtail[aiq-promtail] --> loki[(aiq-loki)]
        grafana[aiq-grafana] --> loki
        grafana --> prom
    end
```

**Network:** Tất cả container join cùng external network `agentic-net` để cùng VPS chạy được nhiều stack (AIQ + agentic_ai). Container hostnames có prefix `aiq-` để tránh va chạm DNS.

---

## 2. Authentication chain

```mermaid
sequenceDiagram
    autonumber
    actor User
    participant Clerk
    participant Web as Next.js /api/auth/sync
    participant GW as Gateway /auth/external-link
    participant DB as Postgres users

    User->>Clerk: OAuth sign-in / accept invite
    Clerk-->>Web: session cookie + user payload
    Web->>GW: POST {email, displayName, provider:"clerk", externalId}<br/>Header: X-Gateway-Secret
    GW->>DB: SELECT WHERE external_provider+external_id<br/>INSERT IF NEW
    GW->>DB: Assign default role ("user")<br/>+ INITIAL_ADMIN_EMAIL → "super_admin"
    GW-->>Web: { token, expiresAt, roles[] }
    Web->>User: Set-Cookie aiq.session=<JWT>
```

- JWT signed bằng `GATEWAY_JWT_SECRET`, TTL 7 ngày
- `aiq.session` cookie: `httpOnly`, `secure`, `sameSite=lax`, `path=/`
- Shared-secret pattern: web → gateway dùng `X-Gateway-Secret` để gateway tin web đã verify user qua Clerk

---

## 3. Chat data flow

```mermaid
sequenceDiagram
    autonumber
    actor User
    participant CR as ChatRoom (React)
    participant ChatAPI as /api/chat (web)
    participant GW as Gateway /notes
    participant Agent as Agent /chat
    participant LLM as Anthropic Claude Haiku 4.5
    participant Redis

    User->>CR: Type "Implant Straumann giá bao nhiêu?"
    CR->>ChatAPI: POST { prompt, thread_id }
    ChatAPI->>GW: GET /notes (Bearer JWT)
    GW-->>ChatAPI: items[] (user's knowledge base)
    ChatAPI->>ChatAPI: Build knowledge_context (max 60K chars)
    ChatAPI->>Agent: POST { prompt, knowledge_context,<br/>thread_id: "<userId>:<thread>" }
    Agent->>Redis: LRANGE prior_history
    Agent->>LLM: messages = [system+KB, ...history, user_prompt]
    LLM-->>Agent: raw response with <thinking>…</thinking>
    Agent->>Agent: _split_thinking(raw) → (thinking, answer)
    Agent->>Redis: RPUSH user_msg + assistant_msg<br/>HSETNX meta:title
    Agent-->>ChatAPI: { final_answer, thinking, ... }
    ChatAPI-->>CR: stream JSON
    CR->>User: Render bubble · Markdown · 💭 Suy nghĩ
```

**Key behaviors:**

- **PEC bypass**: when no tool requested, agent skips Planner-Executor-Critic and calls LLM directly with the thinking-style system prompt
- **Per-user namespacing**: web prefixes `thread_id` with userId from JWT before sending to agent, so Redis keys naturally separate per user
- **RAG-as-context**: every user note prepended into the LLM call (cap 60 KB). Future M6: switch to top-K via Qdrant embeddings.

---

## 4. Redis layout

```text
aiq:mem:<userId>:<threadId>          LIST  → JSON ChatMessage entries
aiq:mem:meta:<userId>:<threadId>     HASH  → { title, updated_at }
```

- TTL: 24 h sliding (refreshed on every append)
- LTRIM caps each thread at 200 messages
- `HSETNX title` runs only on first user message of a thread → title is the first real question

`SCAN MATCH aiq:mem:<userId>:*` powers the chat-history sidebar.

---

## 5. Tech per layer

| Layer | Tech | Key choice rationale |
|---|---|---|
| Edge | Cloudflare Proxied | Free WAF + DDoS, hides origin IP |
| Reverse proxy | jc21/nginx-proxy-manager | UI for cert renewal + per-host routing |
| Frontend | Next.js 15 + React 19 + Tailwind v4 (CSS-first @theme) | App Router + standalone build for Docker |
| UI primitives | Radix + shadcn-style, lucide-react icons | Headless components, full custom styling |
| Markdown | react-markdown + remark-gfm | Tables, strikethrough, fenced code blocks |
| Auth (browser) | Clerk OAuth + email invite | Invite-only flow, magic link OOTB |
| Auth (server) | JWT issued by Gateway, shared-secret bridge | Decouple browser auth provider from API |
| Gateway | ASP.NET Core 9 + Dapper + Npgsql | Strong types, fast cold-start vs EF Core |
| Agent | FastAPI + Pydantic v2 | Async-first, OpenAPI for free |
| LLM | Anthropic Claude Haiku 4.5 (`claude-haiku-4-5-20251001`) | Cost / latency at MVP scale |
| Memory store | Redis 7 LIST per thread | TTL'd; switch to Postgres only if needed |
| KB / Notes | Postgres + pgvector ext | Vector ready for M6, currently used as plain RDBMS |
| Observability | Loki + Promtail + Grafana + Prometheus | Drop-in stack, log queries via LogQL |

---

## 6. Deploy pipeline

```mermaid
flowchart LR
    dev[Local dev] -->|git push master| gh[GitHub]
    gh -->|ssh contabo-cosmos<br/>git pull| vps[/opt/apps/aiq on VPS]
    vps -->|docker compose build<br/>--no-cache web| imgs[(local images)]
    vps -->|docker compose up -d| live[Running containers]
    live --> ingress[NPM ingress]
    ingress --> cf[Cloudflare] --> users[Users]
```

- No CI/CD yet (manual SSH); see roadmap M-future
- Cosmos override file `docker-compose.cosmos.yml` sets `ports: []` so NPM is the only ingress
- `--no-cache web` recommended when only src changes — Next standalone tracing sometimes fools Docker cache

---

## 7. Skills + Agent SDK (Strategy B, Phase 1-3)

### 7.1 Two chat paths

```mermaid
flowchart LR
    user[User chat input] --> mode{Mode toggle<br/>in chat UI}
    mode -->|Chat| chat[/api/chat\n+ /chat agent\n+ skill loader inject\nsystem prompt]
    mode -->|Agentic| run[/api/agent/run\n+ ClaudeSDKClient\n+ MCP tools]
    chat --> haiku[Claude Haiku 4.5<br/>direct Messages API]
    run --> cli[claude CLI subprocess]
    cli --> haiku
    cli --> tools[(MCP tools)]
    tools --> gw[(Gateway)]
    tools --> web[(Public URLs)]
```

- **Chat path**: optimized for latency (~0.8s). Skill loader picks active skills at startup, injects their bodies into the system prompt for that turn only. No SDK.
- **Agentic path**: spawns Claude CLI subprocess via `claude-agent-sdk`. Per-request MCP server bundles tools bound to caller's JWT.

### 7.2 Skill loader flow

```mermaid
flowchart LR
    startup[App startup] --> scan["build_registry(/app/skills)"]
    scan --> parse[Parse YAML frontmatter:<br/>name, description,<br/>keywords, always_on, when]
    parse --> reg[(In-memory SkillRegistry)]
    
    turn[Chat turn] --> select["select_skills(reg,<br/>prompt, has_kb)"]
    reg --> select
    select --> rules{Selection rules}
    rules -->|always_on| active[Active skills]
    rules -->|when has_kb + KB present| active
    rules -->|keyword match in prompt| active
    active --> inject[Concat bodies into<br/>system prompt block]
    inject --> llm[LLM call]
```

### 7.3 Agent SDK tool catalog

Per `/agent/run` request, factory creates fresh tools bound to caller's JWT:

| Tool | Backend | Purpose |
|---|---|---|
| `mcp__aiq__search_notes` | `GET /notes` via Gateway | Find notes by keyword |
| `mcp__aiq__get_note` | `GET /notes/:id` | Read 1 note in full |
| `mcp__aiq__save_note` | `POST /notes` | Save new note to KB |
| `mcp__aiq__fetch_url` | HTTPX direct | Fetch & strip HTML (256 KB cap) |

### 7.4 Skill discovery for SDK

The `claude-agent-sdk` looks for skills in:
- `~/.claude/skills/` (user source)
- `<cwd>/.claude/skills/` (project source)

Container Dockerfile symlinks both to `/app/skills/` so the same SKILL.md files serve both the lightweight loader (chat path) and the SDK auto-discovery (agentic path).

### 7.5 Per-user thread namespacing

Threads in Redis are keyed `aiq:mem:<userId>:<threadId>` so:
- Listing threads via `SCAN MATCH aiq:mem:<userId>:*` returns only that user's chats
- Agent SDK's session id is also namespaced the same way (sub-folder under MCP)

### 7.6 Voice input

`useSpeechRecognition` hook wraps Web Speech API (`vi-VN`). `MicButton` renders inline; for Firefox where the API is missing, the button hides entirely. Interim transcript is bubbled up to parent (chat input or knowledge textarea) so the floating tooltip is suppressed in favor of a full-width banner above the field.

```mermaid
flowchart LR
    mic[User clicks 🎤] --> recog[SpeechRecognition<br/>lang=vi-VN]
    recog --> interim[Interim transcript]
    interim --> banner[Banner above input]
    recog --> final[Final transcript]
    final --> appendInput[Append to input value]
```

Upgrade path documented in `docs/stt_voice_input_decision.md`: switch the hook to call `/api/stt` (Whisper API or PhoWhisper) without UI changes.
