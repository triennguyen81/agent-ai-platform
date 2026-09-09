# Repository Cleanup Audit (post-ACL V2)

Status: **audit complete, read-only — nothing was deleted, moved, or modified.**
Date: 2026-09-06.

Pre-audit `git status`/`git diff --stat`/`git diff` were captured; the tree
already carried the full, uncommitted D1-D8 + ACL V2-A-through-Final history
(179 changed/untracked paths). Nothing was touched. No `git reset`/`stash`/
`checkout .`/`restore .`/`clean` was run. This document is the deliverable —
no code, doc, or script was changed as part of producing it.

Methodology: direct reference/import/route-mapping search across Gateway
(C#), Agent (Python), Web (Next.js), scripts, and docs, plus one delegated
`Explore` sub-agent pass focused specifically on Web reachability (nav-model,
`redirect()` call sites, `<Link>`/`fetch()` call sites, `middleware.ts`
rewrite rules). Every candidate below has explicit evidence, not filename
guessing. Migrations 001-026 were not touched or evaluated as removal
candidates — they are correctly immutable per the task's own instruction.

---

## 1. Dead source files

### 1.1 Gateway (C#) — **none found**

Checked every `Endpoints/*.cs` class against `Program.cs`'s `Map*Endpoints()`
calls (all mapped, none orphaned) and every `Governance|Data|Notes|Contracts|
Vouchers|Books|Customers|Suppliers|Employees/I*Repository.cs` interface
against its DI registration *and* actual use inside an endpoint/job file (all
registered, all consumed — including the newest V2-G/H additions
`ISkillRepository`, `IDepartmentKnowledgeRepository`). `Governance/DepartmentAccessPolicy.cs`
and `Governance/PrincipalAuthorizationPolicy.cs` looked like they might
overlap — confirmed they serve distinct purposes (`DepartmentAccessPolicy` is
consumed by `Middleware/TenantContext.cs`'s per-request scope validation;
`PrincipalAuthorizationPolicy` backs the exact-role policy predicates
registered in `Program.cs`) and both are actively used.

| Path | Category | Risk | Action |
|---|---|---|---|
| — | — | — | **No candidates.** |

### 1.2 Python (Agent) — one orphaned test harness

`agent/src/aiq_agent/**` — every module has at least one importer (verified
individually for the least-obviously-used ones: `orchestration/pec.py`,
`memory/{in_memory,postgres_store,redis_store}.py`, `observability/{cost,tracing}.py`,
`tools/registry.py`, `agentic/*.py`). No dead Python modules found in the
runtime package itself.

| Path | Category | Why it appears unused | Evidence | Risk | Action |
|---|---|---|---|---|---|
| `agent/tests/phase3c_live_e2e.py` | Test harness, superseded | Named `phase3c_live_e2e.py` — does **not** match pytest's default `python_files` discovery pattern (`test_*.py`/`*_test.py`, set in `pyproject.toml:78 testpaths=["tests"]`), so `pytest -q` never collects it. It is `unittest`-based and gated behind `@unittest.skipUnless(os.getenv("RUN_AIQ_PHASE3C_E2E")=="1", ...)`, requiring `TENANT_A_ID`/`TENANT_B_ID`/`USER_A_ID`/`USER_B_ID`/`SUPER_ADMIN_ID`/`JWT_SIGNING_KEY` env vars pointing at real, specific, already-seeded IDs on a live deployment. "Phase 3C" predates the current D1-D8/ACL-V2 phase-naming scheme entirely (an earlier project era). | Read the file in full; confirmed no `test_*`/`*_test` alias exists; confirmed no CI workflow or script sets `RUN_AIQ_PHASE3C_E2E`; confirmed the required tenant/user IDs are not defined anywhere in current fixtures/docs. | LOW | **ARCHIVE** (real historical value as a hand-rolled live-verification harness; not currently runnable without live IDs this repo no longer documents, and orphaned from all automated discovery) |

### 1.3 Next.js (Web) — components and API routes

See §2.3 and §3-equivalent Web findings below (grouped there since most of
the Web dead-code findings are route-shaped, matching the audit's own
category boundaries better under "legacy routes/UI").

Two standalone dead components (Explore-agent-verified, zero importers
anywhere in `app`/`components`/`lib`):

| Path | Category | Why it appears unused | Evidence | Risk | Action |
|---|---|---|---|---|---|
| `web/src/components/textarea-with-mic.tsx` | Orphaned client component | `TextareaWithMic` has zero importers repo-wide | `grep -rn "textarea-with-mic\|TextareaWithMic" app components lib` returns only its own definition | LOW | **SAFE_DELETE** |
| `web/src/components/bulk-md-upload.tsx` | Orphaned client component | `BulkMarkdownUpload` has zero importers repo-wide (a different, still-used component, `save-to-kb-button.tsx`, independently posts to the same `/api/notes` endpoint without importing this one) | `grep -rn "bulk-md-upload\|BulkMarkdownUpload" app components lib` returns only its own definition | LOW | **SAFE_DELETE** |

### 1.4 Old RBAC/ACL code superseded by ACL V2 — **none found as separate dead files**

The pre-V2 RBAC surfaces (`agent_acl`/`IAgentRepository.cs`, legacy
`agent_tool_grants` vs. current `agent_version_tool_grants`, `tenant_members`)
were independently re-confirmed this pass as **still live and actively used**
— not superseded, not dead — matching `D8_PRODUCTION_READINESS_AUDIT.md §9`'s
own finding and `ACL_V2_FINAL_MIGRATION_PRODUCTION_HARDENING_REPORT.md §7`'s
explicit "nothing was removed, no compatibility route was found to be
provably unused" conclusion from this same session's prior phase. Re-auditing
this yet again this pass would be redundant; both prior audits' conclusions
still hold (no source file changed since either was written).

---

## 2. Legacy routes/UI

### 2.1 Pages replaced by redirects — confirmed correct, no action

`web/src/app/(app)/knowledge`, `tenant/users`, `tenant/department-admins`,
`department/members`, `department/agent-assignments`, and the
`system|tenant|department`-prefixed `/audit`, `/usage`, `/reports` family
(re-exporting `../../admin/audit/page`) are all genuine, working
compatibility redirects/re-export shims exactly as `ACL_V2_V2F_WORKSPACE_NAVIGATION_REPORT.md`
describes. **KEEP all** — this is the intended, documented consolidation
pattern, not leftover duplication.

### 2.2 Old Agent Builder UI / duplicate role-specific screens

`web/src/app/(app)/admin/agents/**` (the pre-Agent-Builder legacy per-agent
ACL/lifecycle page) is **not** dead — it is the deliberately-preserved
legacy system `admin/tenant-agents/page.tsx` itself links to for its
"Trợ lý legacy" section, and is reachable directly by `super_admin`.
**KEEP.**

### 2.3 A real product regression found: three fully-built SUPER_ADMIN pages orphaned by the V2-F nav rewrite

| Path | Category | Why it appears unused | Evidence | Risk | Action |
|---|---|---|---|---|---|
| `web/src/app/(app)/admin/users/{page,layout}.tsx` | Orphaned page (nav gap, not dead code) | Fully implemented, working SUPER_ADMIN user directory with a working auth guard — but absent from `lib/nav-model.ts`'s `buildQuanTriGroups` super_admin branch and from `dashboard/page.tsx`'s super_admin card list | `docs/PHASE_D7_NAVIGATION_FUNCTION_CONSOLIDATION_REPORT.md:32` explicitly lists `/admin/users` as **KEEP, visible**; `ACL_V2_V2F_WORKSPACE_NAVIGATION_REPORT.md`'s "Visible navigation" table for `super_admin` and `nav-model.test.ts`'s own assertion list both omit it | MEDIUM | **MANUAL_REVIEW** — restore the nav-model.ts entry (product decision), don't delete the page |
| `web/src/app/(app)/admin/skills/{page,layout}.tsx` | Orphaned page (nav gap) | Same pattern — D7 said KEEP+visible, V2-F's `nav-model.ts` omits it. Only in-repo references left are a code comment in `department/skills/layout.tsx` and the D7 report itself | Same nav-model.ts / nav-model.test.ts / V2-F report cross-check | MEDIUM | **MANUAL_REVIEW** |
| `web/src/app/(app)/admin/workflows/{page,layout}.tsx` | Orphaned page (nav gap) | Same pattern | Same cross-check | MEDIUM | **MANUAL_REVIEW** |

These three are **not cleanup candidates** — they are fully functional,
correctly-guarded pages that a prior phase (D7) explicitly decided to keep
visible in navigation, and a later phase (V2-F)'s nav-model rewrite silently
dropped the links for. Flagging as a genuine finding for a human product
decision (re-add the nav entries, or explicitly ratify "URL-only, no menu
entry" as the new intended state) rather than a deletion candidate.

### 2.4 Duplicate role-specific screens — none found beyond the above

`customers/suppliers/expenses/mau-chung-tu` page trees are absent from
top-nav but are deliberately deep-linked utility/master-data pages per
`PHASE_D7_NAVIGATION_FUNCTION_CONSOLIDATION_REPORT.md:25`'s own explicit
"KEEP" — cross-linked from voucher detail pages and the training-center
walkthrough component, not orphaned. **KEEP.**

### 2.5 Stale navigation components — none found

`components/app-nav.tsx` is the single current nav-rendering component (no
older duplicate found).

---

## 3. Tests/scripts

### 3.1 Gateway xUnit tests — **all KEEP**

Every file under `gateway/tests/Aiq.Gateway.Tests/` (30 test files) is
currently part of the passing 189/189 suite confirmed at the end of the
ACL-V2-Final phase. Per this task's own instruction, a test is not
"unused" merely because production code doesn't reference it — these are
all independently invoked by `dotnet test` and all currently green. **No
candidates.**

### 3.2 Real-HTTP E2E scripts — **all KEEP, no duplicates**

`scripts/test_fresh_database_bootstrap.sh`, `test_d3..d7_*.sh`,
`test_acl_v2a_populated_upgrade.sh`, `test_v2d_*.sh`, `test_v2e_*.sh`,
`test_v2_final_*.sh` — each covers a distinct phase's distinct security
matrix (confirmed by content, not just filename — each script's own header
comment states what it uniquely proves); none duplicate another's coverage.
All currently pass. **No candidates.**

### 3.3 Web dead API route handlers (BFF proxy layer superseded by Server Actions)

The Explore sub-agent traced every `/api/**` route handler against every
`fetch(...)` call site in `app`/`components` and found a coherent pattern: a
whole family of client-fetch-era BFF proxies that the app's later
Server-Component/`"use server"` pages bypass entirely, calling `gateway.*`
(in `lib/api.ts`) directly instead.

| Path group | Count | Why it appears unused | Evidence | Risk | Action |
|---|---:|---|---|---|---|
| `app/api/agents/**` (14 route files: base, `[id]`, `acl`, `acl/[principalId]`, `disable`, `enable`, `gates`, `knowledge`, `knowledge/[noteId]`, `knowledge/attachable`, `review-requests`, `tools`, `tools/[toolId]`, `transitions`) | 14 | Proxies to `gateway.listAgents/getAgent/...acl/...gates/...knowledge/...tools/...transitions`; the actual consumer (`admin/agents/[id]/page.tsx`) calls those same `gateway.*` methods directly as Server Actions, never through this proxy | Zero `fetch("/api/agents...")` call sites found anywhere in `app`/`components` | MEDIUM | **MANUAL_REVIEW** |
| `app/api/gates/[id]/route.ts` | 1 | Same pattern (`gateway.updateGate`/`deleteGate` called directly instead) | Same | MEDIUM | **MANUAL_REVIEW** |
| `app/api/hitl/{actions,queue}/route.ts`, `app/api/hitl/[id]/decide/route.ts` | 3 | Same pattern (`admin/hitl-queue/page.tsx` and `admin/agents/[id]/page.tsx` call `gateway.hitlQueue`/`decideHitlAction`/`createHitlAction` directly, one site even raw-`fetch`ing the Gateway URL directly, bypassing `lib/api.ts` too) | Same | MEDIUM | **MANUAL_REVIEW** |
| `app/api/me/{agents,tenants,review-queue}/route.ts` | 3 | Same pattern (`my-agents`/`chat`/`admin/review-queue` pages call `gateway.listMyAgents`/`myReviewQueue` directly) | Same | MEDIUM | **MANUAL_REVIEW** |
| `app/api/review-boards/**` (base, `[id]/members`, `[id]/members/[userId]`) | 3 | `admin/agents/[id]/page.tsx` calls `gateway.listReviewBoards` directly; **no page anywhere renders board-membership management at all** — looks like a review-board membership UI that was never built, not just relocated | Zero callers of `listReviewBoards`'s sibling member-management methods anywhere in `app` | MEDIUM | **MANUAL_REVIEW** |
| `app/api/review-requests/[id]/route.ts`, `app/api/review-requests/[id]/votes/route.ts` | 2 | Same "never wired to any page" pattern — `lib/api.ts`'s `castReviewVote`/`listReviewVotes`/`getReviewRequest` have zero callers anywhere | Same | MEDIUM | **MANUAL_REVIEW** |
| `app/api/tenants/route.ts`, `app/api/tenants/[id]/members/route.ts`, `app/api/tenants/[id]/members/[userId]/route.ts` | 3 | `admin/tenants/page.tsx` calls `gateway.listTenants` directly; `lib/api.ts`'s `listTenantMembers`/`inviteTenantMember`/`removeTenantMember` have zero callers — likely superseded by the D5 tenant-user model (`createTenantUser`/`changeTenantUserRole`) | Same | MEDIUM | **MANUAL_REVIEW** |
| `app/api/tools/route.ts`, `app/api/tools/[id]/route.ts` | 2 | `admin/tools/page.tsx`, `admin/agent-builder/[id]/page.tsx`, `department/agents/[id]/page.tsx` all call `gateway.listTools`/`createTool`/`updateTool` directly | Same | MEDIUM | **MANUAL_REVIEW** |
| `app/api/users/[id]/agents/route.ts` | 1 | Superseded by `admin/tenant-users/[id]/agents/page.tsx`'s direct server-side calls | Same | MEDIUM | **MANUAL_REVIEW** |

**All 32 route files classified MEDIUM/MANUAL_REVIEW, not SAFE_DELETE**,
because — as the Explore agent's own caveat states — these are **public
HTTP endpoints**; the audit confirmed no in-app UI calls them, but could not
rule out an external client (mobile app, third-party integration, a
monitoring probe, or a browser-bookmarked direct API call) depending on
them. Deleting a live route is harder to reverse than deleting an unimported
`.tsx` file. A human with visibility into actual production access logs
should confirm zero-traffic before removal — exactly the same "prove unused
before removing" standard `ACL_V2_FINAL_MIGRATION_PRODUCTION_HARDENING_REPORT.md §7`
already applied to Gateway-side legacy routes.

**Contrast** — the following `/api/**` routes were confirmed genuinely used
(called from client components, where a BFF proxy is architecturally
required) and are **KEEP**: `vouchers/**`, `contracts/**`, `chat/**`,
`notifications/**`, `notes`, `workflows`, `admin/health`, `agent/run`,
`checklist/[id]`, `auth/sync`, `department-agents/[id]/test`,
`system-agents/[id]/test`.

### 3.4 One-off debugging/verification scripts, stale fixtures — **none found in this pass beyond §3.3/§1.2**

No `.bak`/`.orig`/editor-swap/`~`-suffixed files are tracked by git
(`git ls-files` grep came back empty for all such patterns).

### 3.5 Operational scripts predating this session (`scripts/*.sql`, `scripts/*.py`, `scripts/*.sh`)

These are not test infrastructure — they are operator-invoked data-seeding
and one-off migration/report utilities, most dated 2026-07-21 through
2026-08-25 (before the D1-D8/ACL-V2 phase sequence began). None are imported
by code; that is expected for this category and does not by itself mean
unused (matching the spirit of "do not classify a test as unused merely
because production code does not reference it" applied to operational
tooling).

| Path | Category | Why flagged | Evidence | Risk | Action |
|---|---|---|---|---|---|
| `scripts/seed_demo_agents.sql` | Demo/legacy seed | Seeds the legacy `agent_tool_grants` table | Directly named in `D8_PRODUCTION_READINESS_AUDIT.md §9` as the seed source for a table confirmed "still live, actively used" | LOW | **KEEP** |
| `scripts/vietdang_simulator_ap_ledger_seed.sql`, `scripts/vietdang_simulator_grn_seed.sql` | Current canonical seed path | These are the **replacement** scripts D1.5 introduced after removing the problematic automatic `19_sim_ap_ledger.sql`/`19b_sim_grn.sql` init files | `PHASE_D1_5_DATABASE_BOOTSTRAP_HARDENING_REPORT.md §4/§11` names them explicitly as the fix | LOW | **KEEP** |
| `scripts/vietdang_simulator_seed.sql`, `scripts/vietdang_accounting_setup.sql`, `scripts/vietdang_simulator_phase2.sql` | Prerequisite demo data | D1.5's own dependency graph (§3) shows the AP/GRN seed scripts above have a hard FK dependency on `sim_suppliers`, created only by `vietdang_simulator_seed.sql` | `PHASE_D1_5_DATABASE_BOOTSTRAP_HARDENING_REPORT.md §2/§3` | LOW | **KEEP** |
| `scripts/bsn_demo_content.sql`, `bsn_finance_content.sql`, `bsn_pilot_setup.sql`, `bsn_real_content.sql`, `bsn_ma_advisor_setup.sql` | BSN-tenant operational setup | Zero references in current docs/code, **but** `bsn_ma_advisor_setup.sql` corresponds 1:1 to a real, deliberately-built feature: the commit history (`2026-07-27 "feat(skills): 21 M&A / IB skills + bsn-ma-advisor agent setup"`) confirms these set up a genuine second business line (M&A advisory) for the BSN tenant, alongside its accounting/finance one | `git log` on `agent/skills/ma-*`; cross-referenced against `agent/skills/` contents (§7 dependency note) | MEDIUM | **MANUAL_REVIEW** — very likely still relevant to a live tenant, cannot confirm current invocation cadence from the repo alone; do not delete without asking the operator (this repeats D8's own explicit caution about the live BSN pilot) |
| `scripts/generate_bsn_handover.py` | One-shot docx generator | Generates `bsn_handover.docx` (a client handover document) — a one-time-event artifact generator, tied to a specific already-happened handover | Read file header directly: `"""Generate bsn_handover.docx — handover document for anh Toàn. ... ready-to-send."""` | LOW | **ARCHIVE** |
| `scripts/generate_report.py` | Superseded reporting scheme | Generates `report_task_done.docx`, explicitly covering **"Contract Monitoring Assistant (Phases 1-3)" and "Agent Governance Framework (G1-G4)"** — a project-management scheme that predates and was superseded by the current `docs/PHASE_D*`/`docs/ACL_V2_*` per-phase report convention | Read file header directly | LOW | **ARCHIVE** |
| `scripts/seed_vietnam_knowledge.sql` | Demo/reference content seed | Zero code/doc references found; large (56K) Vietnamese-language knowledge-base seed | grep across `*.md`/`*.sh`/`*.yml` for the filename returns nothing | LOW-MEDIUM | **MANUAL_REVIEW** |
| `scripts/migrate_vouchers_to_s3.sh` | One-shot, likely-complete migration | Self-documented as a "one-shot migration of pre-E-4 voucher files" from the old Docker-volume path to S3; current `VoucherEndpoints.cs` already exclusively uses `IObjectStorage`/S3, meaning this migration has very likely already run to completion on the live pilot | Read file header directly (`migrate_vouchers_to_s3.sh:1-9`); cross-checked against `docs/arch/s3-migration-plan.md` and current `VoucherEndpoints.cs` S3-only code path | LOW | **ARCHIVE** (idempotent — safe to keep as a disaster-recovery/reference tool even though its main job is likely done) |
| `scripts/pin_web_network.sh` | **Active** operational security hook | Initially looked orphaned (zero repo cross-references) but its own header identifies it as a **required post-deploy step** ("Run this AFTER any of: `docker compose up -d web`, `docker restart aiq-web`, ...") that pins static IPs and re-asserts iptables outbound-block rules — this is the actual mechanism behind the network-hardening claims `D8_PRODUCTION_READINESS_AUDIT.md §6` examined | Read file header directly | LOW | **KEEP** — this is a real, load-bearing operational runbook step, not dead code; it simply isn't referenced *by name* anywhere because it's invoked manually per the deploy runbook rather than from another script |

---

## 4. Docker/dev artifacts

| Path | Category | Evidence | Risk | Action |
|---|---|---|---|---|
| `docker-compose.yml` | Local dev compose | Actively validated by CI's `compose-validate` job; referenced throughout this session's D8/V2-H hardening work | — | **KEEP** |
| `docker-compose.cosmos.yml` | Production-adjacent overlay | Same CI job covers it; is the actual source of the container-hardening claims examined in D8 §6 | — | **KEEP** |
| `agent/Dockerfile`, `web/Dockerfile`, `gateway/src/Aiq.Gateway/Dockerfile` | One per service | Exactly 3 Dockerfiles for 3 services, no stale duplicates (no `Dockerfile.dev`/`Dockerfile.old` found) | `find . -iname "Dockerfile*"` | — | **KEEP** |
| `.env` (repo root) | Local secrets | Confirmed **not tracked by git** (`git ls-files .env` returns nothing) — correct, matches D8's own finding that `.env` was never committed | — | **KEEP** (not a repo artifact at all) |
| `.env.example` | Placeholder template | Uses only fake placeholder values per D8's own confirmation; still the only env template in the repo | — | **KEEP** |
| `gateway/**/obj`, `gateway/**/bin`, `web/.next`, `web/node_modules`, `agent/**/__pycache__` | Generated build output | All correctly listed in `.gitignore` (`bin/`, `obj/` confirmed; `.next`/`node_modules`/`__pycache__` are standard-ignored) — not tracked by git at all, present only on this local disk (7.5M+27M+2.3M+36M+416M+648M+360K ≈ **1.14 GB**, entirely regenerable via `dotnet build`/`npm install`/`next build`/`pip install`) | `du -sh` on each; `.gitignore` contents | — | **SAFE_DELETE (local disk only — has zero effect on the repository itself; not a git-tracked cleanup item)** |

No stale compose override files, no unused Dockerfiles, no obsolete `.env.*`
variants were found.

---

## 5. Docs — classification

### KEEP (current, actively authoritative)

`docs/ACL_V2_ARCHITECTURE_{DECISIONS,REVIEW}.md`, `ACL_V2_IMPLEMENTATION_PLAN.md`,
`ACL_V2_V2{A,B,C,D,E,F}_*_REPORT.md`, `ACL_V2_FINAL_MIGRATION_PRODUCTION_HARDENING_REPORT.md`,
`D8_PRODUCTION_READINESS_AUDIT.md`, `PHASE_D1..D7*_REPORT.md` (still cited
by name as required reading in every subsequent phase's own brief),
`DEPARTMENT_RBAC_ARCHITECTURE_DECISIONS.md`, `DEPARTMENT_TENANT_OWNER_AGENT_CAPABILITY_ARCHITECTURE_REVIEW.md`
(both explicitly read-as-baseline by `ACL_V2_ARCHITECTURE_REVIEW.md`),
`huong-dan.md` (live pilot demo guide, actively cited by D8),
`skills_authoring.md` (**actively linked from a live page**,
`web/src/app/(app)/admin/skills/page.tsx:41` — must not be archived without
first updating that page), `docs/architecture/overview.md` (stale content —
see MANUAL_REVIEW below — but still the canonically-named architecture doc
location), `docs/arch/s3-migration-plan.md`, `docs/operations/disaster-recovery.md`,
`docs/security/encryption-at-rest.md`, `docs/accounting_seed/**`,
`docs/demo-templates/**`, `docs/reference/ib-skills-pack/**` (confirmed this
is the **source/staging library** — 21 skills, unprefixed names — for the 10
`ib-*`-prefixed skills actually deployed under `agent/skills/`; a legitimate
content-management split, not duplication), `infra/postgres/init/README.md`.

### MANUAL_REVIEW (content is stale relative to the current ACL V2 model or overlaps another doc; do not archive without a decision)

| Path | Why | Risk |
|---|---|---|
| `docs/architecture/overview.md` | Explicitly self-dated "Reflects the Phase 1-4 implementation as of 2026-06-10... update this document in the same PR" — has not been updated through the entire D1-D8/ACL-V2 sequence; now materially stale (predates department-scoped ACL entirely) | MEDIUM |
| `docs/PROJECT_REVIEW_AND_ARCHITECTURE.md` | Untracked (`git status` shows it as `??`), dated 2026-08-31 — more comprehensive (580 lines) and more recent than `architecture/overview.md`, but **still predates the entire D1-D8/ACL-V2 phase sequence** (which began ~2026-09-01). Zero references from any other doc or code. Worth a decision: promote as the new `architecture/overview.md` replacement (after an ACL-V2 refresh pass), or archive alongside the June cluster below | MEDIUM |
| `docs/README.md` | The **canonical docs index** still points readers at `../plan (3).md` ("Original 4-6 month roadmap") and `../progress-check.md` ("Live milestone tracker — updated per commit") as the primary architecture/progress references — neither has been touched since long before D1, and **none of the ~20 current phase reports are indexed here at all**. This is the single most actionable doc-hygiene finding in this audit: a new engineer following this index would miss the entire current documentation set | HIGH (misleads onboarding, not a security issue) |
| `docs/voucher/PLAN.md`, `docs/voucher/PROGRESS.md` | Feature-specific plan/progress tracker for voucher processing (kickoff 2026-07-21) — the feature is now fully built and independently documented via `PHASE_D6_BUSINESS_PAGE_CAPABILITY_CONVERSION_REPORT.md` and D3/D4/D6 E2E; this tracker was never updated to reflect completion | LOW |

### ARCHIVE (real historical value, superseded by current phase-report convention; a single cohesive early-project cluster)

All of the following cross-reference each other extensively (confirmed via
grep) and predate the D1-D8/ACL-V2 phase sequence entirely — they represent
a **complete, internally-consistent, now-superseded planning/reporting
system** (including its own `.docx` generator scripts), not scattered
one-offs:

`plan.md`, `plan (3).md`, `plan_agent_governance.md`,
`progress-check.md`, `progress-check-agent-governance.md`,
`progress-check-contracts.md`, `Baocao.md`, `baocao.docx`,
`report_task_done.docx`, `bsn_handover.docx`,
`docs/Ketoan_plan.md`, `docs/ketoan_checklist.md`, `docs/roadmap.md`,
`docs/architecture_structure.md`, `docs/implement_progress_status.md`,
`docs/claude_agent_sdk_integration_plan.md`, `docs/stt_voice_input_decision.md`,
`docs/generate_report.py`, `docs/generate_ketoan_plan.py`, `docs/generate_baocao.py`,
`CODEX_MULTI_TENANT_MULTI_AGENT_IMPLEMENTATION.md` (zero references anywhere;
predates ACL V2's own architecture-review documents which cover the same
multi-tenant-multi-agent ground more currently), `use-case-spec-xu-ly-chung-tu-ke-toan.md`
(zero references; the voucher/document-processing use case it specs is now
built and covered by D3/D4/D6), `docs/vietdang_demo_test_cases.md`,
`docs/demo-cheatsheet-anh-toan.md`, `docs/plan-agent-tro-ly-ke-toan.md`
(zero references despite a 2026-09-01 date — same week D1 began, but not
part of the current phase-doc chain).

Approximate size: **432 KB** across ~24 files.

**Do not delete any of these** per the task's explicit instruction — they
document real decisions and real client history. "Archive" here means: move
to a clearly-labeled `docs/archive/` (or equivalent) location and drop them
from `docs/README.md`'s active index, once a human confirms none is still
an open reference for an active client conversation.

---

## 6. Generated/temp files

No tracked logs, caches, coverage output, or accidental editor files were
found anywhere in the git index (`git ls-files` search for `.log`/`.tmp`/
`.bak`/`.orig`/`.DS_Store`/`~`-suffixed paths returned nothing). All
generated build output (`obj/`, `bin/`, `.next/`, `node_modules/`,
`__pycache__/`) is correctly gitignored and exists only as local disk state
(~1.14 GB, see §4) — safe to delete at any time with zero repository impact,
regenerated by the normal build/install commands.

---

## 7. Dependencies

### 7.1 Web (npm) — 6 confirmed-unused packages

Checked every `dependencies` entry in `web/package.json` against actual
import-specifier usage in `web/src/**` (not just presence of the package
name in a comment):

| Package | Evidence | Approx. install size | Risk | Action |
|---|---|---:|---|---|
| `@radix-ui/react-dialog` | Zero imports anywhere; no `components/ui/dialog.tsx` wrapper exists | 128 KB | LOW | **SAFE_DELETE** |
| `@radix-ui/react-label` | Zero imports; the app's actual `Label` component (`components/ui/input.tsx`) is hand-rolled, not Radix-based | 44 KB | LOW | **SAFE_DELETE** |
| `@radix-ui/react-scroll-area` | Zero imports; no wrapper component exists | 232 KB | LOW | **SAFE_DELETE** |
| `@tanstack/react-query` | Zero imports anywhere in `src/` | 2.0 MB | LOW | **SAFE_DELETE** |
| `class-variance-authority` | Zero imports anywhere | 52 KB | LOW | **SAFE_DELETE** |
| `zod` | Zero imports anywhere (validation is done via hand-rolled checks + Gateway-side DataAnnotations, not a shared Zod schema layer) | 5.2 MB | LOW | **SAFE_DELETE** |

**Total potential reduction: ~7.6 MB of `node_modules`, 6 fewer
`package.json` dependencies.** Verifiable in one command after removal:
`npm uninstall @radix-ui/react-dialog @radix-ui/react-label @radix-ui/react-scroll-area @tanstack/react-query class-variance-authority zod && npx tsc --noEmit && npx next build`.

Packages actually in use and correctly kept: `@clerk/nextjs` (3 files),
`@radix-ui/react-dropdown-menu` (1), `@radix-ui/react-slot` (1),
`@radix-ui/react-tabs` (1), `clsx` (1), `react-markdown`/`remark-gfm` (8
each), `tailwind-merge` (1), plus `next`/`react`/`react-dom`/`lucide-react`
(framework/icon deps, used pervasively).

Two `lib/api.ts` client-method groups have zero callers anywhere (not
separate files, so not independently deletable, but worth noting since they
correspond 1:1 to the dead API routes in §3.3): `castReviewVote`/
`listReviewVotes`/`getReviewRequest` (review-board voting — never got a UI)
and `listTenantMembers`/`inviteTenantMember`/`removeTenantMember` (likely
superseded by the D5 tenant-user model). **MANUAL_REVIEW**, LOW risk, tied to
the same product decision as §3.3/§2.3.

### 7.2 .NET (Gateway) — no unused package references found

All 15 `PackageReference` entries in `Aiq.Gateway.csproj` (JWT auth,
Npgsql, Dapper, BCrypt, Hangfire ×2, OpenTelemetry ×4, Npgsql.OpenTelemetry,
prometheus-net, Grpc.Net.Client, AWSSDK.S3) are foundational, load-bearing
packages with confirmed active usage traced throughout this and prior
phases' work (e.g. `AWSSDK.S3` → `Storage/S3ObjectStorage.cs`; Hangfire →
`Jobs/*.cs`). **No candidates.**

### 7.3 Python (Agent) — no unused package references found

Every dependency in `pyproject.toml`'s `dependencies` list carries an
explanatory inline comment naming its exact consumer (e.g. `qdrant-client`/
`fastembed` → `embeddings/service.py`; `asyncpg` → `PostgresMemoryStore`/OCR
validator; `openpyxl` → `import_excel_ledger` tool; `claude-agent-sdk` →
`/agent/run`), all independently confirmed used earlier in this audit's §1.2
Python-module pass. **No candidates.**

---

## 8. Database/migrations

**Migrations 001-026 were not modified, evaluated for removal, or otherwise
touched** — confirmed immutable per this task's own explicit instruction.
`gateway/src/Aiq.Gateway/Data/Migrations/` contains exactly 26 sequential
files, no gaps, no duplicates, no orphans (`ls | sort -V` produced a clean
001→026 sequence). This matches the fresh-bootstrap verification from the
ACL-V2-Final phase (26 migrations applied cleanly).

---

## 9. Runtime safety verification performed

For every candidate above, the following were checked before classification
(not filename guessing alone):

- **Import/reference search**: exact-specifier `grep` across `.cs`/`.py`/`.ts`/`.tsx` for every file/package/route candidate.
- **Route/reference search**: every `Map*Endpoints()` call in Gateway `Program.cs`; every `redirect(...)`/`<Link href=`/`fetch(...)` call site in Web; `nav-model.ts` + its own test assertions as the single source of truth for intended navigation.
- **Build/project inclusion**: `.csproj`/`package.json`/`pyproject.toml` cross-checked against actual import usage, not just declared dependency lists.
- **Docker/CI references**: `.github/workflows/ci.yml`, both compose files, all three Dockerfiles grepped for every script/file candidate.
- **Docs/scripts cross-references**: every ambiguous script/doc candidate grepped across the full repo (`*.md`/`*.sh`/`*.yml`/`*.py`) before classification, and — where still ambiguous — the file's own header comment was read directly rather than assumed from its name (this is how `pin_web_network.sh` was correctly reclassified from "looks orphaned" to "active operational hook", and how `migrate_vouchers_to_s3.sh`/`generate_bsn_handover.py`/`generate_report.py` were correctly identified as one-shot/superseded rather than randomly named).
- **Middleware/rewrite check**: `web/src/middleware.ts` confirmed to be Clerk-passthrough only (no rewrite/redirect rules), so it cannot make any page reachable that isn't already linked in-app — ruling out a false "orphaned page" conclusion being masked by a middleware rewrite.

---

## Summary

### SAFE_DELETE (LOW risk — zero importers/callers found, self-contained, or purely local generated output)

- `web/src/components/textarea-with-mic.tsx`, `web/src/components/bulk-md-upload.tsx` (2 files, 12 KB)
- 6 unused npm packages: `@radix-ui/react-dialog`, `@radix-ui/react-label`, `@radix-ui/react-scroll-area`, `@tanstack/react-query`, `class-variance-authority`, `zod` (~7.6 MB `node_modules`)
- Local generated build output (`obj/`, `bin/`, `.next/`, `node_modules/`, `__pycache__/`) — not a git-tracked concern, ~1.14 GB local disk, regenerate anytime

### MANUAL_REVIEW (real uncertainty or a live-system/product-decision dependency — do not act without a human decision)

- 32 Web `/api/**` route files superseded by direct Server Action calls (~284 KB) — confirmed unreached by the in-app UI, but public HTTP endpoints that could have an external/unknown caller
- 3 orphaned-but-fully-functional SUPER_ADMIN pages (`admin/users`, `admin/skills`, `admin/workflows`) — a genuine V2-F nav-model regression against D7's own explicit "keep visible" decision; the fix is re-adding nav entries, not deleting pages
- `lib/api.ts`'s `castReviewVote`/`listReviewVotes`/`getReviewRequest` and `listTenantMembers`/`inviteTenantMember`/`removeTenantMember` (never-wired client methods, tied to the same decision as the routes above)
- `scripts/bsn_{demo_content,finance_content,ma_advisor_setup,pilot_setup,real_content}.sql` and `scripts/seed_vietnam_knowledge.sql` — very likely still relevant to the live BSN pilot's real accounting + M&A-advisory tenants; cannot confirm current invocation cadence from the repo alone
- `docs/architecture/overview.md`, `docs/PROJECT_REVIEW_AND_ARCHITECTURE.md`, `docs/README.md` (the last one especially — its active index still points to a superseded 2026-06-era roadmap/tracker and indexes none of the ~20 current phase reports)
- `docs/voucher/PLAN.md`/`PROGRESS.md`

### ARCHIVE-only docs (real historical value; never auto-delete)

~24 files, ≈432 KB: the self-contained pre-D1 planning/reporting cluster
(`plan.md`, `plan (3).md`, `plan_agent_governance.md`, three
`progress-check*.md`, `Baocao.md`/`.docx`, `report_task_done.docx`,
`bsn_handover.docx`, `docs/Ketoan_plan.md`, `docs/ketoan_checklist.md`,
`docs/roadmap.md`, `docs/architecture_structure.md`,
`docs/implement_progress_status.md`, `docs/claude_agent_sdk_integration_plan.md`,
`docs/stt_voice_input_decision.md`, its three `generate_*.py` scripts,
`CODEX_MULTI_TENANT_MULTI_AGENT_IMPLEMENTATION.md`,
`use-case-spec-xu-ly-chung-tu-ke-toan.md`, `docs/vietdang_demo_test_cases.md`,
`docs/demo-cheatsheet-anh-toan.md`, `docs/plan-agent-tro-ly-ke-toan.md`) —
plus `agent/tests/phase3c_live_e2e.py` (8 KB), `scripts/migrate_vouchers_to_s3.sh`,
`scripts/generate_bsn_handover.py`, `scripts/generate_report.py` (~28 KB).

### Blockers/uncertainties

- The 32 dead-looking Web API routes cannot be fully cleared without access to actual production access logs (out of this repo-only audit's reach) — the D8 audit's own precedent ("never touch the live BSN pilot host") applies equally here: don't remove a live-traffic-serving route on guesswork.
- The `bsn_*.sql`/`seed_vietnam_knowledge.sql` operational scripts' *current* invocation cadence cannot be verified from the repo alone; only their historical purpose could be confirmed.
- No estimate was made of git-history/`.git` object size reduction from archiving docs, since git history itself was explicitly out of scope (nothing was moved or deleted this pass).

### Estimated cleanup if every SAFE_DELETE + ARCHIVE item above were actioned

- **File count**: ~2 dead components + 6 npm packages + ~24 historical docs + 4 misc scripts/tests ≈ **36 files** moved/removed from the active tree (repository-tracked; excludes the ~1.14 GB of already-gitignored local build output, which is separate and unlimited-safe to delete anytime).
- **Approximate disk size**: ~460 KB of tracked docs/scripts + ~7.6 MB of `node_modules` (after `npm uninstall`) + 12 KB of dead components ≈ **~8 MB** of tracked-repo-relevant reduction, plus the ~1.14 GB of local-only generated output that isn't a repository concern at all.
- **Dependency reduction**: 6 fewer npm packages (and their own transitive sub-dependencies, not separately sized here).

---

**REPOSITORY CLEANUP AUDIT COMPLETE.**
