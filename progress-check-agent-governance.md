# Progress Check — Agent Governance Framework

> Tracking checklist for [`plan_agent_governance.md`](plan_agent_governance.md).
> `[x]` xong · `[~]` đang làm · `[ ]` chưa làm.
> Cập nhật cuối: 2026-06-27 (plan drafted, awaiting decisions).

---

## Status

| Phase | Status | Commit | Notes |
|-------|--------|--------|-------|
| Decisions D1–D8 | ⏳ Waiting user | — | Cần chốt trước khi G1 |
| G1 Tenant + Agent Identity | ⏳ Pending | — | 2 tuần |
| G2 Tool Registry + Action Tier | ⏳ Pending | — | 1.5 tuần |
| G3 Lifecycle + Review Board | ⏳ Pending | — | 2 tuần |
| G4 HITL + Kill Switch + Rate | ⏳ Pending | — | 2 tuần |
| G5 Decision Trace + Cost | ⏳ Pending | — | 1.5 tuần |
| G6 Governance Body + Export | ⏳ Pending | — | 1 tuần |

**Pilot:** 1 tuần thực tế sau G3.

---

## 0. Decisions (D1–D8) — cần user chốt

- [ ] **D1** Phase scope: full 6 phases hay G1+G2 trước? _(default: G1+G2)_
- [ ] **D2** Multi-tenant: 1 user → N tenants hay 1-1? _(default: N-N)_
- [ ] **D3** Skill cũ vs Agent mới: gộp hay tách? _(default: tách, Agent = orchestrator + N skills)_
- [ ] **D4** Compliance target: NĐ 13 only hay thêm GDPR/SOC2? _(default: NĐ 13 only MVP)_
- [ ] **D5** Contract Assistant: backward-compat hay refactor? _(default: backward-compat, gắn vào framework mới)_
- [ ] **D6** Data isolation: RLS hay app-level filter? _(default: RLS)_
- [ ] **D7** Action Tier default cho agents mới _(default: suggest)_
- [ ] **D8** Per-agent credential storage strategy _(default: OIDC service account trong gateway)_

---

## Phase G1 — Tenant + Agent Identity Foundation (2 tuần)

### Schema & Migration

- [ ] `10_governance_tenants.sql` — `tenants`, `tenant_members`
- [ ] `11_governance_agents.sql` — `agents`, `agent_lifecycle_events`, `agent_acl`
- [ ] `12_backfill_default_tenant.sql` — seed default tenant + migrate existing users/data
- [ ] Add `tenant_id` column to: contracts, notes, books, audit_log, notifications, contract_clauses, contract_checklist_items
- [ ] Backfill all existing rows to default tenant
- [ ] Set tenant_id NOT NULL after backfill

### Gateway (.NET)

- [ ] `Governance/ITenantRepository.cs` + impl
- [ ] `Governance/IAgentRepository.cs` + impl (lifecycle transitions)
- [ ] `Governance/IAgentAclRepository.cs` + impl (M:N grant/revoke)
- [ ] `Endpoints/TenantEndpoints.cs` — CRUD (super_admin only)
- [ ] `Endpoints/AgentEndpoints.cs`:
  - [ ] `GET /agents` — list per tenant
  - [ ] `POST /agents` — create (default state=draft)
  - [ ] `GET /agents/{id}` — detail with ACL
  - [ ] `PATCH /agents/{id}` — update config/action_tier
  - [ ] `POST /agents/{id}/transitions` — lifecycle state change
  - [ ] `POST /agents/{id}/disable` — kill switch
  - [ ] `POST /agents/{id}/acl` — grant principal
  - [ ] `DELETE /agents/{id}/acl/{principalId}` — revoke
- [ ] `Auth/JwtTokenService.cs` — thêm `tenant_id` claim
- [ ] `Middleware/TenantContextMiddleware.cs` — extract tenant from JWT
- [ ] `Middleware/AgentAclMiddleware.cs` — check agent_acl khi action target agent
- [ ] Update existing repos (Note, Contract, Checklist, Book, Notification) — filter by tenantId
- [ ] DI registration in `Program.cs`
- [ ] `dotnet build` pass

### Web (Next.js)

- [ ] `/admin/tenants` — list + create + invite member (super_admin)
- [ ] `/admin/agents` — list agents trong active tenant
- [ ] `/admin/agents/[id]` — detail + ACL editor + lifecycle button + kill switch
- [ ] `/admin/agents/create` — new agent form
- [ ] `components/tenant-switcher.tsx` — header dropdown khi user thuộc nhiều tenants
- [ ] `components/agent-lifecycle-button.tsx`
- [ ] `lib/api.ts` — add tenant/agent helpers
- [ ] api proxies dưới `/api/tenants/*` và `/api/agents/*`

### Deploy + Verify

- [ ] Commit + push
- [ ] Cosmos: pull + run migrations + rebuild gateway/web
- [ ] Verify: existing Contract Assistant data có `tenant_id='default'`
- [ ] Verify: user existing có `tenant_members` row
- [ ] Create test tenant + agent + ACL → verify isolation

---

## Phase G2 — Tool Registry + Action Tier (1.5 tuần)

### Schema

- [ ] `13_governance_tools.sql` — `tools`, `agent_tool_grants`, `agent_delegations`
- [ ] Seed all existing MCP tools (`mcp__aiq__*`) with risk_tier

### Gateway

- [ ] `Governance/IToolRepository.cs` + impl
- [ ] `Endpoints/ToolEndpoints.cs`:
  - [ ] `GET /tools` — list global registry
  - [ ] `POST /tools` — create (super_admin)
  - [ ] `GET /agents/{id}/tools` — whitelist for runtime
  - [ ] `POST /agents/{id}/tools` — grant
  - [ ] `DELETE /agents/{id}/tools/{toolId}` — revoke

### Agent runtime (Python)

- [ ] `agentic/registry.py` — fetch agent + tool grants from gateway
- [ ] `runner.py` — `allowed_tools` query from registry instead of hardcode
- [ ] Tool wrapper — check `agent.action_tier >= tool.min_action_tier`
- [ ] Log `agent_delegations` when orchestrator → sub-agent

### Web

- [ ] `/admin/tools` — global registry (super_admin)
- [ ] `/admin/agents/[id]/tools` — grant tools to agent
- [ ] Filter UI: hide tools where min_action_tier > agent.action_tier

### Verify

- [ ] Agent without grant → tool call returns 403
- [ ] read_only agent → cannot call execute_with_approval tool
- [ ] Orchestrator → sub-agent invocation logged

---

## Phase G3 — Lifecycle + Review Board (2 tuần)

### Schema

- [ ] `14_governance_review.sql` — `review_boards`, `review_board_members`, `agent_review_requests`, `agent_review_votes`

### Gateway

- [ ] `Governance/IReviewBoardRepository.cs` + impl
- [ ] `Endpoints/ReviewEndpoints.cs`:
  - [ ] `POST /review-boards` — create board
  - [ ] `POST /agents/{id}/review-requests` — submit for review
  - [ ] `POST /review-requests/{id}/votes` — cast vote
  - [ ] `GET /review-queue` — pending for current user
- [ ] Lifecycle state machine enforcement (Owner CANNOT skip to production)

### Web

- [ ] `/admin/review-boards` — manage boards + members
- [ ] `/admin/review-queue` — pending reviews
- [ ] `/admin/agents/[id]` — "Submit for review" button replaces direct promote
- [ ] Vote card with comment

### Verify

- [ ] Owner cannot promote draft → production directly
- [ ] Quorum enforced before transition
- [ ] All votes + transitions in audit log

---

## Phase G4 — HITL + Kill Switch + Per-Agent Rate Limit (2 tuần)

### Schema

- [ ] `15_governance_runtime.sql` — `hitl_gates`, `hitl_pending_actions`, `agent_rate_limits`

### Gateway

- [ ] `Governance/IHitlRepository.cs` + impl
- [ ] `Endpoints/HitlEndpoints.cs`:
  - [ ] `POST /agents/{id}/gates` — create gate
  - [ ] `GET /hitl-queue` — pending actions
  - [ ] `POST /hitl/{id}/decide` — approve/reject
- [ ] Rate limiter partition: `agent:{id}:tenant:{id}`
- [ ] Kill switch middleware: reject 423 Locked when agent.disabled_at NOT NULL

### Agent runtime

- [ ] `hitl_client.py` — check gates before tool exec; insert pending; pause
- [ ] Resume mechanism after approval (polling or webhook)

### Web

- [ ] `/admin/hitl-queue` — pending action cards
- [ ] `/admin/agents/[id]/gates` — manage gates
- [ ] `/admin/agents/[id]/rate-limits` — per-agent quota

### Verify

- [ ] Gate `send_payment > 50M` triggers approval queue
- [ ] Kill switch immediately stops agent
- [ ] Tenant A burning quota doesn't affect tenant B

---

## Phase G5 — Decision Trace + Cost Attribution (1.5 tuần)

### Schema

- [ ] `16_governance_audit.sql` — `decision_traces`, `cost_attribution`
- [ ] Composite indexes for tenant+time queries

### Agent runtime

- [ ] `cost_tracker.py` — wrap Anthropic API calls, capture usage
- [ ] Insert `decision_traces` after each turn (reasoning summary)
- [ ] Async insert (Hangfire enqueue or background task)

### Gateway

- [ ] `Governance/IDecisionTraceRepository.cs` + impl
- [ ] `Governance/ICostAttributionRepository.cs` + impl
- [ ] `Endpoints/DecisionEndpoints.cs` — list + detail
- [ ] `Endpoints/CostEndpoints.cs` — aggregated by tenant/agent/day
- [ ] Pre-aggregate materialized view `cost_attribution_hourly`

### Web

- [ ] `/admin/cost-dashboard` — chart per tenant/agent/model
- [ ] `/admin/decisions` — searchable traces
- [ ] Cost widget on `/admin/agents/[id]`

### Verify

- [ ] User runs task → row in decision_traces + cost_attribution
- [ ] Dashboard sum matches Anthropic invoice ±5%

---

## Phase G6 — Governance Body + Compliance Export (1 tuần)

### Schema

- [ ] `17_governance_body.sql` — `governance_roles`, `incident_log`
- [ ] Trigger: audit_log immutable (BEFORE UPDATE/DELETE → RAISE)

### Gateway

- [ ] `Governance/IIncidentRepository.cs` + impl
- [ ] `Endpoints/IncidentEndpoints.cs` — log + auto-assign
- [ ] `GET /admin/audit-export?tenant_id=&from=&to=&format=csv|jsonl`
- [ ] Hangfire job: rotate old logs (>90 days) to cold storage

### Web

- [ ] `/admin/governance` — assign Policy Owner / Incident Owner
- [ ] `/admin/incidents` — list + create + assign
- [ ] Export button on `/admin/audit`

### Verify

- [ ] UPDATE audit_log raises
- [ ] Export 1 month → CSV with action+decision+cost
- [ ] Create incident → notification to incident owner

---

## Pilot (sau G3)

### Setup

- [ ] Tạo tenant pilot cho 1 đối tác
- [ ] Tenant admin invite 3-5 users
- [ ] Tạo 2-3 agents thực tế (ví dụ: contract-monitor, kt-bookkeeper)
- [ ] Submit for review → approve → production

### Đánh giá

- [ ] Tenant isolation verified (cross-tenant query test)
- [ ] Agent ACL enforcement (user không có grant → 403)
- [ ] Tool whitelist works
- [ ] Review board flow smooth
- [ ] UX có cần polish (bulk grant, agent templates)?

---

## Documentation (parallel)

- [ ] Update `docs/architecture_structure.md` — add 7-layer diagram + governance overview
- [ ] Update `docs/roadmap.md` — add M-Governance milestones
- [ ] Tạo `docs/governance_admin_guide.md` — hướng dẫn tenant admin
- [ ] Tạo `docs/governance_developer_guide.md` — hướng dẫn thêm agent mới
- [ ] Update `docs/implement_progress_status.md` — add governance rows

---

## Risks + Mitigation

| Risk | Mitigation |
|------|------------|
| Big refactor breaks Contract Assistant | Phase G1 keep backward-compat, default tenant + agent backfill |
| RLS perf issues | Composite indexes (tenant_id, ...); benchmark before/after |
| Tool registry adoption slow | Pre-seed all existing MCP tools; clear migration guide |
| Review board bottleneck | Allow auto-approve for low-risk agents (action_tier=read_only) |
| Cost dashboard wrong | Compare with Anthropic monthly invoice; alert if diff >10% |
| HITL gate fatigue | Gate config conservative defaults; allow per-user mute |
| User lock out (forgot owner) | Super_admin escalation path; recovery via support ticket |

---

## Reference

- Full plan: [`plan_agent_governance.md`](plan_agent_governance.md)
- Source PPT: `YeuCau/Agent_Governance_Framework.pptx`
- Architecture: `YeuCau/agent_platform_layered_architecture.png`
- Object model: `YeuCau/architech_new.jpg`
- Prior plan: [`plan.md`](plan.md), [`progress-check-contracts.md`](progress-check-contracts.md)
