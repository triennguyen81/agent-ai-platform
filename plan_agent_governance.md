# Plan — Agent Governance Framework (AIQ → Agentic AI Platform)

> Source: `YeuCau/Agent_Governance_Framework.pptx` + 2 ảnh kiến trúc.
> Mục tiêu: nâng AIQ từ **single-tenant + agent-monolith** lên **multi-tenant Agentic AI Platform** với 5 nhóm management objects + 7-layer architecture chuẩn.
> Tracking: [`progress-check-agent-governance.md`](progress-check-agent-governance.md).

---

## 0. Context & Decisions cần chốt

### 0.1 Vì sao cần làm

Yêu cầu đối tác mở rộng AIQ thành **platform dùng chung cho nhiều tổ chức/SME** — không còn là 1 app riêng cho 1 client. Điều này đòi 5 thứ cốt lõi mà AIQ chưa có:

1. **Multi-tenancy** — `tenant_id` trên mọi data
2. **Agent identity** — mỗi agent có `agent_id` riêng, không share credential
3. **Agent ACL** — Owner / Editor / User per agent
4. **Tool whitelist** — agent chỉ gọi được tools đã grant
5. **Decision trace + cost attribution** — compliance NĐ 13/2023 + chargeback

### 0.2 Decisions cần chốt trước khi start

| # | Câu hỏi | Default đề xuất | Tác động |
|---|---------|-----------------|----------|
| D1 | Scope đợt này: full 6 phases (~3 tháng) hay G1+G2 trước (~4 tuần)? | **G1+G2 trước** — get multi-tenant + identity working, các phase sau khi thấy giá trị | High |
| D2 | 1 user thuộc 1 tenant hay multi-tenant switch? | **Multi-tenant switch** — user có thể là member nhiều tenant với role khác nhau | Medium |
| D3 | "Skill" hiện tại vs "Agent" mới: gộp hay tách? | **Tách**: 1 Agent = orchestrator + N skills. Skill là capability building-block, Agent là entity quản lý | High |
| D4 | Compliance target | **NĐ 13/2023 only** (MVP), thêm GDPR/SOC2 khi có client cần | Medium |
| D5 | Backward-compat với Contract Assistant | **Gắn vào framework mới** — Contract Assistant trở thành domain agent `agent.contract-monitor` thuộc tenant default | High |
| D6 | Tenant isolation strategy | **Row-level security** (RLS) trên PostgreSQL, không tách DB | Medium |
| D7 | Action Tier default cho agents mới | **suggest** (Read-only safest, suggest practical) | Low |
| D8 | Per-agent credential storage | **OIDC service accounts** trong gateway (không tạo IAM provider riêng) | Medium |

---

## 1. Mapping yêu cầu ↔ AIQ hiện tại

### Object mapping

| Nhóm | Object | AIQ status |
|------|--------|------------|
| **Identity** | Agent Identity | ❌ Chưa có (chỉ có User identity) |
| | Agent Owner | ❌ |
| | Agent Type/Class | ⚠️ Có khái niệm skill, chưa formal |
| | Lifecycle State | ❌ |
| | Agent ACL | ❌ (chỉ có user roles toàn cục) |
| **Permission** | Role/Policy Template | ⚠️ Roles global (super_admin/admin/user), chưa per-agent |
| | Tool Registry | ❌ Tools hardcode trong Python |
| | Data Scope/Tenant | ❌ Single-tenant |
| | Action Tier | ❌ |
| | Delegation Chain | ⚠️ MCP tools chained, không log formal |
| **Runtime Gov** | Session Boundary | ✅ Chat threads có user_id |
| | Rate Limit | ✅ Per-user; ❌ per-agent/tenant |
| | HITL Gate | ❌ |
| | Kill Switch | ❌ |
| **Audit** | Action Log | ✅ `audit_log` table |
| | Decision Trace | ❌ |
| | Cost Attribution | ❌ Token usage không log per agent |
| **Body** | Review Board | ❌ |
| | Policy Owner | ⚠️ super_admin role làm tạm |
| | Incident Response Owner | ❌ |

### Layer mapping

| Layer | AIQ có | Gap |
|-------|--------|-----|
| L1 Data | PostgreSQL ✅, Redis ✅, Qdrant ✅ | S3/MinIO (large file), Kafka (event bus optional), **RLS per tenant** |
| L2 LLM | Claude (Haiku/Sonnet/Opus) ✅, fastembed ✅ | — |
| L3 Runtime | FastAPI agent + skills + MCP ✅ | **Tool registry**, **Delegation log** |
| L4 Gateway | ASP.NET + JWT + RateLimit + Hangfire ✅ | **ACL per-agent**, **HITL gate**, **Tier check** |
| L5 Client | Next.js 15 web ✅ | (Mobile/ERP/CRM sau) |
| L6 Identity | 3-tier user roles + JWT ✅ | **Agent OIDC**, **tenant_id claim**, ACL M:N |
| L7 Governance | /admin/{users,skills,audit,health} ✅ | **/admin/agents**, Lifecycle UI, Review Board approval |
| Audit | `audit_log` ✅ | Decision trace, Cost attrib, Immutable export |

---

## 2. Architecture Target

```
                                ┌────────────────────────────────┐
                                │ L7 Control Plane (NEW)         │
                                │  /admin/agents, lifecycle,     │
                                │  review board, policy editor   │
                                └─────────────┬──────────────────┘
                                              │ approves/disables
                                              ▼
┌──────────────────────┐    ┌──────────────────────────────────┐
│ L5 Client            │───▶│ L4 Gateway (.NET)                │
│ Next.js web          │    │ JWT (user+tenant claim)          │
│ principal_id in JWT  │    │ ┌──────────────────────────────┐ │
└──────────────────────┘    │ │ L6 Authz (NEW middleware)    │ │
                            │ │ - Validate agent_acl         │ │
                            │ │ - Check action_tier ≥ ask    │ │
                            │ │ - Enforce kill_switch        │ │
                            │ │ - Inject tenant_id filter    │ │
                            │ └──────────────────────────────┘ │
                            │ Rate limit (per agent+tenant)    │
                            │ HITL gate → approval queue       │
                            └─────────────┬────────────────────┘
                                          │ /agent/run + agent_id
                                          ▼
                            ┌──────────────────────────────────┐
                            │ L3 Agent Runtime (Python)        │
                            │ Orchestrator → load Agent record │
                            │ → fetch tool_registry whitelist  │
                            │ → call only allowed MCP tools    │
                            │ → record decision_trace          │
                            │ → emit cost_attribution event    │
                            └─────────────┬────────────────────┘
                                          │
                                          ▼
                            ┌──────────────────────────────────┐
                            │ L2 LLM (Claude API)              │
                            └─────────────┬────────────────────┘
                                          │
                                          ▼
                            ┌──────────────────────────────────┐
                            │ L1 Data (Postgres+RLS)           │
                            │ Every row: tenant_id NOT NULL    │
                            └──────────────────────────────────┘

       Cross-layer audit channel (Kafka or just async insert):
       action_log + decision_trace + cost_attribution
       captured at every layer transition
```

---

## 3. Phasing — 6 Phases (G1–G6)

### Phase G1 — Tenant + Agent Identity Foundation (2 tuần)

**Goal:** AIQ hỗ trợ multi-tenant + agent là 1 entity riêng có owner.

**Schema mới (`infra/postgres/init/10_governance_tenants.sql` + `11_governance_agents.sql`):**

```sql
-- Tenants
CREATE TABLE tenants (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code            TEXT UNIQUE NOT NULL,        -- 'default', 'partner-abc'
    name            TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'active',  -- active/suspended/archived
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata        JSONB DEFAULT '{}'::jsonb
);

-- Tenant membership (user can belong to N tenants with different roles)
CREATE TABLE tenant_members (
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id         UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role            TEXT NOT NULL,               -- tenant_admin/member/viewer
    joined_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, user_id)
);

-- Agents
CREATE TABLE agents (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id         UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    code              TEXT NOT NULL,             -- 'contract-monitor', 'kt-bookkeeper'
    name              TEXT NOT NULL,
    type              TEXT NOT NULL,             -- orchestrator/domain/sub/tool_only
    description       TEXT,
    lifecycle_state   TEXT NOT NULL DEFAULT 'draft',
       -- draft → testing → approved → production → deprecated → retired
    owner_user_id     UUID REFERENCES users(id) ON DELETE SET NULL,
    sponsor_team      TEXT,
    baseline_role     TEXT,                      -- ref to role template
    action_tier       TEXT NOT NULL DEFAULT 'suggest',
       -- read_only / suggest / execute_with_approval / autonomous
    config            JSONB DEFAULT '{}'::jsonb, -- skills, model, etc.
    disabled_at       TIMESTAMPTZ,               -- kill switch
    disabled_by       UUID REFERENCES users(id),
    disabled_reason   TEXT,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, code)
);

-- Agent lifecycle history (audit transitions)
CREATE TABLE agent_lifecycle_events (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    from_state      TEXT,
    to_state        TEXT NOT NULL,
    transitioned_by UUID REFERENCES users(id),
    reason          TEXT,
    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Agent ACL — M:N agent ↔ principal (user/group)
CREATE TABLE agent_acl (
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    principal_id    UUID NOT NULL,              -- user_id (later: group_id)
    principal_type  TEXT NOT NULL DEFAULT 'user',
    access_level    TEXT NOT NULL,              -- owner / editor / user
    granted_by      UUID REFERENCES users(id),
    granted_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (agent_id, principal_id, principal_type)
);

CREATE INDEX idx_agents_tenant ON agents(tenant_id, lifecycle_state);
CREATE INDEX idx_agent_acl_principal ON agent_acl(principal_id, principal_type);
```

**Backfill migration (`12_backfill_default_tenant.sql`):**
- Tạo `tenants` row `code='default', name='AIQ Default'`
- Tạo `tenant_members` cho tất cả users hiện tại với role='tenant_admin'
- Thêm column `tenant_id UUID` vào: `contracts`, `notes`, `books`, `audit_log`, `notifications` (nullable trước, backfill = default tenant, rồi NOT NULL)
- Tạo agent `code='contract-monitor'` tenant=default cho Contract Assistant hiện tại

**Gateway changes:**

- `tenants/` namespace mới: `ITenantRepository`, `TenantEndpoints` (CRUD super_admin only)
- `agents/` namespace mới: `IAgentRepository`, `AgentEndpoints` (list/create/get/update/lifecycle-transition/kill-switch)
- `JwtTokenService` — thêm `tenant_id` claim khi login (chọn active tenant; default = first membership)
- New middleware `TenantContextMiddleware` — extract tenant_id từ JWT, gắn vào `HttpContext.Items["TenantId"]`
- Update all existing repos (NoteRepo, ContractRepo, ChecklistRepo, BookRepo, NotificationRepo) — filter `WHERE tenant_id = @tenantId`

**Web changes:**

- `/admin/tenants` — super_admin: list + create + invite member
- `/admin/agents` — tenant_admin: list agents trong tenant, filter by lifecycle_state
- `/admin/agents/[id]` — detail + ACL editor + lifecycle transition button + kill switch
- Header: tenant switcher dropdown (nếu user thuộc nhiều tenants)
- All API proxies: forward `Authorization: Bearer <token>` (token đã carry tenant claim)

**Files modified/created:**
- `infra/postgres/init/{10,11,12}_governance_*.sql` (NEW)
- `gateway/src/Aiq.Gateway/Governance/{ITenantRepository,IAgentRepository,IAgentAclRepository}.cs` (NEW)
- `gateway/src/Aiq.Gateway/Endpoints/{TenantEndpoints,AgentEndpoints}.cs` (NEW)
- `gateway/src/Aiq.Gateway/Auth/JwtTokenService.cs` (modify — thêm tenant claim)
- `gateway/src/Aiq.Gateway/Middleware/TenantContextMiddleware.cs` (NEW)
- All `IRepository` impls (modify — accept tenantId, filter)
- `web/src/app/(app)/admin/tenants/` + `agents/` (NEW pages)
- `web/src/components/tenant-switcher.tsx` (NEW)
- `web/src/lib/api.ts` (add tenant/agent helpers)

**Acceptance:**
- super_admin tạo được tenant mới, mời được user
- tenant_admin tạo agent mới (lifecycle=draft), grant ACL
- Existing Contract Assistant vẫn chạy bình thường nhưng giờ thuộc agent `contract-monitor` của tenant `default`
- User chỉ thấy contracts của tenant mình đang active

---

### Phase G2 — Tool Registry + Action Tier Enforcement (1-2 tuần)

**Goal:** Tools không còn hardcode — agents có whitelist; mỗi tool có risk tier.

**Schema (`13_governance_tools.sql`):**

```sql
CREATE TABLE tools (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code            TEXT UNIQUE NOT NULL,        -- 'mcp__aiq__save_contract'
    name            TEXT NOT NULL,
    description     TEXT,
    category        TEXT,                        -- read/write/external
    risk_tier       TEXT NOT NULL,               -- low/medium/high/critical
    min_action_tier TEXT NOT NULL DEFAULT 'suggest',
       -- minimum agent action_tier needed to use this tool
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    metadata        JSONB DEFAULT '{}'::jsonb
);

CREATE TABLE agent_tool_grants (
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    tool_id         UUID NOT NULL REFERENCES tools(id) ON DELETE CASCADE,
    granted_by      UUID REFERENCES users(id),
    granted_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (agent_id, tool_id)
);

-- Delegation chain log (parent → child agent calls)
CREATE TABLE agent_delegations (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    parent_agent_id UUID NOT NULL REFERENCES agents(id),
    child_agent_id  UUID NOT NULL REFERENCES agents(id),
    session_id      UUID,
    invoked_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    payload_hash    TEXT
);
```

**Agent runtime changes (Python):**
- New module `agentic/registry.py` — load agent + tool grants from gateway at runtime
- `runner.py` — `allowed_tools` không còn hardcode, query `GET /agents/{id}/tools` ra list
- Tool execution wrapper — trước khi call MCP tool, check `agent.action_tier >= tool.min_action_tier`
- Nếu agent là orchestrator gọi sub-agent → insert `agent_delegations` row

**Gateway changes:**
- `IToolRepository`, `ToolEndpoints` — list/create/update tools, grant/revoke to agents
- `GET /agents/{id}/tools` — return whitelist cho agent runtime

**Web:**
- `/admin/tools` — super_admin manages global tool registry + risk tier
- `/admin/agents/[id]/tools` — tenant_admin grant tools to agent (filtered by min_action_tier ≤ agent.action_tier)

**Seed data:** insert all existing MCP tools (`mcp__aiq__*`) with sensible risk tiers.

**Acceptance:**
- Agent không grant tool X → runtime reject 403
- Agent action_tier=`read_only` không gọi được tool có min=`execute_with_approval`
- Hangfire job log delegation chain khi orchestrator gọi sub-agent

---

### Phase G3 — Lifecycle Workflow + Review Board (2 tuần)

**Goal:** Agent không tự lên production — phải qua review board approval.

**Schema (`14_governance_review.sql`):**

```sql
CREATE TABLE review_boards (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name            TEXT NOT NULL,
    quorum          INT NOT NULL DEFAULT 1,
    UNIQUE (tenant_id, name)
);

CREATE TABLE review_board_members (
    board_id        UUID NOT NULL REFERENCES review_boards(id) ON DELETE CASCADE,
    user_id         UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role            TEXT NOT NULL DEFAULT 'reviewer',  -- reviewer/chair
    PRIMARY KEY (board_id, user_id)
);

CREATE TABLE agent_review_requests (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    board_id        UUID NOT NULL REFERENCES review_boards(id),
    requested_by    UUID NOT NULL REFERENCES users(id),
    target_state    TEXT NOT NULL,                     -- 'production' typically
    status          TEXT NOT NULL DEFAULT 'pending',  -- pending/approved/rejected
    decided_at      TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE agent_review_votes (
    request_id      UUID NOT NULL REFERENCES agent_review_requests(id) ON DELETE CASCADE,
    voter_id        UUID NOT NULL REFERENCES users(id),
    vote            TEXT NOT NULL,                     -- approve/reject
    comment         TEXT,
    voted_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (request_id, voter_id)
);
```

**Lifecycle state machine (enforced in gateway):**

```
draft ──submit──▶ testing ──submit_review──▶ pending_review
                                                  │
                                          ┌───────┴───────┐
                                       approve         reject
                                          │               │
                                          ▼               ▼
                                      production       testing
                                          │
                                     deprecate
                                          │
                                          ▼
                                     deprecated
                                          │
                                       retire
                                          │
                                          ▼
                                       retired
```

**Web:**
- `/admin/review-queue` — pending review requests cho user ở review_board
- `/admin/agents/[id]` — nút "Submit for review" thay vì "Promote to production" trực tiếp
- Vote UI với comment box

**Acceptance:**
- Owner không thể tự promote agent lên production
- Vote đủ quorum mới chuyển state
- Audit log ghi mọi vote + lifecycle transition

---

### Phase G4 — HITL Gate + Kill Switch + Per-Agent Rate Limit (1-2 tuần)

**Goal:** Action rủi ro cao cần con người duyệt; quota theo agent/tenant; kill switch tức thời.

**Schema (`15_governance_runtime.sql`):**

```sql
CREATE TABLE hitl_gates (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    name            TEXT NOT NULL,
    trigger_rule    JSONB NOT NULL,
       -- e.g. {"tool": "send_payment", "amount_gte": 50000000}
    approver_role   TEXT NOT NULL,                     -- 'owner' / 'tenant_admin'
    is_active       BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE TABLE hitl_pending_actions (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    gate_id         UUID NOT NULL REFERENCES hitl_gates(id),
    agent_id        UUID NOT NULL REFERENCES agents(id),
    triggered_by    UUID REFERENCES users(id),
    payload         JSONB NOT NULL,
    status          TEXT NOT NULL DEFAULT 'pending',  -- pending/approved/rejected/expired
    decided_by      UUID REFERENCES users(id),
    decided_at      TIMESTAMPTZ,
    expires_at      TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE agent_rate_limits (
    agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    requests_per_min INT,
    tokens_per_day  BIGINT,
    PRIMARY KEY (agent_id, tenant_id)
);
```

**Gateway:**
- Update `RateLimiter` partition: `agent:{agent_id}:tenant:{tenant_id}` instead of just `user:{sub}`
- New endpoint `POST /agents/{id}/actions/{action_id}/decide` (approve/reject pending)
- Kill switch endpoint: `POST /agents/{id}/disable` (set `disabled_at`); middleware rejects any request to disabled agent với 423 Locked

**Agent runtime:**
- Before executing tool — check matching `hitl_gates`; if matched → insert `hitl_pending_actions` and PAUSE execution (return 202 with action_id)
- Web polls / SSE notification for approver

**Web:**
- `/admin/hitl-queue` — pending actions waiting approval
- Card per action: agent name, trigger, payload preview, approve/reject buttons
- `/admin/agents/[id]/gates` — manage gates for this agent

**Acceptance:**
- Tạo gate `send_payment > 50M` → agent gọi tool đó → tạo pending action, agent đợi → approver duyệt → tool execute
- Kill switch: bấm disable → agent ngừng nhận request luôn

---

### Phase G5 — Decision Trace + Cost Attribution (1-2 tuần)

**Goal:** Mọi quyết định agent ghi lại reasoning + token cost — phục vụ compliance + chargeback.

**Schema (`16_governance_audit.sql`):**

```sql
CREATE TABLE decision_traces (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    agent_id        UUID NOT NULL REFERENCES agents(id),
    session_id      UUID,
    turn_index      INT NOT NULL,
    user_input      TEXT,
    reasoning       TEXT,                              -- summary từ extended thinking hoặc final answer
    tool_calls      JSONB,                             -- [{tool, args_hash, result_hash}]
    final_output    TEXT,
    model           TEXT NOT NULL,                     -- 'claude-opus-4-7'
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE cost_attribution (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    agent_id        UUID NOT NULL REFERENCES agents(id),
    decision_id     UUID REFERENCES decision_traces(id) ON DELETE SET NULL,
    model           TEXT NOT NULL,
    input_tokens    INT NOT NULL,
    output_tokens   INT NOT NULL,
    cache_read_tokens INT,
    cost_usd        NUMERIC(10,6),                     -- compute at insert time
    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_decision_traces_tenant_agent ON decision_traces(tenant_id, agent_id, created_at DESC);
CREATE INDEX idx_cost_attribution_tenant_time ON cost_attribution(tenant_id, occurred_at DESC);
```

**Agent runtime:**
- Wrap every Anthropic API call — capture `usage.input_tokens`, `usage.output_tokens` → insert `cost_attribution`
- After every turn — insert `decision_traces` with reasoning summary
- Use Kafka or just async PG insert (not blocking)

**Gateway:**
- `GET /admin/cost?tenant_id=&from=&to=` — aggregated cost
- `GET /admin/decisions?agent_id=&limit=` — list traces
- `GET /admin/decisions/{id}` — full reasoning + tool calls

**Web:**
- `/admin/cost-dashboard` — chart cost per tenant/agent/day, model breakdown
- `/admin/decisions` — search reasoning by keyword, drill into a trace
- Embed cost widget in `/admin/agents/[id]` detail page

**Acceptance:**
- Sau khi 1 user chạy task → thấy `decision_traces` row + `cost_attribution` row
- Cost dashboard tổng đúng với invoice Anthropic API

---

### Phase G6 — Governance Body + Compliance Export (1 tuần)

**Goal:** Hoàn thiện workflow con người + export audit cho compliance.

**Schema (`17_governance_body.sql`):**

```sql
CREATE TABLE governance_roles (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    role_type       TEXT NOT NULL,                    -- policy_owner / incident_response
    user_id         UUID NOT NULL REFERENCES users(id),
    escalation_path JSONB,                            -- [{user_id, after_minutes}]
    UNIQUE (tenant_id, role_type)
);

CREATE TABLE incident_log (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id),
    agent_id        UUID REFERENCES agents(id),
    severity        TEXT NOT NULL,                    -- low/medium/high/critical
    summary         TEXT NOT NULL,
    detected_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    resolved_at     TIMESTAMPTZ,
    assigned_to     UUID REFERENCES users(id)
);
```

**Gateway:**
- `POST /admin/incidents` — log incident, auto-assign to incident_response owner
- `GET /admin/audit-export?tenant_id=&from=&to=&format=csv|jsonl` — bulk export action_log + decision_traces + cost_attribution

**Compliance features:**
- Audit log immutable: add trigger `BEFORE UPDATE/DELETE ON audit_log` → RAISE EXCEPTION
- Daily Hangfire job: rotate old logs to cold storage (S3/MinIO sau) — keep 90 days hot
- Export format đáp ứng NĐ 13/2023 (Personal Data Protection Decree)

**Web:**
- `/admin/governance` — assign Policy Owner / Incident Response Owner per tenant
- `/admin/incidents` — list + create + assign
- Export button on `/admin/audit`

**Acceptance:**
- Try UPDATE audit_log → fail
- Export 1 tháng audit ra file CSV — đầy đủ action+decision+cost
- Tạo incident "agent X gửi sai data" → notification tự đến incident_response owner

---

## 4. Cross-cutting concerns

### 4.1 Backward compatibility

- Contract Assistant existing: gắn vào agent `code='contract-monitor'`, tenant `code='default'`
- Existing users: tạo `tenant_members(tenant='default', role='tenant_admin')` cho tất cả
- JWT migration: hỗ trợ token cũ (không có tenant_id claim) trong 30 ngày — middleware default về tenant `default`

### 4.2 Performance

- RLS có thể slow — index mọi bảng theo `(tenant_id, ...)` composite
- Decision trace + cost insert async (Kafka hoặc Hangfire enqueue) — không block agent response
- Cost dashboard: pre-aggregate hourly vào `cost_attribution_hourly` materialized view

### 4.3 Security

- `tenant_id` claim trong JWT phải signed + immutable
- Tenant switch → re-issue token (không tự sửa client-side)
- Agent OIDC service accounts: rotate secret 90 days (Hangfire job)
- HITL approval pages cần CSRF token (state-changing)

### 4.4 Testing

- Unit: each new repository + endpoint
- Integration: cross-tenant data leakage test (user tenant A query → must not see tenant B data)
- E2E (Playwright): full agent lifecycle flow — create → submit review → approve → use → kill switch

---

## 5. Effort summary

| Phase | Schema | Backend | Frontend | Test+Deploy | Total |
|-------|--------|---------|----------|-------------|-------|
| G1    | 3 days | 5 days  | 3 days   | 2 days      | **2 tuần** |
| G2    | 1 day  | 4 days  | 2 days   | 2 days      | **1.5 tuần** |
| G3    | 2 days | 5 days  | 4 days   | 2 days      | **2 tuần** |
| G4    | 2 days | 5 days  | 4 days   | 2 days      | **2 tuần** |
| G5    | 1 day  | 4 days  | 4 days   | 2 days      | **1.5 tuần** |
| G6    | 1 day  | 3 days  | 2 days   | 1 day       | **1 tuần** |
| **Total** | | | | | **~10 tuần (~2.5 tháng)** |

Pilot 1 tuần thực tế với 1 đối tác sau G3 (đã đủ identity + tool + lifecycle).

---

## 6. Files affected snapshot

```
infra/postgres/init/
  10_governance_tenants.sql              [G1]
  11_governance_agents.sql               [G1]
  12_backfill_default_tenant.sql         [G1]
  13_governance_tools.sql                [G2]
  14_governance_review.sql               [G3]
  15_governance_runtime.sql              [G4]
  16_governance_audit.sql                [G5]
  17_governance_body.sql                 [G6]

gateway/src/Aiq.Gateway/
  Governance/
    ITenantRepository.cs                 [G1]
    IAgentRepository.cs                  [G1]
    IAgentAclRepository.cs               [G1]
    IToolRepository.cs                   [G2]
    IReviewBoardRepository.cs            [G3]
    IHitlRepository.cs                   [G4]
    IDecisionTraceRepository.cs          [G5]
    ICostAttributionRepository.cs        [G5]
    IIncidentRepository.cs               [G6]
  Endpoints/
    TenantEndpoints.cs                   [G1]
    AgentEndpoints.cs                    [G1]
    ToolEndpoints.cs                     [G2]
    ReviewEndpoints.cs                   [G3]
    HitlEndpoints.cs                     [G4]
    DecisionEndpoints.cs                 [G5]
    CostEndpoints.cs                     [G5]
    IncidentEndpoints.cs                 [G6]
  Middleware/
    TenantContextMiddleware.cs           [G1]
    AgentAclMiddleware.cs                [G1]
  Auth/
    JwtTokenService.cs                   [modify G1]

agent/src/aiq_agent/
  agentic/
    registry.py                          [G2 — load tools from gateway]
    runner.py                            [modify G2,G4,G5]
    cost_tracker.py                      [G5]
    hitl_client.py                       [G4]

web/src/app/(app)/
  admin/
    tenants/page.tsx                     [G1]
    agents/{page,[id]/page,[id]/tools,[id]/gates}/page.tsx  [G1,G2,G4]
    tools/page.tsx                       [G2]
    review-queue/page.tsx                [G3]
    hitl-queue/page.tsx                  [G4]
    cost-dashboard/page.tsx              [G5]
    decisions/page.tsx                   [G5]
    incidents/page.tsx                   [G6]
    governance/page.tsx                  [G6]
web/src/components/
  tenant-switcher.tsx                    [G1]
  agent-lifecycle-button.tsx             [G3]
  hitl-approval-card.tsx                 [G4]
  cost-chart.tsx                         [G5]
```

---

## 7. Open questions (cần user trả lời trước khi bắt đầu G1)

1. **D1** — Phase scope: full 6 phases hay G1+G2 trước?
2. **D2** — Multi-tenant switch (user thuộc nhiều tenant) hay 1-1?
3. **D3** — "Skill" cũ vs "Agent" mới: gộp hay tách như đề xuất?
4. **D5** — Contract Assistant nên backward-compat hay refactor luôn vào framework mới?
5. **D6** — RLS (row-level security) hay app-level filter? (RLS an toàn hơn, perf tương đương khi index đúng)
6. **D8** — Service account credentials lưu ở đâu? (Gateway secrets table với encryption-at-rest?)

Sau khi chốt 6 câu trên → tạo `progress-check-agent-governance.md` và bắt đầu G1.

---

## 8. References

- Source: `YeuCau/Agent_Governance_Framework.pptx`
- Architecture diagram: `YeuCau/agent_platform_layered_architecture.png`
- Object model image: `YeuCau/architech_new.jpg`
- Existing plan: [`plan.md`](plan.md), [`progress-check-contracts.md`](progress-check-contracts.md)
- Compliance: Nghị định 13/2023/NĐ-CP (Personal Data Protection)
