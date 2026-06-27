# Progress Check — Agent Governance Framework

> Tracking checklist for [`plan_agent_governance.md`](plan_agent_governance.md).
> `[x]` xong · `[~]` đang làm · `[ ]` chưa làm.
> Cập nhật cuối: 2026-06-27 (plan drafted, awaiting decisions).

---

## Status

| Phase | Status | Commit | Notes |
|-------|--------|--------|-------|
| Decisions D1–D8 | ✅ Defaults chosen | — | User chọn defaults, ghi rõ ở bên dưới |
| **G1 Backend** Tenant + Agent Identity | 🟡 Backend done, web pending | _pending commit_ | Schemas + repos + endpoints + JWT + middleware + existing repo updates |
| G1 Web | ⏳ Pending | — | admin/tenants, admin/agents, tenant-switcher |
| G1 Deploy | ⏳ Pending | — | Run 10/11/12 migrations on cosmos |
| G2 Tool Registry + Action Tier | ⏳ Pending | — | 1.5 tuần |
| G3 Lifecycle + Review Board | ⏳ Pending | — | 2 tuần |
| G4 HITL + Kill Switch + Rate | ⏳ Pending | — | (out of scope đợt 1-2-3) |
| G5 Decision Trace + Cost | ⏳ Pending | — | (out of scope đợt 1-2-3) |
| G6 Governance Body + Export | ⏳ Pending | — | (out of scope đợt 1-2-3) |

**Pilot:** 1 tuần thực tế sau G3.

---

## 0. Decisions (D1–D8) — chốt 2026-06-27 (user chọn defaults)

- [x] **D1** Scope: **G1+G2+G3** (3 phases ~5.5 tuần)
- [x] **D2** Multi-tenant: **1 user → N tenants** (membership table tenant_members)
- [x] **D3** Skill vs Agent: **tách** — Agent = entity quản lý, skills là capabilities
- [x] **D4** Compliance: **NĐ 13 only MVP** (GDPR/SOC2 sau)
- [x] **D5** Contract Assistant: **backward-compat** — backfill thành agent `contract-monitor` tenant `default`
- [x] **D6** Data isolation: **app-level filter** (RLS có thể bật sau khi pilot ổn)
- [x] **D7** Action Tier default: **suggest** (Contract Assistant nâng lên `execute_with_approval`)
- [x] **D8** Per-agent credentials: **OIDC service account** trong gateway (deferred — chưa cần ở G1)

---

## Phase G1 — Tenant + Agent Identity Foundation (2 tuần)

### Schema & Migration

- [x] `10_governance_tenants.sql` — `tenants`, `tenant_members`
- [x] `11_governance_agents.sql` — `agents`, `agent_lifecycle_events`, `agent_acl` + trigger auto-log transitions
- [x] `12_backfill_default_tenant.sql` — seed default tenant + migrate existing users/data
- [x] Add `tenant_id` column to: contracts, notes, books, audit_log, notifications, contract_clauses, contract_checklist_items
- [x] Backfill all existing rows to default tenant
- [x] Set tenant_id NOT NULL after backfill (audit_log nullable)
- [ ] **Run on cosmos** (pending deploy)

### Gateway (.NET)

- [x] `Governance/ITenantRepository.cs` + impl (list/create/find/memberships)
- [x] `Governance/IAgentRepository.cs` + impl (CRUD + lifecycle state machine + ACL)
- [x] `Endpoints/TenantEndpoints.cs` — CRUD super_admin + /me/tenants/switch
- [x] `Endpoints/AgentEndpoints.cs`:
  - [x] `GET /agents` — list per tenant
  - [x] `POST /agents` — create (default state=draft, auto-grant owner ACL)
  - [x] `GET /agents/{id}` — detail with ACL
  - [x] `PATCH /agents/{id}` — update config/action_tier
  - [x] `POST /agents/{id}/transitions` — lifecycle state change (state machine enforced)
  - [x] `POST /agents/{id}/disable` + `/enable` — kill switch
  - [x] `GET /agents/{id}/lifecycle` — transition history
  - [x] `GET /agents/{id}/acl` + `POST` + `DELETE` — ACL management
- [x] `Auth/JwtTokenService.cs` — thêm `tenant_id` claim (optional)
- [x] `Middleware/TenantContext.cs` — TenantContextMiddleware extract tenant from JWT (fallback to first membership for legacy tokens)
- [x] Update Auth endpoints — login/register/external-link auto-issue tenant claim
- [x] Update `IContractRepository` + `ContractEndpoints` — tenant-filtered everywhere
- [x] Update `IChecklistRepository` + `ChecklistEndpoints` — tenant-filtered via contract JOIN
- [x] Update `INotificationRepository` + `ContractDeadlineJob` — pass tenant_id on insert
- [ ] Notes / Books repos — deferred (not used by Contract Assistant; can update later)
- [x] DI registration in `Program.cs` (ITenantRepository, IAgentRepository, TenantContextMiddleware)
- [x] `dotnet build` pass — 0 warnings, 0 errors

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

- [ ] Commit + push G1 backend ✅ (this commit)
- [ ] Cosmos: pull + run migrations 10/11/12 + rebuild gateway
- [ ] Verify: existing Contract Assistant data có `tenant_id='default'`
- [ ] Verify: existing users có `tenant_members` row
- [ ] Verify: `contract-monitor` agent created with lifecycle='production'
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
