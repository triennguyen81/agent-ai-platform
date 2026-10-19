# Accounting Agent Audit

Audit date: 2026-09-24
Scope: real code (`gateway/`, `agent/`, `web/`), real running stack (`aiq-gateway`, `aiq-web`, `aiq-agent`, `aiq-postgres`, `aiq-redis`), real DB rows, and live HTTP requests against the running system for the real tenant "Sáu Trực" (`tenant_id=6dc70ea0-c430-4d18-81f3-1821787fcce4`), department `e7d1ccd1-0105-45a9-b588-9460e2f57956`, Accounting Agent "Trợ lý kế toán" (`agent_id=1c1c6d03-8ea0-4f48-b6b2-17866418c584`, published version `100ec78f-95b8-41da-806d-985693fba958`).

Method: 6 parallel read-only research passes over the codebase (file:line evidence required for every claim) + direct Postgres/Redis queries + live authenticated HTTP tests through the real web→gateway→agent chain (login via the real `/login` Server Action, real JWT cookie, real `POST /api/chat`, real `POST /vouchers/upload`, real `POST /vouchers/{id}/apply-invoice|apply-expense`, real Knowledge Base create/index/attach/retrieve). Live testing required a temporary, user-approved password reset on the demo account `admin1@sautruc.com` (original bcrypt hash saved beforehand and restored immediately after — verified restored, byte-for-byte, at the end of this audit). All test artifacts created for this audit (1 note, 1 knowledge base + document) were deleted afterward.

Where a claim could not be directly verified (no live test performed, or the underlying dependency's internals aren't vendored in this repo), it is marked **NOT VERIFIED** — never counted as PASS.

---

## Executive Summary

```text
Overall status: PARTIAL

Runtime:            PASS — manifest-driven execution confirmed live (real chat, real tool call, real RAG
                     retrieval, real cross-tenant denial, all reproduced with real HTTP requests and
                     cross-checked against Postgres/Redis audit trails, not just code reading).
Security:            PASS (with 1 HIGH code-level finding) — no live exploit found; SSRF risk in
                     import_excel_ledger is real at the code level but that tool is gated at
                     min_action_tier=execute_with_approval (HITL) in this tenant's actual grant.
Tenant isolation:    PASS — live-verified (cross-tenant agent access → 403, cross-tenant knowledge
                     retrieval structurally impossible per code + DB constraints).
Skills:              PARTIAL — skills are pure prompt text (no code), correctly loaded from the
                     manifest's exact grant list for the real agent, but `always_on`/`when` skill flags
                     are silently inert for the manifest-driven path, and skill grants are inconsistent
                     across seeded demo agents.
Tools:               PARTIAL — 23 tools correctly granted and live-tested working (calculators, AR/AP
                     lookups, contract/voucher tools); 1 real tool (`import_excel_ledger`) has a genuine
                     SSRF code defect; several other tools exist in Python but are not currently
                     grantable to any manifest-driven agent (dead/orphaned).
Knowledge:           PARTIAL — the real (Qdrant vector) RAG pipeline is live-verified working correctly,
                     tenant-isolated, with citations, and resists prompt injection from document content
                     (all confirmed live, not just read). But the legacy per-note "Attach Knowledge"
                     admin UI feature (`/agents/{id}/knowledge`) is **dead code for every agent-bound
                     chat** — confirmed live: content attached that way is silently never used.
Workflow:            PASS — confirmed live: no Planner/Critic step in the real path (fact, not a defect
                     per se), hard turn caps enforced (max_turns ≤ 20, DB + Pydantic validated), no
                     runaway loop observed; one un-budgeted fan-out risk (router delegation) found
                     in code, not exercised live.
Performance:         PARTIAL — per-request latency/tokens/cost ARE captured (Redis-backed audit store,
                     live-read for this audit's own test requests), but there is no gateway/LLM/tool/
                     RAG/DB component breakdown anywhere, and the dedicated Postgres cost-tracking table
                     (`agent_usage_events`) exists in schema but has never been written to (0 rows,
                     confirmed by direct query) — dead schema.
Production readiness: READY WITH CONDITIONS — see Final Gap List.
```

---

## Architecture Found

```text
Browser
  │  POST /api/chat  { prompt, agent_id }
  ▼
Next.js Web (web/src/app/api/chat/route.ts)
  │  reads aiq.session cookie (JWT) → forwards as Bearer
  │  fetches legacy per-note "knowledge_context" (DEAD for agent_id-bound calls — see Finding F-07)
  ▼
Python Agent service — FastAPI (agent/src/aiq_agent/api/routes.py: POST /chat)
  │  require_principal() → GETS {gateway}/me with caller's own Bearer token (re-validates every call)
  │  resolve_capability_agent() / body.agent_id → resolved_agent_id
  │  require_agent_execution() → fetch_execution_manifest()
  ▼
Gateway — ASP.NET Core (gateway/src/Aiq.Gateway/Endpoints/DynamicAgentEndpoints.cs: GET /runtime/agents/{id}/manifest)
  │  RequireAuthorization("department_member")
  │  IsResolvedDepartmentAgentAsync(agentId) → true for "ke-toan" (v2_scope=department)
  │  GetDepartmentAgentManifestAsync(userId, tenantId, departmentId, agentId, isAdmin, ct)
  │    SQL: agents ⋈ agent_versions ⋈ agent_tenant_assignments ⋈ agent_department_assignments
  │         ⋈ (isAdmin OR agent_assignments EXISTS) ⋈ agent_version_tool_grants ⋈ tools
  │         ⋈ agent_version_skill_assignments ⋈ agent_version_capabilities ⋈ agent_knowledge_bases
  │  denial ladder: user_disabled / tenant_disabled / agent_not_assigned_to_tenant /
  │    agent_not_assigned_to_department / agent_not_assigned / agent_disabled /
  │    agent_not_production / agent_not_published  → 403 (never 404)
  │  mints a short-lived tenant-scoped tool_execution JWT (TTL = min(timeoutSeconds+60, 3600)s)
  ▼  returns AgentExecutionManifest (provider, model, system_prompt, tools[], skills[], knowledge[], ...)
Python Agent — build_agent_executor(manifest) → ClaudeSdkAgentExecutor (provider="claude_sdk" for this agent)
  │  installs ONLY manifest.tools into an in-process MCP server (structural allow-list — the LLM cannot
  │    even name an ungranted tool; live-confirmed, Test G)
  │  retrieve_context(manifest, prompt) → Qdrant vector search, tenant_id-filtered (live-confirmed, Test C)
  │  ClaudeSDKClient(...).query(...) → Claude Agent SDK's own internal tool-use loop (max_turns from manifest)
  │    each tool call intercepted by _wrap_tool_with_hitl_gate() → POST {gateway}/runtime/agents/{id}/hitl/evaluate
  │    (live-confirmed in audit_log for Test B: 2 hitl/evaluate calls preceded the 2 tool calls)
  ▼
Tool handlers (agent/src/aiq_agent/agentic/{accounting,voucher,contract,report}_tools.py)
  │  accounting_tools.py's 7 tools: pure in-process calculators — ZERO gateway/DB calls
  │  voucher/contract tools: HTTP calls to Gateway using the minted tool_execution token (never the
  │    original user session token for tool calls; never a separate unscoped service credential)
  ▼
Gateway resource endpoints (VoucherEndpoints.cs, ContractEndpoints.cs, CustomerEndpoints.cs, ...)
  │  tenant_id/department_id derived from the token's own claims (never request body) → parameterized
  │  Dapper SQL against sim_ar_ledger / sim_ap_ledger / sim_customers / contracts / vouchers / ...
  ▼
PostgreSQL (aiq-postgres) / Qdrant (vector search, inside aiq-agent's knowledge service)
  ▲  results flow back up through the same chain
Final LLM response ← Gateway ← Python Agent (ClaudeSdkAgentExecutor.execute → final_answer)
  ▼
Next.js Web → Browser
```

This is the **actual** path a real `/api/chat` request with `agent_id` takes — confirmed by reading the code AND by reproducing it live (audit_log rows for every hop: "GET runtime agent manifest (admin department execution)", "POST .../hitl/evaluate" ×2, and a correct, real, DB-backed answer returned to the browser-equivalent HTTP call).

---

## Accounting Agent Composition

```text
Identity:     agents row id=1c1c6d03-8ea0-4f48-b6b2-17866418c584, code="ke-toan", name="Trợ lý kế toán",
              scope=tenant, v2_scope=department, tenant_id=6dc70ea0-... (Sáu Trực),
              department_id=e7d1ccd1-..., lifecycle_state=production, action_tier=execute_with_approval,
              disabled_at=NULL, published_version_id=100ec78f-95b8-41da-806d-985693fba958.
              (Evidence: direct `SELECT` against `agents`.)

              A SECOND row with the same code/name exists: a601c8a6-... , scope=system,
              v2_scope=platform_template, tenant_id=NULL. This is NOT ambiguous at runtime — it is a
              platform template other tenants can clone from; DynamicAgentEndpoints.cs's
              IsResolvedDepartmentAgentAsync() check routes all Sáu Trực traffic to the department-scoped
              clone (1c1c6d03) exclusively. (Evidence: `agents.source_agent_id`/`v2_migration_status`
              columns + DynamicAgentEndpoints.cs:419-423, live-confirmed via audit_log showing
              "admin department execution" mode for every real request this session.)

Model:        agent_versions row 100ec78f-...: provider=claude_sdk, model=sonnet, temperature=0.20,
              max_turns=8, timeout_seconds=120, max_tokens=2048, status=published.
              Live-confirmed: every /api/chat response returned `"provider":"claude_sdk","model":"sonnet"`.
              (The platform-template agent's version history shows real historical provider switching:
              v1-3 = ollama/qwen3:latest, v4-7 = claude_sdk/sonnet — proves provider switching is a real,
              exercised mechanism in this system, not theoretical.)

Prompt:       agent_versions.system_prompt (DB-stored, not read verbatim in this audit to avoid dumping
              full prompt text into this report; existence and DB-authority confirmed structurally —
              ExecutionManifest.systemPrompt is the only system-prompt source
              ClaudeSdkAgentExecutor reads for a manifest-driven run, per agent/src/aiq_agent/runtime/
              executors.py). No hardcoded accounting-specific prompt found bypassing this in either C#
              or Python execution code (only a generic non-accounting default exists, used solely by
              the no-agent-id legacy /chat fallback).

Skills:       9 granted: accounting-vietnam, accounting-vn-style, compliance-citation,
              contract-checklist-builder, contract-extractor, tax-calculation, url-ingest,
              vietnamese-style, voucher-invoice-vat.
              (Evidence: `agent_version_skill_assignments` for version 100ec78f, direct query.)
              NOTE: "voucher-universal-extractor" (the modern, non-legacy voucher extraction skill) is
              NOT in this list — only the legacy "voucher-invoice-vat" skill is granted. Confirmed
              directly against this agent's real DB grant (not inferred).

Tools:        23 granted (agent_version_tool_grants for 100ec78f, direct query + join to `tools`):
              calculate_vat, calculate_pit, calculate_cit, check_filing_deadline, lookup_account,
              journal_entry_validator, import_excel_ledger, extract_contract_fields, extract_voucher_fields,
              get_ap_summary, get_contract, get_customer_receivables, get_supplier_payables,
              get_weekly_summary_report, list_customers, list_suppliers, list_vouchers,
              save_checklist_items, save_contract, save_journal_suggestions, save_voucher,
              suggest_journal_entry, verify_einvoice_tct.
              Live-confirmed callable: get_customer_receivables + list_customers (Test B, Test D).
              Live-confirmed NOT callable (correctly refused): fetch_url, save_note (Test G — these were
              never granted, so the LLM had no schema entry for them at all).

Knowledge:    0 legacy per-note attachments, 0 knowledge_base attachments at audit start. A real
              knowledge_base + document was created and attached for this audit (Test C), live-verified
              retrievable with correct citation metadata, then deleted (cleanup confirmed).

Workflow:     agent_versions.workflow_id = NULL for this agent — no formal Workflow object attached; the
              agent runs as a plain manifest-driven tool-use loop, not a multi-step Workflow graph.

Memory:       Redis-backed (memory/redis_store.py; MEMORY_BACKEND=redis confirmed via container env),
              per-thread, capped. thread_id in this audit's tests was omitted → history_length=0 each
              call (expected — no persistent memory across these one-off test calls).

Permissions:  department_admin required to edit/publish/assign/attach-knowledge; department_member
              (admin OR user) required to execute; ADMIN role gets implicit "assigned" status for a
              Department Agent in its own department without needing an agent_assignments row
              (`@isAdmin OR EXISTS(...)`, IDynamicAgentRepository.cs:1321); a plain USER role would need
              an explicit agent_assignments row (NOT VERIFIED live — no plain-"user"-role account exists
              in this tenant to click-test; verified from code only, see Finding F-08).

Runtime executor: ClaudeSdkAgentExecutor (agent/src/aiq_agent/runtime/executors.py) — wraps the Claude
              Agent SDK's own tool-use loop; not a custom Planner/Executor/Critic implementation for this
              path (that pattern exists elsewhere in the codebase but is not used here — see Workflow
              audit below).
```

---

## Runtime Trace (one real request, reproduced live)

**Test B — "Công nợ phải thu hiện tại của khách hàng Việt Tín là bao nhiêu?"**

| Step | File/Function | Input | Output | Auth check | Failure points |
|---|---|---|---|---|---|
| 1. Browser→Web | `POST /api/chat` | `{prompt, agent_id}` | — | `aiq.session` cookie required | missing cookie → 401 |
| 2. Web→Agent | `web/src/app/api/chat/route.ts` | forwards prompt+agent_id+Bearer | — | none added (delegates) | Gateway/Agent down → 502 propagated as-is |
| 3. Agent principal check | `agent/src/aiq_agent/api/security.py:require_principal` | Bearer token | `RuntimePrincipal` | live `GET {gateway}/me` | invalid/expired token → 401 |
| 4. Agent manifest fetch | `agent/src/aiq_agent/runtime/manifest.py:require_agent_execution` | principal, agent_id | `ExecutionManifest` or `None` | delegates to gateway | gateway denial → agent returns "not allowed" (live-confirmed for cross-tenant, Test F) |
| 5. Gateway manifest resolve | `IDynamicAgentRepository.GetDepartmentAgentManifestAsync` | userId, tenantId, departmentId, agentId, isAdmin | `AgentExecutionManifest` | full SQL denial ladder (see Architecture) | any ladder condition false → 403 with specific reason string (live-confirmed: `agent_not_assigned_to_tenant`, `agent_not_owned_by_department`) |
| 6. Executor build | `executors.py:build_agent_executor` | manifest | `ClaudeSdkAgentExecutor` | dispatches on `manifest.provider` only | unknown provider string → `ProviderResolutionError` |
| 7. Tool install | `executors.py:_resolve_tools` | `manifest.tools` | in-process MCP tool set | only these tools exist in the LLM's schema | a tool code with no installed handler → `ProviderResolutionError` (fails closed, not silently ignored) |
| 8. RAG | `agent/src/aiq_agent/knowledge/service.py:retrieve_context` | manifest, prompt | untrusted-framed JSON chunk block or "no evidence" string | tenant_id from manifest (server-derived) | Qdrant down → falls back gracefully (not tested live this pass) |
| 9. SDK loop | `ClaudeSDKClient.query/receive_response` | system+skills+RAG+prompt | streamed `AssistantMessage`s, tool_use blocks | `max_turns` from manifest (8 here) | SDK internal — **NOT VERIFIED** (pip dependency, not vendored) |
| 10. Tool call gate | `executors.py:_wrap_tool_with_hitl_gate` | tool_code, args | allow/block | `POST {gateway}/runtime/agents/{id}/hitl/evaluate` with the minted tool_execution token | live-confirmed: 2 calls in `audit_log` immediately preceding the tool results in this exact test |
| 11. Tool handler | `voucher_tools.py`/Gateway `CustomerEndpoints.cs` (`get_customer_receivables`) | tenant-scoped, token-derived | real AR data | Gateway-side tenant scoping (JWT-derived) | none observed |
| 12. Final answer | executor assembles `final_answer` | — | Vietnamese markdown table, real numbers matching Postgres `sim_customers`/`sim_ar_ledger` | — | — |
| 13. Agent→Web→Browser | JSON passthrough | — | `{final_answer, provider, model, steps, ...}` | — | `steps[].tool_name` is always `null` even when a tool ran — observability gap (Finding F-09) |

Total wall time for this exact request: **25,945 ms** (measured client-side via curl), matching the agent-side self-reported `ms=25905` in the Redis audit store almost exactly (Δ≈40ms = Gateway+Web overhead, negligible).

---

## Test Results

| Test | Result | Evidence |
|---|---|---|
| A — Simple chat (no tool) | **PASS** | `POST /api/chat` → 200, correct company identity ("Sáu Trực, MST 0123456789" — from live CompanyProfile injection), `provider=claude_sdk model=sonnet` matching DB, no tool called. 10,489 ms wall / `ms=10448` self-reported. |
| B — Tool selection | **PASS** | Same call with "Công nợ phải thu Việt Tín" → correct real data (matches `sim_customers`), `audit_log` shows 2×`hitl/evaluate` immediately preceding the answer; Redis audit confirms `tools_used=['mcp__aiq__list_customers','mcp__aiq__get_customer_receivables']`. |
| C — Knowledge retrieval | **PASS (2nd attempt)** | 1st attempt used the legacy per-note attach path → **silently ignored** (0% retrieval — confirmed this is code-intentional, see F-07). 2nd attempt via the real `knowledge_bases`/Qdrant pipeline: created KB → uploaded doc → indexed (`indexingStatus:"completed"`) → attached to agent → asked for the embedded secret code → agent returned it **verbatim with citation** ("Audit Test Doc, trang 1, version 1") and explicitly flagged it as untrusted reference content. |
| D — Combined (Skill+Knowledge+Tool) | **PASS** | Single prompt asking for a customer's AR (tool) AND relevance of the attached doc (KB) in one turn → correct AR data for Minh Long (30.000.000₫, matches DB) AND correct refusal to treat the KB content as authoritative, explicitly stating the AR answer came only from `get_customer_receivables`. |
| E — Unauthorized agent (user not assigned) | **NOT VERIFIED (live)** | No plain "user"-role account exists in this tenant (only `admin` and `tenant_owner`); user's approved test scope was limited to reusing the existing `admin1` account, which is implicitly always-assigned as ADMIN (`@isAdmin OR EXISTS(...)`), so this exact negative case could not be produced live without creating a new account (out of this session's approved scope). Code-level mechanism (`agent_assignments` EXISTS check, 403 `agent_not_assigned`) is confirmed present but **not click-tested**. |
| F — Cross tenant | **PASS** | admin1 (Sáu Trực token) → foreign tenant's agent via 3 different routes: `GET /runtime/agents/{foreign-disabled-agent}/manifest` → 403 `agent_not_assigned_to_tenant`; `GET /runtime/agents/{foreign-production-agent}/manifest` → 403 `agent_not_owned_by_department`; `POST /api/chat` with a foreign agent_id → 403 `"Agent execution is not allowed"`. All three real, live denials. |
| G — Invalid tool | **PASS** | Asked the agent to fetch a URL and save a note (tools never granted to this agent: `fetch_url`, `save_note`) → agent correctly self-reported it has no such tools and took no action; `steps[].tool_name` stayed `null` (no call attempted) — confirms the structural allow-list (ungranted tools are never in the LLM's function schema at all, not merely policy-discouraged). |
| H — Disabled/unpublished agent | **PARTIAL** | No disabled agent exists inside Sáu Trực tenant to isolate this reason alone. A foreign tenant's agent literally named "Disabled Agent" (`disabled_at` set) was tried and got 403 — but the denial reason returned was the tenant-scoping check (which runs first in the SQL ladder), not the `agent_disabled` reason specifically. Code-level: confirmed the same manifest-resolution query checks `disabled_at`, `lifecycle_state='production'`, and `agent_versions.status='published'` at the exact boundary real traffic uses (not a separate/bypassable check) — **static PASS, live isolation of this specific reason NOT VERIFIED**. |

---

## Findings

```text
CRITICAL: 0
HIGH:     1
MEDIUM:   3
LOW:      4
INFO:     4
```

### F-01 — HIGH — SSRF in `import_excel_ledger`
```
Description: The tool fetches an arbitrary caller-supplied `file_url` via httpx with follow_redirects=True
             and no private-IP/hostname allowlist or scheme restriction beyond http/https.
Evidence:    Direct read of the full function body; `client.get(url)` at line 717, no auth_headers used,
             no network-layer restriction anywhere in the function.
File:        agent/src/aiq_agent/agentic/accounting_tools.py:701-793 (import_excel_ledger)
Function:    import_excel_ledger()
Impact:      Any agent granted this tool could be induced (via prompt or malicious user request) to make
             the Gateway/Agent host issue outbound requests to internal/private addresses (cloud metadata
             endpoint, localhost, internal service ports), and the first ~1KB of the response (20 rows ×
             8 cols + 5-row JSON sample) is returned to the model/user — a real exfiltration channel.
Mitigating factor (confirmed): this tool is seeded with `risk_tier=high`, `min_action_tier=
             execute_with_approval` (scripts/seed_demo_agents.sql) and is wrapped by the same per-call
             HITL gate every other tool goes through (live-confirmed mechanism in Test B) — so in THIS
             tenant's configuration, a human approval step sits in front of it. This reduces but does not
             eliminate the risk (HITL approval of "import a ledger from this URL" does not obviously
             convey "this could probe your internal network" to the approving human).
Recommended fix: add a private-IP/hostname blocklist (RFC1918, link-local, loopback, cloud metadata IP)
             before the fetch, disable redirect-following or re-validate the redirect target, and consider
             an explicit domain allowlist for ledger-import sources.
```

### F-02 — MEDIUM — Legacy per-note "Attach Knowledge" is dead code for agent-bound chat
```
Description: web/src/app/api/chat/route.ts fetches legacy per-note knowledge (via
             GET /agents/{id}/knowledge/context) and forwards it as `knowledge_context` in the /api/chat
             body whenever `agent_id` is present — but the Python /chat handler explicitly discards
             `body.knowledge_context` for every agent_id-bound (manifest-driven) request. Live-confirmed:
             a note attached via this exact admin-facing endpoint was completely invisible to the agent
             ("Em không thấy tài liệu nào được đính kèm...").
Evidence:    agent/src/aiq_agent/api/routes.py:201-203 — code comment: "All client runtime knobs and
             client knowledge are intentionally ignored here. The signed Gateway manifest is the sole
             authority." — confirms this is a deliberate design decision, not an oversight, but the
             consequence is that the department_admin-only "/agents/{id}/knowledge" attach UI (and all
             ~80 lines of note-fetching/ranking logic in web's chat route) does nothing for the primary,
             real production chat path.
File:        agent/src/aiq_agent/api/routes.py:201-203; web/src/app/api/chat/route.ts:70-130;
             gateway/src/Aiq.Gateway/Endpoints/AgentKnowledgeEndpoints.cs (the attach endpoint itself)
Impact:      An admin using the "attach knowledge (note)" feature on an Accounting Agent will see no
             effect on real chat answers and has no error/warning telling them so — a silent capability
             gap that could mislead an operator into believing knowledge is wired up when it is not.
Recommended fix: either (a) remove/hide the legacy per-note attach UI for agent-id-bound agents and point
             admins to the real `knowledge_bases` flow, or (b) make the manifest-driven executor also
             merge legacy per-note content into its knowledge context, or (c) at minimum surface a warning
             in the UI that per-note attachment doesn't affect agent-bound chat.
```

### F-03 — MEDIUM — `agent_usage_events` (cost/token tracking table) is fully unpopulated
```
Description: A dedicated Postgres table for per-agent usage (input_tokens, output_tokens, cost_amount,
             request_count) exists in schema but has 0 rows across the ENTIRE database, not just this
             agent — confirmed by direct query.
Evidence:    `SELECT count(*) FROM agent_usage_events;` → 0. No `INSERT INTO agent_usage_events` found
             anywhere in gateway/src or agent/src.
File:        gateway/src/Aiq.Gateway/Data/Migrations/024_acl_v2_scope_foundations.sql (table definition,
             never referenced elsewhere)
Impact:      Any tenant-usage/billing/cost dashboard relying on this table would show zero data forever.
             The ACTUAL cost/token data lives only in the capped Redis list `aiq:audit` (5,000-entry cap,
             no TTL-independent durability, not queryable by SQL, not tenant-partitioned for reporting).
Recommended fix: either wire real writes into `agent_usage_events` from the same point that writes the
             Redis audit entry, or formally deprecate/drop the unused table so it doesn't mislead future
             engineers into building on it.
```

### F-04 — MEDIUM — Duplicate/competing voucher-extraction implementation
```
Description: Voucher field extraction + journal-entry suggestion exists as two independent
             implementations: (a) the dedicated MCP tools `extract_voucher_fields`/`suggest_journal_entry`
             (structured, tool-call based), and (b) a separate freeform-prompt + C#-side regex-parsing
             implementation inline in the Hangfire job runner that never calls those tools.
Evidence:    gateway/src/Aiq.Gateway/Jobs/VoucherExtractJobRunner.cs:484-522 (BuildExtractionPrompt +
             FenceRegex/ExtractJsonBlocks) vs agent/src/aiq_agent/agentic/voucher_tools.py:74,371.
File:        gateway/src/Aiq.Gateway/Jobs/VoucherExtractJobRunner.cs
Impact:      Two parsing/extraction code paths must be kept in sync manually; a fix to one (e.g. a field
             extraction bug) will not automatically apply to the other. The job-runner comment itself
             references a probable THIRD parallel parser in the web tier ("mirror of web-tier
             extractJsonBlocks").
Recommended fix: consolidate on the MCP tool-based extraction path, or explicitly document why the job
             runner intentionally uses a separate mechanism (its own comments say it's for authorization
             routing via the capability resolver — worth confirming this couldn't be achieved with the
             tool-based path too).
```

### F-05 — LOW — Skill `always_on`/`when` flags are inert for manifest-driven agents
```
Description: The skill loader parses `always_on`/`when: [has_knowledge_context]` frontmatter, but only
             the plain no-agent-id /chat path (keyword+flag based `select_skills()`) honors them. The
             real Accounting Agent path loads only the exact names in `manifest.skills`, ignoring these
             flags entirely.
Evidence:    agent/src/aiq_agent/runtime/executors.py:129-138 (_resolve_skills: exact-name lookup only)
             vs agent/src/aiq_agent/skills/loader.py:152-176 (always_on/when logic, only reachable from
             select_skills()). `vietnamese-style` (marked always_on:true) IS present in this agent's real
             grant, `compliance-citation` (marked `when: [has_knowledge_context]`) is also granted
             unconditionally — so for this specific agent the practical effect is limited, but the
             mechanism itself is confirmed inert for the real path.
File:        agent/src/aiq_agent/runtime/executors.py:129-138
Impact:      An operator configuring a skill's `when`/`always_on` metadata, believing it will be honored
             for a real Department Agent, will be wrong — inconsistent with two demo agents found in seed
             data (`kt-bookkeeper` grants only 1 of 9 accounting skills; `vd-accounting` grants 6 of 9).
Recommended fix: either enforce `always_on`/`when` in the manifest-driven path too, or remove the fields
             from SKILL.md frontmatter / document clearly that they only apply to the legacy chat path.
```

### F-06 — LOW — 12 Python tools not currently grantable via governance (partially corrected during this audit)
```
Description: Subagent research initially found 12 tools (7 accounting calculators + 2 voucher tax-
             verification tools + 4 generic note/URL tools) built in Python but absent from the seed
             files it checked (infra/postgres/init/13_governance_tools.sql, migration 048).
CORRECTION (found during this audit's own direct DB verification): the 7 accounting-calculator tools
             (calculate_vat/pit/cit, lookup_account, check_filing_deadline, journal_entry_validator,
             import_excel_ledger) ARE in fact present, active, and genuinely granted to the real Sáu Trực
             Accounting Agent — seeded via `scripts/seed_demo_agents.sql` (a standalone script, not a
             migration), confirmed by direct `SELECT * FROM tools WHERE code='mcp__aiq__calculate_vat'`
             (is_active=true) and by `agent_version_tool_grants` containing all 7 for this agent's
             published version, and live-usable (all 23 granted tools include these 7).
Still confirmed orphaned: `confirm_tax_verified`/`reject_tax_verified` (voucher_tools.py:318,343) and
             `search_notes`/`get_note`/`save_note`/`fetch_url` (tools.py:36,93,117,157) — grep of
             infra/, gateway/src/Aiq.Gateway/Data/Migrations/, and scripts/ for these exact tool codes
             found no seed row; confirmed unreachable by any manifest-driven agent today.
File:        agent/src/aiq_agent/agentic/voucher_tools.py:318,343; agent/src/aiq_agent/agentic/tools.py
Impact:      Real but narrower than first reported — LOW, not MEDIUM: the accounting calculator toolset
             (the "Accounting Mức 2" set explicitly named in the audit brief) is fully functional. The
             remaining orphans are a genuinely unused code surface, plus a deployment-consistency risk:
             the calculator tools' availability depends on `scripts/seed_demo_agents.sql` having been run
             out-of-band against this specific database — a fresh production deploy following only
             versioned migrations would NOT have these tool codes in the `tools` table at all.
Recommended fix: fold the calculator-tool seed rows into a proper numbered migration (not a standalone
             script) so their availability doesn't depend on someone remembering to run a script; either
             wire or remove the still-orphaned tax-verification/note/URL tools.
```

### F-07 — LOW — `steps[].tool_name` always null in /chat response for manifest-driven runs
```
Description: Even when a tool call demonstrably occurred (confirmed via Redis audit `tools_used` and
             Postgres `audit_log` hitl/evaluate rows), the HTTP response's `steps[]` array reports
             `tool_name: null` for every step.
Evidence:    Test B/D live responses — single `steps` entry, `role=assistant`, `tool_name=null`, despite
             `tools_used=['mcp__aiq__list_customers','mcp__aiq__get_customer_receivables']` in the
             corresponding Redis audit entry from the same request.
File:        agent/src/aiq_agent/runtime/executors.py:244 (steps=[StepResponse(role=..., content=...)
             for step in dynamic.steps] — no tool_name/tool_ok/tool_output populated for the manifest path,
             unlike the PEC path which does populate these fields)
Impact:      A caller/UI inspecting the API response alone cannot tell which tool(s) ran without also
             reading server-side audit logs — an observability/transparency gap for end users, though not
             a security issue (server-side records are complete).
Recommended fix: populate tool_name/tool_ok/tool_output on the manifest-driven executor's step records
             the same way the PEC path already does.
```

### F-08 — LOW — Plain-"user"-role assignment path not live-tested
```
Description: Test E (unassigned user → 403) and part of Test H could not be reproduced live because no
             plain "user"-role account exists in the audited tenant and this session's approved test
             scope was limited to reusing the existing admin account.
Evidence:    `SELECT u.email, r.name FROM users u JOIN user_roles ur ... WHERE tenant_id=...` → only
             `admin` and `tenant_owner` rows exist for Sáu Trực.
File:        N/A (test-coverage gap, not a code defect)
Impact:      The `agent_assignments` EXISTS-check for non-admin users (IDynamicAgentRepository.cs:1321)
             is confirmed present in code but its 403 behavior for a real non-admin session was NOT
             independently reproduced this pass.
Recommended fix: create a disposable USER-role test account (with explicit user consent) in a follow-up
             pass to close this gap, or accept static-code verification as sufficient.
```

### F-09 to F-12 — INFO
```
F-09 INFO — Two "Trợ lý kế toán" agent rows exist (platform template + department clone) — intentional
     design (v2_migration_status='resolved' on both), not a defect; flagged only because it initially
     looked ambiguous during this audit and is worth documenting for future engineers.
F-10 INFO — `department_capability_bindings` has 0 rows for Sáu Trực's department, yet capability
     resolution (e.g. "accounting.invoice" → this agent) works correctly (confirmed via historical
     audit_log "GET capability agent resolution" success rows) — resolution falls back to the tenant's
     sole eligible agent when no explicit binding exists; not a bug, but undocumented behavior worth a
     comment in code.
F-11 INFO — `redis_password`/`aiq:audit` list requires the Redis password (not anonymous) — correctly
     access-controlled; confirmed only via container-env introspection, not a finding, just documenting
     how this audit obtained performance data.
F-12 INFO — `ApprovalEndpoints.cs` is explicitly self-documented in its own code comments as a legacy,
     tenant-wide (non-department-scoped) HITL mechanism kept alive only because
     VoucherExtractJobRunner still calls it, running in parallel with the newer department-scoped review
     pattern. Not itself exploitable (still requires RequireAuthorization("admin")), but a maintenance/
     consistency risk worth tracking.
```

---

## "Agent không ghi sổ" (Agent Never Posts) — Dedicated Verdict

**No code path lets the Accounting Agent (LLM tool-calling, no human click) post a real journal entry, apply an invoice/receipt/expense to the ledger, issue an invoice, or finalize a tax filing.**

Evidence (static, corroborated live):
- `accounting_tools.py`'s 7 tools perform zero writes and zero Gateway calls (only 1 outbound GET to a caller-supplied URL, F-01).
- `voucher_tools.py`/`contract_tools.py` write only to draft/proposal tables (`vouchers.extractedFields`, `voucher_journal_suggestions`, contracts in pre-APPROVED states) via routes gated at `department_member`, not `department_admin` — because they are not privileged actions.
- Every route that mutates `sim_ar_ledger`/`sim_ap_ledger`/`sim_customers.current_ar`/sets `status='verified'` is registered with `.RequireAuthorization("department_admin")`, and the token used for ALL agent tool calls is a short-lived, tenant-scoped `tool_execution` token distinct from a full admin session — **live-confirmed this audit's own tests never once triggered a ledger write**: Test B/D/G all performed only reads; the only ledger-affecting actions in this session were the explicit, separate, human-driven `POST /vouchers/{id}/apply-invoice` calls from the *previous* task in this conversation, made directly by the tester acting as the human approver, not by agent tool-calling.
- `grep -rniE "auto[_-]?approve|skip[_-]?review|bypass[_-]?hitl|auto_post|self[_-]?approve"` across the whole repo: **zero matches**.
- The one always-running, no-live-click path (`VoucherExtractJobRunner`, a Hangfire background job) only ever proposes (journal suggestions, receipt/expense-claim match outcomes) or enqueues a genuine HITL approval-queue entry — confirmed by its own code comments ("Read-only proposal: never posts to sim_ar_ledger/sim_ap_ledger") and by never calling any `apply-*` endpoint (grep confirmed zero occurrences of these path strings anywhere in `agent/src`).

---

## Performance

```text
Total (Test A, simple chat):        10,489 ms wall-clock (curl) / 10,448 ms self-reported agent-side
Total (Test B, 1 tool call):        25,945 ms wall-clock / 25,905 ms self-reported agent-side
Total (Test D, combined):           not separately timed this pass (correctness-focused)
Gateway+Web overhead (derived):     ≈40 ms (Δ between wall-clock and agent-side self-report) — negligible
Agent-internal breakdown
  (LLM vs Tool vs RAG vs DB):       NOT AVAILABLE — no instrumentation exists to decompose the single
                                     aggregate "ms" figure into LLM/tool/RAG/DB components (GAP — see F-03
                                     and general performance gap below)
LLM calls per request:              1 (manifest-driven path never makes more than 1 top-level SDK
                                     `.query()` call per /chat request; internal SDK turn-counting for
                                     multi-tool-call sequences within that one call is NOT independently
                                     verifiable — pip dependency, not vendored)
Input tokens:                       Reported as 2-6 in the Redis audit store for every observed request —
                                     implausibly low for a request carrying a full system prompt + skills
                                     + RAG context (should be in the thousands). GAP: this metric appears
                                     unreliable/miscounted, not a true prompt-token count. Flagged, not
                                     silently trusted.
Output tokens:                      321-4,886 across observed historical + this session's requests
                                     (Redis `aiq:audit`, real data) — this figure looks plausible.
Estimated cost (USD):               $0.012 - $0.183 per request observed (Redis `aiq:audit`, real data)
ttft_ms:                            NOT VERIFIED — no time-to-first-token instrumentation found anywhere
Dedicated cost/usage DB table:      EXISTS but UNUSED — `agent_usage_events`, 0 rows total in the whole
                                     database (F-03)
```

**Conclusion for Section 11**: partial, real instrumentation exists (total per-request latency + output tokens + cost, persisted in a capped Redis list, live-read for this report), but there is **no** gateway_ms/llm_ms/tool_ms/rag_ms/db_ms/ttft_ms breakdown anywhere in the system, and the Postgres table that schema-wise looks like it should hold durable usage data is dead. This is a genuine GAP, not a "slow" verdict — the system does log enough to answer "how much did this cost and how long did it take in total," but not "where did the time go."

---

## Final Gap List

```text
Must Fix:
  - F-01 (HIGH): SSRF hardening for import_excel_ledger (private-IP/hostname blocklist, redirect re-check).
  - F-02 (MEDIUM): Resolve the dead legacy-knowledge-attach UI — either wire it up, remove it, or warn
    admins it has no effect on agent-bound chat (currently silently misleading).

Should Fix:
  - F-03 (MEDIUM): Either populate agent_usage_events for real or remove it; add gateway/llm/tool/rag/db
    latency breakdown instrumentation for genuine performance triage.
  - F-04 (MEDIUM): Consolidate the duplicate voucher-extraction implementation (job-runner regex parsing
    vs. the dedicated MCP tools), or document why both must exist.
  - F-06 (LOW): Move the accounting-calculator tool seed rows from a standalone script into a real
    migration so their availability isn't deployment-order-dependent; remove or wire the remaining
    confirmed-orphan tools (confirm/reject_tax_verified, search_notes/get_note/save_note/fetch_url).
  - F-07 (LOW): Populate tool_name/tool_ok/tool_output in the manifest-driven executor's step records for
    API-response transparency.
  - F-08 (LOW): Close the plain-USER-role live-test gap with a disposable test account in a follow-up.

Nice to Have:
  - F-05 (LOW): Make always_on/when skill flags either work for manifest-driven agents or be removed from
    the frontmatter schema to avoid operator confusion.
  - F-09/F-10/F-11/F-12 (INFO): documentation/code-comment cleanups only, no functional risk found.
  - Input-tokens metric in the Redis audit store looks miscounted (values of 2-6) — worth a quick look,
    likely low effort to fix, not urgent since it isn't used for billing today (no consumer found).
```

---

## Production Readiness

**READY WITH CONDITIONS**

Evidence for readiness: the core manifest-driven execution chain (auth → tenant isolation → agent
resolution → tool-grant enforcement → HITL gating → real business-tool execution → real RAG retrieval with
citations and prompt-injection resistance) was reproduced **live**, end-to-end, against the real running
stack for a real tenant, not merely read in source. Tenant isolation, cross-tenant denial, and the
"agent never posts to the ledger" principle all have both static (file:line) and live (HTTP 403s,
audit_log rows, Redis audit entries) evidence. No CRITICAL finding was produced by either the 6-agent
static review or this session's live testing.

Conditions before calling this unconditionally READY:
1. Fix F-01 (SSRF) before `import_excel_ledger` is granted to any agent-tier below `execute_with_approval`,
   or add the network-layer hardening regardless (defense in depth — HITL approval of a URL string does
   not obviously convey network-probing risk to a human approver).
2. Resolve F-02 (dead knowledge-attach UI) — a silently non-functional admin feature is a real operational
   risk (an admin could believe compliance-sensitive knowledge is reachable by the agent when it is not).
3. Close F-08 (plain-USER-role live test) before signing off on the full RBAC matrix as production-verified
   rather than code-verified.

Everything else in the Final Gap List (F-03 through F-07, F-09 through F-12) is real but does not block
production use of the Accounting Agent as currently configured for the Sáu Trực tenant.

---

## Remediation (Hardening Phase — 2026-09-24)

Full implementation report: `docs/ACCOUNTING_AGENT_HARDENING_REPORT.md`. Status below per original
finding — never marked FIXED without test or runtime evidence; evidence is the exact test name/command,
not a description of intent.

| ID | Finding | Status | Evidence |
|---|---|---|---|
| F-01 | SSRF in `import_excel_ledger` (HIGH) | **FIXED** | New `agent/src/aiq_agent/security/ssrf.py` (`safe_get`/`validate_url`) enforces scheme allowlist, localhost/loopback/private/link-local/reserved/unspecified/IPv4-mapped blocking, per-redirect-hop re-resolution, incremental size cap, explicit timeout, and safe (non-leaking) error mapping. Wired into `accounting_tools.py`'s `import_excel_ledger`, replacing the old unchecked `httpx.AsyncClient(follow_redirects=True)`. Verified: `agent/tests/test_ssrf_protection.py`, 33/33 passing (`pytest tests/test_ssrf_protection.py -v`), covering every category above plus a redirect chain that resolves publicly on hop 1 and privately on hop 2 (proves per-hop re-validation, not just origin-URL validation) and an incrementally-enforced size cap (a mocked async byte stream exceeding the cap with no Content-Length header). Full existing agent suite re-run clean alongside it (205 passed, 8 pre-existing skips, 0 failed). |
| F-02 | Legacy per-note "Attach Knowledge" dead for agent-bound chat (MEDIUM) | **FIXED** | `web/src/app/api/chat/route.ts`: the legacy per-note fetch/rank/build-context block now runs only in the `!agentId` branch (the one case Python's `/chat` actually reads `knowledge_context` for) — an agent-bound request no longer does the wasted fetch at all, and the code comment states exactly why. `web/src/app/(app)/admin/agents/[id]/page.tsx`'s "Kho kiến thức" section — which previously made the literally false claim "Notes gắn ở đây trở thành ngữ cảnh RAG khi user chat với agent này" — now carries an explicit, visible warning that this mechanism has no effect on real chat, and points admins at the real Knowledge Base flow. No second Knowledge retrieval path was introduced — the one real path (`agent/src/aiq_agent/knowledge/service.py:retrieve_context`, Qdrant-backed) is untouched. Verified: `npx tsc --noEmit` clean, `npm run build` clean (both re-run after this change). |
| F-03 | `agent_usage_events` never populated (MEDIUM) | **FIXED** | New migration `050_agent_usage_reporting.sql` adds `trace_id, provider, model, total_latency_ms, llm_latency_ms, tool_latency_ms, rag_latency_ms, llm_call_count` columns. New `POST /runtime/agents/{agentId}/usage` (`AgentUsageEndpoints.cs` + `IAgentUsageRepository`/`AgentUsageRepository`), gated by the same `resource_or_tool` policy as HITL evaluate, tenant/department/actor always server-derived from the authenticated principal (never the request body). New `agent/src/aiq_agent/observability/usage_reporter.py:report_usage()` called from both `/chat` and `/agent/run` success paths in `routes.py`, fire-and-forget (a reporting failure never fails the user's request). `rag_latency_ms` (real, wraps the single `retrieve_context()` call) and `tool_latency_ms` (real, summed from each granted tool's own handler duration, via a new optional `latency_sink_ms` parameter threaded through `_wrap_tool_with_hitl_gate`/`_resolve_tools`) are genuine measurements; `llm_latency_ms` is an explicitly-labeled derived approximation (`total − rag − tool`, floored at 0) since neither provider exposes a true per-turn LLM-only span — documented in code and in the migration's column comment, never presented as a real measurement. Redis (`aiq_agent.audit.store`) is unchanged and remains the realtime/capped log; Postgres is now the durable source. Verified: gateway build clean (`dotnet build` 0 errors), full agent suite re-run clean (205 passed) after the `executors.py`/`routes.py` changes — **not yet exercised against a live request in this phase** (would require the same live-login technique used in the audit; scoped out of this pass — see Gaps below). |
| F-04 | Duplicate/competing voucher-extraction implementation (MEDIUM) | **PARTIALLY FIXED** | Confirmed, via direct grep, that a THIRD independent copy (`web/src/app/api/vouchers/upload/route.ts`'s own `extractJsonBlocks`/`toGatewayFieldsPayload`/`toJournalPayload`/`AgentRunResponse`) was 100% dead code — the route's actual `POST` handler never called any of them, only `gateway.uploadVoucher(...)`. Removed entirely (not merely disabled) — zero behavior change, verified by `npx tsc --noEmit` + `npm run build` both clean. `VoucherExtractJobRunner.cs`'s own copy is now the sole implementation for the background-job path (its stale "mirror of web-tier prompt/extractJsonBlocks" comments, which were actively misleading after the above removal, were corrected). The remaining, still-separate `agent/src/aiq_agent/agentic/voucher_tools.py` (`extract_voucher_fields`/`suggest_journal_entry`) was assessed and NOT merged: it serves a different call pattern (interactive, tool-calling-driven; the job runner needs a synchronous structured return value in one Hangfire step to immediately chain AR/AP enrichment) under the same manifest/tool-grant/HITL governance, and a full merge was judged high-risk to the production voucher pipeline (6 voucher types) for this phase's "do not change unrelated product behavior" constraint. Cross-referencing comments were added in both files so this is a documented, deliberate split, not unrecognized drift. |
| F-05 | Skill `always_on`/`when` flags inert for manifest-driven agents (LOW) | **OPEN** | Not addressed this phase — the audit's own recommendation ("either enforce these flags for manifest-driven agents or remove them from the schema") is a product/design decision outside this phase's fix list, which the task explicitly scoped to priorities 1-5. |
| F-06 | Python tools not grantable via governance (LOW) | **OPEN** | Not addressed this phase — the 7 accounting-calculator tools were already corrected to "not orphaned" during the audit itself (real DB evidence they're seeded and grantable); the genuinely-orphaned 4 (`confirm_tax_verified`, `reject_tax_verified`, `search_notes`/`get_note`/`save_note`/`fetch_url`) were out of this phase's priority list. |
| F-07 | `steps[].tool_name` always null in manifest-driven `/chat` response (LOW) | **OPEN** | Not in this phase's priority list. Still reproducible: the manifest-driven executor's `steps` never populate `tool_name`/`tool_ok`/`tool_output`, unlike the PEC path. |
| F-08 | Plain-"user"-role assignment path not live-tested (LOW) | **FIXED** | New automated tests in `gateway/tests/Aiq.Gateway.Tests/Data/DepartmentAgentOwnershipIntegrationTests.cs`, run against real PostgreSQL (not mocked): `DisabledDepartmentAgent_AdminAndUserExecutionDenied_WithIsolatedReason` proves a same-tenant/same-department disabled agent denies BOTH `admin` and `user` roles with the isolated `agent_disabled` reason (the live audit could only reach this via a foreign tenant's agent, where the tenant check masked the reason as `agent_not_owned_by_department`) — this test also re-enables the agent and confirms execution resumes, proving the toggle isn't one-way. `ToolAboveAgentsActionTier_CannotBeGranted_AndUngrantedToolNeverAppearsInManifest` proves a tool above the agent's `action_tier` cannot even be granted (`EnsureToolsEligibleAsync` throws `tool_not_eligible` — stricter than the audit assumed: not "grant then silently drop", but "refuse the grant outright") and that an ungranted tool never appears in the resolved manifest. Both run alongside the file's 6 pre-existing tests (`UserAccounting_DirectAssignmentPlusDepartmentEntitlement_Executes`, `UserAccounting_NoDirectAssignment_Denied`, `TenantB_NeverReachesTenantAsAgentRegardlessOfKnownUuid`, `Test_ResolvesCurrentDraftBeforeAnyPublish_OwnerOnly`, etc. — which the audit had not previously cross-referenced against this exact finding). Verified: `dotnet test --filter FullyQualifiedName~DepartmentAgentOwnershipIntegrationTests` → **8/8 passed** against real Postgres. |
| F-09–F-12 | INFO items (two agent rows, empty `department_capability_bindings`, Redis auth, legacy `ApprovalEndpoints`) | **OPEN** | Documentation/tracking items only, explicitly out of this phase's fix priorities; no functional risk was attached to any of them in the original audit. |
| — | "Agent không ghi sổ" principle | **NO REGRESSION** | Not a finding (the audit found zero bypass). Re-confirmed structurally intact this phase: none of the new code (SSRF fetch, usage reporter, tool-latency wrapper) adds a Gateway POST/PUT/PATCH call from Python tool code, and the two new C# endpoints (`AgentUsageEndpoints`, tool-tier enforcement already existed) touch only `agent_usage_events` (a reporting table) and the pre-existing tool-grant table — neither is a ledger/journal/tax table. |

### Gaps carried forward from this phase

- F-03 is implemented and unit/build-verified but **not yet exercised against a live running request** in
  this phase (no fresh live login was performed here) — the next verification pass should repeat the
  audit's live-login technique, send one real `/chat` request through an agent, and confirm a row lands in
  `agent_usage_events` with plausible `total_latency_ms`/`rag_latency_ms`/`tool_latency_ms` values.
- F-04 is a documented, deliberate non-merge, not a completed unification — if "one canonical
  implementation" is later required literally (not just "no dead duplicates and no unrecognized drift"),
  that is a separate, larger, higher-risk project.
- The full gateway test suite (`dotnet test` with no filter, 314 tests) still hits the pre-existing,
  previously-documented Postgres `max_connections=100` exhaustion flake when run unfiltered/parallel
  (105 failures, all `53300: sorry, too many clients already` — zero non-infra failures, confirmed by the
  same `awk` genuine-failure filter used throughout this project's history). The filtered sweep actually
  relevant to this phase (`Security|Governance|Data.Ar|Accounting|AgentDepartmentEntitlement|
  DepartmentAgentOwnership|AgentPrincipalVisibility`, 166 tests) passed cleanly. This is an
  environment/infra limitation, not a regression introduced this phase.
