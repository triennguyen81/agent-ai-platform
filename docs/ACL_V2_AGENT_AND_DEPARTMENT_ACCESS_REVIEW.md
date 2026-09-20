# ACL V2 — Agent & Department Access Review

## Addendum — final product rule: TENANT_OWNER does not manage agents at all

A later revision of ACL V2 made this rule absolute and final: TENANT_OWNER has **no**
agent-management responsibility of any kind — not tenant entitlement, not department
entitlement, not user assignment, not even read-only oversight. ADMIN's Agent authority is
scoped entirely to their own department's Department Agents. This section documents what
changed to enforce that; everything below "Scope traced" onward is the original review this
addendum builds on and remains accurate for the two bugs it describes.

**UI removed** (not just relabeled — deleted):
- `web/src/app/(app)/admin/tenant-agents/page.tsx` and `.../tenant-agents/[id]/page.tsx` —
  the tenant-wide "browse/assign platform agents" list + detail screens.
- `web/src/app/(app)/admin/tenant-users/[id]/agents/page.tsx` — the legacy per-user
  platform-agent-assignment screen (this is the page the original "Tenant chưa được gán
  agent nào" bug report was about).
- The "Agents" tab (assign/revoke a tenant-entitled agent to a department) on
  `web/src/app/(app)/admin/tenant-departments/[id]/page.tsx` — this was the TENANT_OWNER
  "assign agent to department" action, now fully removed; the Members/Admins/Overview/
  Nghiệp vụ tabs are untouched.
- The "Agent assignments" link on `web/src/app/(app)/admin/tenant-users/page.tsx`'s user
  list.
- Two compatibility-redirect stubs (`/admin/agents` for non-super_admin,
  `/department/agent-assignments`) now point at `/department/agents` or `/dashboard`
  instead of the removed pages.

**Backend policy tightened, not deleted.** `gateway/src/Aiq.Gateway/Endpoints/
DynamicAgentEndpoints.cs`'s `/tenant/agents/*` group and `/tenant/departments/{id}/agents`
group were narrowed from the `"admin"` policy (`super_admin`, `tenant_owner`, `admin`) to
`"platform_admin"` (`super_admin` only). Per the task's explicit instruction ("Do NOT delete
historical schema blindly... keep it internal only"), nothing was deleted — the tables
(`agent_tenant_assignments`, `agent_department_assignments`, `agent_assignments` for
legacy-chain rows), the repository methods, and the route mappings themselves are all
unchanged; only who is authorized to call them changed. This was verified safe because every
xUnit test exercising these code paths (`AgentDepartmentEntitlementIntegrationTests`,
`DepartmentManagementIntegrationTests`, `DepartmentRbacIntegrationTests`,
`LegacyAgentMigrationIntegrationTests`) calls the repository layer directly, never through
the HTTP authorization pipeline, so none of them were affected by the policy change (101/101
still pass). After the UI removal there is no remaining product-flow caller of these routes
at all — `super_admin` itself has no tenant/department claims in its own JWT (platform-wide,
not tenant-scoped), so in practice these endpoints are now unreachable by anyone through
normal use, while the underlying data and capability remain fully intact for a genuinely
internal/platform tool to use later if ever needed.

**What was already correct and needed no change:** Department Agent ownership
(`v2_scope='department'`), ADMIN's create→publish→assign flow
(`AssignUserToDepartmentAgentAsync`), `/me/agents` for both ADMIN and USER, runtime manifest
dispatch, and the TENANT_OWNER department-transfer flow (`MoveDepartmentAsync`) were all
already independent of this legacy tenant-agent-entitlement chain — confirmed in the
original review below. Removing TENANT_OWNER's access to it changes zero current ACL
decisions, exactly as required ("No current ACL decision may depend on TENANT_OWNER manually
granting an Agent").

**Remaining legacy tenant-agent dependency.** The schema and repository/endpoint code for
the legacy scope='system' entitlement chain (tenant→department→user) still exist, now gated
`platform_admin`-only. This is intentional, not an oversight — it is exactly the "keep it
internal only" outcome the task asked for. No further action is needed unless the schema
itself is later judged genuinely obsolete, which was explicitly out of scope here ("Do NOT
delete historical schema blindly").

---

Status: reviewed end-to-end, two real gaps found and fixed. The great majority of ACL V2
(SUPER_ADMIN / TENANT_OWNER / ADMIN / USER, Department Agent ownership, canonical
assignment, department transfer) was already correctly implemented across many prior
phases (see the `D2`/`D3`/`D5`/`ACL V2-C`/`ACL V2-D`/`ACL V2-E`/`ACL V2-G` comments
throughout `gateway/src/Aiq.Gateway/Governance/` and `gateway/src/Aiq.Gateway/Data/`).
This review traced every layer named in the task (schema, auth/RBAC, agent ownership,
assignments, sidebar/My Agents, Chat/Agentic authorization, department transfer) against
the authoritative model, verified it against real and synthetic data, and fixed the two
concrete defects found rather than re-designing anything already correct.

## Scope traced

- Schema: `agents` (`scope`, `v2_scope`, `owning_tenant_id`, `department_id` — migration
  007/024), `agent_tenant_assignments`, `agent_department_assignments`, `agent_assignments`,
  `departments`, `users.department_id`, `tools`/`agent_tool_grants`.
- Auth/RBAC: `PrincipalAuthorizationPolicy` (`IsPlatformAdmin`/`IsTenantGovernor`/
  `IsDepartmentAdmin`/`IsDepartmentMember`), `AuthenticatedPrincipalContext`,
  `TenantContextMiddleware`.
- Agent ownership & assignment: `IDynamicAgentRepository` (system-scope entitlement chain
  + V2-C Department Agent structural-ownership chain), `IAgentRepository`
  (`ListForPrincipalAsync` / `ListForDepartmentManagerAsync`), `IAgentAssignmentRepository`
  (`AuthorizeExecutionAsync`).
- Sidebar / My Agents: `GET /me/agents` → `web/src/app/(app)/layout.tsx` →
  `SideNav` + `web/src/app/(app)/my-agents/page.tsx`.
- Chat/Agentic authorization & runtime manifest: `GET /runtime/agents/{id}/manifest`
  (`ManifestAsync`, dispatches to `GetDepartmentAgentManifestAsync` first, then the legacy
  entitlement chain), `agent/src/aiq_agent/runtime/executors.py`'s manifest-driven tool
  resolution, and the separate legacy `GET /runtime/agents/{id}/authorization` surface
  (`agent/src/aiq_agent/agentic/registry.py`, `AgentKnowledgeEndpoints.ContextAsync`).
- User department transfer: `PATCH /tenant/users/{id}/department` →
  `MoveTenantUserDepartmentAsync` → `IUserRepository.MoveDepartmentAsync`.
- Existing tests: `AgentDepartmentEntitlementIntegrationTests.cs`,
  `AgentPrincipalVisibilityIntegrationTests.cs`, `DepartmentAgentOwnershipIntegrationTests.cs`,
  `DepartmentManagementIntegrationTests.cs`, `DepartmentRbacIntegrationTests.cs`,
  `PrincipalAuthorizationPolicyTests.cs`, `ExecutionAuthorizationTests.cs`,
  `LegacyAgentMigrationIntegrationTests.cs`.

## Bug reported: "Tenant chưa được gán agent nào" for a production Department Agent

**Root cause.** There are two independent, both-legitimate agent models in this codebase:

1. **Legacy / platform-scope** (`agents.scope='system'`): SUPER_ADMIN entitles the agent to
   a tenant (`agent_tenant_assignments`), then to a department (`agent_department_assignments`),
   then a user is granted it directly (`agent_assignments`). `web/src/app/(app)/admin/
   tenant-users/[id]/agents/page.tsx` (`GET /tenant/agents`) is the UI for exactly this
   chain — and only this chain.
2. **Department Agent** (`agents.v2_scope='department'`, migration 024): ownership is
   structural on the agent row itself (`owning_tenant_id`, `department_id`), never an
   entitlement row — created/published/assigned entirely under
   `web/src/app/(app)/department/agents/[id]` (tab "Nhân sự") by that department's own ADMIN.

A Department Agent can never have an `agent_department_assignments` row (enforced by
migration 007/024's ownership constraints), so `GET /tenant/agents` legitimately returns
zero rows for a tenant whose only agents are department-owned — the legacy page then showed
"Tenant chưa được gán agent nào," which reads as "this tenant has no usable agents at all,"
even while one or more Department Agents are production/active and fully assignable via the
correct screen.

**Fix (`web/.../admin/tenant-users/[id]/agents/page.tsx`).** Relabeled the page "Platform
agent assignments" (it only ever manages that one legacy chain), added a persistent banner
— "Agent khả dụng trong phòng ban được quản lý riêng tại Department Agents... không qua
trang này" — and reworded the empty state so it never claims the tenant has no agents, only
that it has no *platform-entitled* ones. No functionality removed: platform/global agent
entitlement management is unchanged and still works for tenants that legitimately use it.

## Second, related gap found: ADMIN's own sidebar/My Agents never showed their own Department Agent

While tracing "Do not fix only one page/record," the same class of bug existed one layer
down, on the endpoint `GET /me/agents` that powers **both** the sidebar agent switcher
(`web/src/app/(app)/layout.tsx` → `SideNav`) and the `/my-agents` page for an ADMIN caller.

`IAgentRepository.ListForDepartmentManagerAsync` (the ADMIN branch of `/me/agents`) only
ever queried `agent_department_assignments` — the same legacy entitlement table a Department
Agent can never have a row in. So an ADMIN who creates and publishes their *own* Department
Agent never saw it in their own sidebar or My Agents, even though it was already fully
executable via `GetDepartmentAgentManifestAsync(isAdmin: true)` (an ADMIN doesn't need a
direct `agent_assignments` row to run their department's own agent). Visible-in-chat and
executable-in-runtime had drifted apart — the exact inconsistency mandatory test #12 checks
for.

**Fix (`gateway/src/Aiq.Gateway/Governance/IAgentRepository.cs`,
`ListForDepartmentManagerAsync`).** UNIONed in a second branch selecting
`v2_scope='department'` agents owned by the caller's own tenant+department — the same
pattern `ListForPrincipalAsync` already used for its USER-role chain 2. No entitlement row
required, matching how execution authorization already treats department ownership.

Verified live against the real running database (direct repository call, same technique used
in prior phases of this session — no HTTP credentials available for the real tenant): before
the fix, `ListForDepartmentManagerAsync` for tenant `74a79d3f-…` / department `0facef04-…`
returned only the one legacy-chain agent (`ke-toan`); after the fix it returns 7 agents,
including the 6 Department Agents (`aaaaaa`, `test1233`, `onboarding-smoke-dba648e8`, `test`,
`tettt`, `testzzz`) that were previously invisible to their own department's ADMIN.

## Third gap found: cross-department leak in the legacy execution-authorization endpoint

While confirming Chat/Agentic authorization treats Department Agents consistently, found
that `GET /runtime/agents/{id}/authorization` (`IAgentAssignmentRepository.
AuthorizeExecutionAsync`) — used directly by `AgentKnowledgeEndpoints.ContextAsync` (RAG
context for Chat) and by the agent runtime's legacy tool/model registry lookups
(`agent/src/aiq_agent/agentic/registry.py`) — checked **tenant** match only, never
department. A Department Agent's `agents.tenant_id` equals its owning tenant (required by
the `agents_scope_ownership_valid` check constraint), so an ADMIN of a *different*
department in the *same* tenant satisfied `isTenantAdmin && sameTenant` and could read
another department's agent config/knowledge — a real cross-department leak, directly
violating "Cross-department assignment/access → denied."

Confirmed this endpoint is not reachable with a real department-scoped `agent_id` through
the primary Chat/Agentic execution path (`POST /agent/run` only calls the legacy
`run_task`/registry-lookup path when no `agent_id` is resolved at all — a real agent_id
always routes through the already-correct `require_agent_execution` → manifest path
instead), but `GET /runtime/agents/{id}/authorization` itself requires only the generic
`"user"` policy and is directly callable by any authenticated caller, and
`AgentKnowledgeEndpoints.ContextAsync` (a live Chat RAG-context read) is driven by it too —
so this was a real, directly-exploitable gap, not dead code.

**Fix (`gateway/src/Aiq.Gateway/Governance/IAgentAssignmentRepository.cs`,
`AgentEndpoints.cs`, `AgentKnowledgeEndpoints.cs`).** Added `currentDepartmentId` to
`AuthorizeExecutionAsync`, selected the agent's own `v2_scope`/`department_id`, and added a
`sameDepartment` requirement (`v2_scope != 'department' OR currentDepartmentId ==
agent.department_id`) to `ExecutionAuthorizationRules.CanExecute`, alongside the existing
`sameTenant` check. Legacy `scope='system'` agents (`v2_scope IS NULL`) are unaffected —
department isolation for those is already enforced further up the D3 entitlement chain, not
here. `super_admin` keeps its full bypass.

## Everything else traced and confirmed already correct (no change)

- **Runtime manifest dispatch** (`DynamicAgentEndpoints.ManifestAsync`): Department Agent
  ownership is checked first and is authoritative (`IsResolvedDepartmentAgentAsync` →
  `GetDepartmentAgentManifestAsync`), never falling through to the legacy chain — a
  Department Agent belonging to another department is unreachable even with its raw UUID.
- **USER-role `/me/agents`** (`ListForPrincipalAsync`): already correctly UNIONs both the
  legacy entitlement chain and structural Department Agent ownership (this was the template
  the two ADMIN-side fixes above now match).
- **Canonical assignment** (`AssignDepartmentAgentAsync`/`AssignUserToDepartmentAgentAsync`
  in `IDynamicAgentRepository.cs`): already enforces same tenant, same department, active
  department, agent structural ownership — exactly the spec.
- **User department transfer** (`IUserRepository.MoveDepartmentAsync`,
  `UserEndpoints.MoveTenantUserDepartmentAsync`): already transactional, already tenant-
  derived from server auth context (never from the request body), already audits
  `tenant_user.department_move`, already revokes (status flip, never delete) any
  `agent_assignments` row not also entitled/owned in the destination department, already
  gated by the `tenant_governance` policy (`Role == "tenant_owner"` exactly — an ADMIN
  cannot call it). The SQL correctly handles both agent models: for a legacy-chain agent it
  keeps the assignment only if `agent_department_assignments` is also active for the new
  department; for a Department Agent (which can never have such a row) the assignment is
  unconditionally revoked, which is correct since a Department Agent belongs to exactly one
  department by construction.
- **UI**: `web/src/app/(app)/admin/tenant-users/page.tsx` already has TENANT_OWNER-only
  "Chuyển phòng ban" with current/destination department and confirmation
  (`moveDepartmentAction`); ADMIN never sees this control (`isOwner` gate) and their own
  department is otherwise fixed by their JWT-derived `DepartmentId`, never client-editable.

## Files changed

- `gateway/src/Aiq.Gateway/Governance/IAgentRepository.cs` —
  `ListForDepartmentManagerAsync` now includes Department Agents.
- `gateway/src/Aiq.Gateway/Governance/IAgentAssignmentRepository.cs` —
  `AuthorizeExecutionAsync`/`ExecutionAuthorizationRules.CanExecute` now department-aware.
- `gateway/src/Aiq.Gateway/Endpoints/AgentEndpoints.cs` — passes caller's `DepartmentId`.
- `gateway/src/Aiq.Gateway/Endpoints/AgentKnowledgeEndpoints.cs` — same.
- `web/src/app/(app)/admin/tenant-users/[id]/agents/page.tsx` — relabeled, banner, honest
  empty state.
- `gateway/tests/Aiq.Gateway.Tests/Data/AgentPrincipalVisibilityIntegrationTests.cs` — 2 new
  tests for the sidebar/My Agents fix.
- `gateway/tests/Aiq.Gateway.Tests/Security/ExecutionAuthorizationTests.cs` — 4 new tests for
  the department-aware authorization rule.
- `gateway/tests/Aiq.Gateway.Tests/Security/PrincipalAuthorizationPolicyTests.cs` — explicit
  ADMIN-cannot-govern-tenant assertion.

## Migrations

None. The structural model (migration 024, `v2_scope`/`owning_tenant_id`/`department_id`)
already existed and was already sufficient — both bugs were query/logic gaps in code that
had not yet been updated to consult it, not schema gaps. No manual SQL was used as a fix;
every change is in application code plus regression tests.

## Real-data verification — limitation

The task named specific production records (`kettoan1@vietdang.vn`, tenant `vd-accounting`,
agent id `203114ed-fdbe-4f28-ab1f-23c5d3bdc656`). None of these exist in the only database
reachable from this environment (confirmed by direct query — 0 rows for all three). This
environment has no SSH/kubectl/remote-docker access to a separate production system (checked
before concluding this, same finding as the immediately preceding task in this session).
Verification was instead done against:
- the newly added xUnit regression tests (synthetic but structurally identical fixtures),
  all passing;
- a direct live read against this environment's real database using the same repository
  code the HTTP endpoint calls, against real (non-synthetic) locally-seeded records
  including a Department Agent literally named "Trợ lý kế toán" (`ke-toan`, different id
  than the one named in the task) and department `testzzz`.

If given read access to the actual production database, the same before/after
`ListForDepartmentManagerAsync` read against tenant `vd-accounting` / agent
`203114ed-fdbe-4f28-ab1f-23c5d3bdc656` would give a direct confirmation against the named
records.
