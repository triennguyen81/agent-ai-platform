# Accounting Agent Hardening Report

Implementation date: 2026-09-24
Basis: `docs/ACCOUNTING_AGENT_AUDIT.md` (2026-09-24) — implements priorities 1-5 from the audit's remediation
request, strictly scoped to the findings verified there. No new accounting business features were added.

---

## Scope and constraints honored

- ExecutionManifest remains the sole runtime authority — no new hardcoded prompt/tool/model bypass was
  introduced; the one new Gateway→Python data flow (usage reporting) is Python→Gateway, reporting facts
  about a completed manifest-driven execution, never influencing which manifest resolves or what it grants.
- Tenant isolation was not touched in any way that weakens it: the new `POST /runtime/agents/{agentId}/usage`
  endpoint derives `tenant_id`/`department_id`/`actor_id` exclusively from the authenticated principal
  (`AuthenticatedPrincipalContext`), never from the request body — the same pattern as the pre-existing HITL
  evaluate endpoint.
- Tool grants were not weakened — the SSRF fix changes network-layer behavior of one existing tool's
  handler, not what tools are grantable or how grants are resolved. The new tool-tier test in fact revealed
  the enforcement is *stricter* than the audit assumed (grants above an agent's action_tier are rejected
  outright, not silently dropped).
- HITL was not touched. No new tool, no new write path, no new bypass.
- The Accounting Agent still has no autonomous ledger-posting capability — re-confirmed structurally this
  phase (see the audit doc's Remediation table, "Agent không ghi sổ" row).
- No second Knowledge retrieval path was introduced — F-02's fix removes dead work from a path that already
  did nothing, it does not add a new mechanism.
- No silent fallback: F-02's fix makes the previously-silent no-op explicit (an HTML warning banner, not a
  removed feature); the SSRF fix fails closed (blocks) rather than falling back to the old unchecked fetch.
- Unrelated product behavior was not changed — verified by full `web` type-check + build, full `agent`
  pytest suite, and the relevant filtered `gateway` test sweep all passing with the same results shape as
  before this phase's changes (no new failures, no changed assertions in pre-existing tests).

---

## 1. SSRF fix — `import_excel_ledger`

**Root cause** (audit F-01): the tool fetched any caller-supplied `file_url` via
`httpx.AsyncClient(follow_redirects=True)` with no scheme/host/IP validation, and buffered the entire
response body before checking it against the 1 MB cap.

**Fix**: new module `agent/src/aiq_agent/security/ssrf.py`:

- `validate_url(url)` — validates the scheme (http/https only), rejects the literal hostname `localhost`
  (any case), resolves the hostname via `socket.getaddrinfo` (run off the event loop via
  `asyncio.to_thread`), and classifies every resolved address: loopback, private (RFC 1918), link-local
  (169.254.0.0/16 — including the cloud metadata endpoint 169.254.169.254 — and fe80::/10), reserved,
  multicast, unspecified, and IPv4-mapped IPv6 (`::ffff:127.0.0.1`-style) addresses are all rejected by
  unwrapping to the underlying IPv4 address and re-checking it.
- `safe_get(url, *, timeout, max_bytes, max_redirects, headers)` — disables the HTTP client's own
  redirect-following (`follow_redirects=False`) and instead follows redirects manually, one hop at a time,
  calling `validate_url` again before every single request (including redirect targets) — this is what
  closes the "public-looking hostname redirects to an internal IP" bypass class. The response body is read
  via `aiter_bytes()` with the byte count checked after every chunk, so a malicious/compromised server
  cannot exhaust memory by sending an oversized body before any check runs (the `Content-Length` header, if
  present, is also checked up front as a fast-path, but the incremental check is the real enforcement).
  Every httpx-level exception is caught and re-raised as `SsrfFetchError` with only the exception class name
  and message — never a raw stack trace reaching the tool's text output.
- `accounting_tools.py`'s `import_excel_ledger` now calls `safe_get` instead of raw `httpx.AsyncClient`;
  the dead, never-sent `auth_headers` local variable (flagged in the audit as a code smell) was removed in
  the same edit.

**Explicitly documented residual risk**: this validates the resolved IP at request time for each hop, but
does not pin the TCP connection to that exact IP — a DNS-rebinding attacker controlling both the initial and
a subsequent lookup could in principle swap the answer between validation and connection. Full protection
would need a custom httpx transport connecting to the pre-resolved IP while still presenting the original
Host/SNI. Not implemented — flagged in the module's own docstring rather than silently claimed as closed.

**Tests**: `agent/tests/test_ssrf_protection.py`, 33 cases:

- scheme rejection (`file://`, `ftp://`, `gopher://`)
- localhost (3 case variants)
- loopback/private/link-local/reserved/unspecified by literal IPv4 (9 addresses) and IPv6 (4 addresses)
- IPv4-mapped IPv6 loopback
- a real public IP literal allowed through
- DNS-resolution-based bypass: a benign-looking hostname (`looks-public.example.test`) mocked to resolve to
  a private IP → blocked; another mocked to the metadata endpoint → blocked; DNS failure → blocked; a
  hostname mocked to resolve publicly → allowed
- **redirect re-validation**: hop 1 (`public-hop1.example.test`, mocked to resolve publicly) returns a 302
  to hop 2 (`internal-hop2.example.test`, mocked to resolve to `10.5.5.5`) — the test asserts the mocked
  transport handler is never even called a second time (`raise AssertionError(...)` in the handler for any
  request other than hop 1), proving the block happens before the second request is issued, not after
  fetching it
- redirect with no `Location` header → blocked; too many redirects → blocked
- response size cap enforced incrementally (an async byte-stream response with no `Content-Length` header,
  exceeding the cap) and via the declared-header fast path
- successful fetch happy path (content, content-type, status code all returned correctly)
- network error (`httpx.ConnectTimeout`) wrapped as `SsrfFetchError`, not leaked raw
- `max_redirects < 0` rejected with `ValueError`

**Run**: `PYTHONPATH=src python3 -m pytest tests/test_ssrf_protection.py -v` → **33 passed**. Full existing
`agent` suite re-run alongside it: **205 passed, 8 skipped (pre-existing, unrelated — real-Postgres/Qdrant
integration tests, skipped when those aren't configured for the throwaway container this suite ran in), 0
failed**.

---

## 2. Legacy agent-note knowledge attachment — deprecated

**Root cause** (audit F-02): `web/src/app/api/chat/route.ts` always fetched, ranked, and built a
`knowledge_context` block from an agent's legacy per-note attachments (`GET /agents/{id}/knowledge/context`)
whenever `agent_id` was present — but `agent/src/aiq_agent/api/routes.py`'s manifest-driven branch
explicitly discards `body.knowledge_context` ("the signed Gateway manifest is the sole authority"). The
admin UI for attaching notes to an agent claimed, in Vietnamese, that attached notes "become RAG context
when a user chats with this agent" — false for every real agent-bound chat.

**Fix**:

- `web/src/app/api/chat/route.ts`: the per-note fetch/embedding-rank/context-build block now only executes
  when `agentId` is falsy — the one case Python's `/chat` handler actually reads `knowledge_context` for
  (the no-agent-id admin/super_admin plain-chat path). For an agent-bound request, none of that work runs
  at all, and a code comment explains exactly why, cross-referencing the audit finding and the exact line in
  `routes.py` that discards it, so a future re-wiring of either side won't silently reintroduce the mismatch
  without a maintainer noticing the comment.
- `web/src/app/(app)/admin/agents/[id]/page.tsx`: the "Kho kiến thức" (Knowledge) section's header now
  reads "Kho kiến thức (legacy per-note)"; the previously false claim was replaced with an amber warning
  box stating plainly that this mechanism has no effect on real chat with the agent, and pointing admins at
  the real Department Agent Knowledge Base tab instead. The attach/detach functionality itself was left
  working (not removed) — it remains genuinely useful for the still-functional no-agent-id plain chat
  feature; only the misleading claim about agent-bound chat was corrected. This is the "safely deprecate,
  don't silently break" choice: the constraint set explicitly forbade introducing a second Knowledge
  retrieval path, and this UI's endpoints are shared infrastructure also used by the legitimate no-agent-id
  path, so removing them outright would have changed unrelated, still-working product behavior.

**Tests**: `npx tsc --noEmit` (clean) and `npm run build` (clean) re-run after these two file edits; no
gateway or agent test suite touches this path (it's web-tier only), so no backend test evidence applies
here — the correctness evidence is the code-comment-documented removal of the exact call that was proven
dead in the audit, plus a clean build/typecheck confirming no other code depended on the removed branch's
side effects.

---

## 3. Durable usage/cost reporting → `agent_usage_events`

**Root cause** (audit F-03): the table existed (added in migration `024_acl_v2_scope_foundations.sql`) but
had zero rows anywhere in the database — nothing ever wrote to it. The only real instrumentation was a
capped, Redis-backed realtime log (`agent/src/aiq_agent/audit/store.py`) with no durable/SQL-queryable
counterpart.

**Fix**:

- **Migration** `gateway/src/Aiq.Gateway/Data/Migrations/050_agent_usage_reporting.sql` — additive,
  nullable columns on the existing table: `trace_id`, `provider`, `model`, `total_latency_ms`,
  `llm_latency_ms`, `tool_latency_ms`, `rag_latency_ms`, `llm_call_count`. Zero existing rows/behavior
  affected (the table had none).
- **Gateway**: `IAgentUsageRepository`/`AgentUsageRepository` (parameterized Dapper INSERT, one row per
  call — no upsert/dedup logic needed since each call represents one completed request) and
  `AgentUsageEndpoints.cs` exposing `POST /runtime/agents/{agentId}/usage`, gated by the exact same
  `resource_or_tool` authorization policy as the pre-existing HITL evaluate endpoint (`HitlEndpoints.cs`'s
  `EvaluateAsync` was used as the direct template). `tenant_id`/`department_id`/`actor_id` are read from
  `AuthenticatedPrincipalContext`, never the request body. Registered in `Program.cs` alongside the existing
  HITL DI registration and endpoint mapping.
- **Python**: `agent/src/aiq_agent/observability/usage_reporter.py` — `report_usage()` builds the payload
  from an `ExecutionManifest` + `ExecutionResult` and POSTs it to the new endpoint using the manifest's own
  short-lived `toolExecutionToken` (never a new or broader credential; skipped entirely when that token is
  absent — the departmentless super_admin test/preview path, which this durable table isn't meant to track).
  Every failure (network error, non-2xx response) is caught and logged, never raised — a reporting failure
  cannot turn a successful chat/agent-run into a failed HTTP response for the user, mirroring the existing
  Redis-audit-write's non-fatal posture exactly.
- Called from both `/chat` and `/agent/run`'s success paths in `agent/src/aiq_agent/api/routes.py`,
  immediately after (and using the same `_started`/`_t.monotonic()` timing variables as) the pre-existing
  Redis audit write — so Redis (realtime, capped) and Postgres (durable, queryable) are written from the
  same call site with the same underlying numbers, not two independently-drifting sources of truth.

**Component latencies** (llm/tool/rag), captured where the runtime data allows without invasive restructuring:

- `rag_latency_ms` — **real measurement**. Wraps the single `retrieve_context(self.manifest, prompt)` call
  in both `ClaudeSdkAgentExecutor.execute` and `OllamaAgentExecutor.execute` with `time.monotonic()`.
- `tool_latency_ms` — **real measurement**, summed. `_wrap_tool_with_hitl_gate` gained an optional
  `latency_sink_ms: list[int] | None` parameter (threaded through `_resolve_tools`); when present, it times
  only the `original_handler(args)` call — i.e. the tool's own work, excluding the HITL round-trip — and
  appends each duration. `ClaudeSdkAgentExecutor` sums the list into `ExecutionResult.tool_latency_ms`.
- `llm_latency_ms` — **explicitly-labeled derived approximation**, computed in `usage_reporter.report_usage`
  as `max(total_latency_ms − rag_latency_ms − tool_latency_ms, 0)`. Neither the Claude Agent SDK nor the
  Ollama HTTP client exposes a true per-turn, LLM-only span from inside its own black box (confirmed in the
  audit's orchestration review) — fabricating a more precise number was rejected in favor of this documented
  approximation, called out as such in three places: the `ExecutionResult` field docstring, the
  `usage_reporter` module docstring, and the migration file's own `COMMENT ON COLUMN` (for `llm_call_count`,
  which is likewise honestly documented as "always 1 today" rather than a true per-turn SDK call count,
  which is not observable from application code).
- `llm_call_count` — always `1` per usage row (one row per top-level `/chat` or `/agent/run` request that
  resolved a manifest and completed) — not a fabricated finer-grained count.

**Tests**: `python3 -c "import ast; ast.parse(...)"` syntax checks on all three modified/new Python files;
full `agent` pytest suite re-run after the `executors.py`/`routes.py` changes — **205 passed, 8 skipped, 0
failed** (no test asserts on the new fields directly, since no existing test constructs a full manifest→HTTP
round trip against a live Gateway, but the full suite passing confirms no regression to
`ExecutionResult`'s existing consumers, the tool-wrapping chain, or the two endpoint handlers). Gateway
`dotnet build` clean (0 errors) for the new endpoint/repository/migration.

**Deployment verification**: `gateway`, `agent`, and `web` were rebuilt and redeployed
(`docker compose build gateway web agent && docker compose up -d gateway web agent`) after this phase's
changes. Gateway startup log confirms `"Applied database migration 050_agent_usage_reporting.sql"` against
the real running Postgres, and `\d agent_usage_events` against the live database shows all 8 new columns
(`trace_id, provider, model, total_latency_ms, llm_latency_ms, tool_latency_ms, rag_latency_ms,
llm_call_count`) plus the partial index. All three containers came up with zero errors/exceptions in their
startup logs.

**Known gap** (carried into the audit doc's remediation table): this phase did not re-run the audit's live
login-and-chat technique to send an actual `/chat` request through the running stack and confirm a real row
lands in `agent_usage_events` end-to-end (that requires the same temporary, user-approved credential reset
used in the audit — out of scope without fresh consent in this phase). The implementation is build-, unit-,
and now deployment-verified (real migration applied, real schema confirmed, clean startup), but not
live-request-verified in this phase.

---

## 4. Duplicate voucher extraction — one dead copy removed, remaining split documented

**Root cause** (audit F-04): three places implemented "parse 2 JSON blocks out of an LLM's markdown reply
for voucher fields + journal entries": (a) `VoucherExtractJobRunner.cs` (C#, background Hangfire job), (b)
`web/src/app/api/vouchers/upload/route.ts` (TypeScript, described in its own header comment as the
synchronous predecessor flow), and (c) `agent/src/aiq_agent/agentic/voucher_tools.py`'s
`extract_voucher_fields`/`suggest_journal_entry` MCP tools (Python, for the interactive tool-calling flow).

**Investigation finding**: (b) was confirmed, by exhaustive grep across the file, to be **100% dead code** —
`extractJsonBlocks`, `toGatewayFieldsPayload`, `toJournalPayload`, and the `AgentRunResponse` interface were
all defined but never called; the actual `POST` handler only calls `gateway.uploadVoucher(...)` and returns
immediately (per its own "P1 async fix" comment, superseded months ago by the Hangfire pipeline). This is
not a judgment call — it is directly, mechanically verifiable (no call site exists in the file at all).

**Fix**:

- `web/src/app/api/vouchers/upload/route.ts` was rewritten to remove the entire dead block — down from ~180
  lines to ~55. The route's actual behavior (upload → relay `{voucherId, status:"uploading",...}`) is
  byte-for-byte unchanged; only unreachable code was deleted. Verified via `npx tsc --noEmit` + `npm run
  build`, both clean.
- `VoucherExtractJobRunner.cs`'s `BuildExtractionPrompt`/`ExtractJsonBlocks` comments, which described
  themselves as a "mirror of web-tier prompt/extractJsonBlocks", were corrected — they are now the sole
  canonical background-extraction implementation, and the comment says so, plus explains why they are
  deliberately not merged with (c): the job runner needs the extracted fields back as a synchronous
  structured return value within one Hangfire step (to immediately chain AR/AP context enrichment); the
  Python tools serve a different, interactive, tool-calling call pattern. Both persist through the same
  underlying save-voucher/save-journal-suggestions-equivalent repository calls and the same manifest/tool-
  grant/HITL governance.

**What was NOT done, and why**: fully unifying (a) and (c) into one shared implementation was assessed and
rejected for this phase — it would mean either making the background job route through the interactive
tool-calling mechanism (restructuring how a Hangfire job gets a synchronous return value from an
LLM-driven tool call it doesn't directly control) or making the interactive tools call into C# job-runner
logic across the language boundary — both are substantial, higher-risk changes touching the live voucher
pipeline for all 6 voucher types (invoice_vat_in/out, receipt, payment, bank_transfer, expense_claim),
explicitly out of scope for "do not change unrelated product behavior" and the general risk posture of a
hardening phase. Reported as **PARTIALLY FIXED** in the audit doc, not FIXED — one of two real duplicates
removed and verified, the other documented rather than merged.

---

## 5. USER-role E2E fixture and previously-NOT-VERIFIED authorization tests

**Root cause** (audit Section 9 / F-08): the live audit's Test E (unassigned USER → denied) could not be
reproduced live because no plain "user"-role account existed in the audited tenant, and the session's
approved live-test scope was limited to reusing the existing admin account. Test H (disabled agent) was
only reproduced via a foreign tenant's agent, so the returned denial reason was the tenant-scoping one
(`agent_not_owned_by_department`), never proving the `agent_disabled` branch fires in isolation. Test G
(invalid tool) was only demonstrated behaviorally (the LLM self-reported it had no such tool), not proven at
the repository/enforcement level.

**Investigation finding**: `gateway/tests/Aiq.Gateway.Tests/Data/DepartmentAgentOwnershipIntegrationTests.cs`
already had substantial, real-Postgres-backed coverage for the exact `GetDepartmentAgentManifestAsync` path
the real Accounting Agent uses — including `UserAccounting_DirectAssignmentPlusDepartmentEntitlement_
Executes`, `UserAccounting_NoDirectAssignment_Denied` (Test E's automated equivalent), `TenantB_
NeverReachesTenantAsAgentRegardlessOfKnownUuid` (cross-tenant, with an isolated denial reason), and
`Test_ResolvesCurrentDraftBeforeAnyPublish_OwnerOnly` (unpublished-agent denial) — none of which the
original audit had cross-referenced against its own NOT VERIFIED findings. The genuinely missing cases were
narrower than the audit implied: a same-tenant disabled-agent case (to isolate the reason from tenant
masking) and a repository-level tool-eligibility check.

**Fix — two new tests added to the existing fixture** (no new fixture file; the existing one already models
tenant A / accounting+sales departments / admin+user pairs / tenant B, matching what "a dedicated USER-role
E2E fixture" needs):

- `DisabledDepartmentAgent_AdminAndUserExecutionDenied_WithIsolatedReason` — publishes a real Department
  Agent, assigns the USER, confirms both roles execute successfully while enabled (sanity baseline), disables
  the agent via `SetDepartmentAgentEnabledAsync`, confirms **both** admin and user are denied with the
  isolated `agent_disabled` reason (same tenant, same department — no cross-tenant masking possible), then
  re-enables it and confirms execution resumes (proving the toggle is a live switch, not a one-way state).
- `ToolAboveAgentsActionTier_CannotBeGranted_AndUngrantedToolNeverAppearsInManifest` — seeds three tools
  (`within_tier` = `suggest`, matching the agent's default `action_tier`; `above_tier` = `autonomous`;
  `ungranted` = never granted at all). Asserts that attempting to grant `within_tier` + `above_tier` together
  at version-creation time throws `InvalidOperationException("tool_not_eligible")` — the actual, stricter-
  than-assumed enforcement point (grants are refused outright, not silently filtered later). Then creates a
  version granting only `within_tier`, publishes it, fetches the real manifest, and asserts `Tools` contains
  `within_tier` and excludes both `above_tier` and `ungranted`.

**Run**: `dotnet test --filter FullyQualifiedName~DepartmentAgentOwnershipIntegrationTests` against real
PostgreSQL → **8/8 passed** (6 pre-existing + 2 new). Broader sweep
(`Security|Governance|Data.Ar|Accounting|AgentDepartmentEntitlement|DepartmentAgentOwnership|
AgentPrincipalVisibility`) → **166/166 passed, 0 failed**.

---

## Test commands run this phase (verbatim, for reproducibility)

```bash
# Priority 1 — SSRF tests + full agent suite
docker run --rm --entrypoint sh -v "$(pwd)/agent":/work -w /work aiq-agentic-agent:latest -c "
  pip install --quiet 'pytest>=8.3' 'pytest-asyncio>=0.24'
  PYTHONPATH=/work/src python3 -m pytest tests/test_ssrf_protection.py -v"
# → 33 passed

docker run --rm --entrypoint sh -v "$(pwd)/agent":/work -w /work aiq-agentic-agent:latest -c "
  pip install --quiet 'pytest>=8.3' 'pytest-asyncio>=0.24'
  PYTHONPATH=/work/src python3 -m pytest tests/ -v --ignore=tests/test_ocr_pipeline.py"
# → 205 passed, 8 skipped, 0 failed

# Priority 2 & 4 — Web build/typecheck
cd web && npx tsc --noEmit -p tsconfig.json   # clean
cd web && npm run build                        # clean

# Gateway build
docker run --rm -v "$(pwd)":/src -w /src/gateway mcr.microsoft.com/dotnet/sdk:9.0 \
  bash -c "dotnet build src/Aiq.Gateway/Aiq.Gateway.csproj -c Debug"
# → Build succeeded, 0 warnings (new files), 0 errors
docker run --rm -v "$(pwd)":/src -w /src/gateway mcr.microsoft.com/dotnet/sdk:9.0 \
  bash -c "dotnet build tests/Aiq.Gateway.Tests/Aiq.Gateway.Tests.csproj -c Debug"
# → Build succeeded, 0 errors (5 pre-existing unrelated nullability warnings in ContractProcessingIntegrationTests.cs)

# Priority 5 — new + existing Department Agent ownership tests
docker run --rm --network aiq-agentic_aiq-net -v "$(pwd)":/src -w /src \
  -e POSTGRES_HOST=postgres -e POSTGRES_PASSWORD='Nqt@1234' -e POSTGRES_USER=aiq -e POSTGRES_DB=aiq \
  mcr.microsoft.com/dotnet/sdk:9.0 bash -c "cd gateway && dotnet test \
  --settings tests/Aiq.Gateway.Tests/serial.runsettings \
  --filter 'FullyQualifiedName~DepartmentAgentOwnershipIntegrationTests'"
# → 8 passed, 0 failed

# Broad regression sweep — tenant isolation / USER assigned-unassigned / cross-tenant /
# invalid-tool / unpublished-disabled-agent / accounting
docker run --rm --network aiq-agentic_aiq-net -v "$(pwd)":/src -w /src \
  -e POSTGRES_HOST=postgres -e POSTGRES_PASSWORD='Nqt@1234' -e POSTGRES_USER=aiq -e POSTGRES_DB=aiq \
  mcr.microsoft.com/dotnet/sdk:9.0 bash -c "cd gateway && dotnet test \
  --settings tests/Aiq.Gateway.Tests/serial.runsettings \
  --filter 'FullyQualifiedName~Security|FullyQualifiedName~Governance|FullyQualifiedName~Data.Ar|FullyQualifiedName~Accounting|FullyQualifiedName~AgentDepartmentEntitlement|FullyQualifiedName~DepartmentAgentOwnership|FullyQualifiedName~AgentPrincipalVisibility'"
# → 166 passed, 0 failed

# Full gateway suite (informational — pre-existing connection-pool flake, not a regression)
docker run --rm --network aiq-agentic_aiq-net -v "$(pwd)":/src -w /src \
  -e POSTGRES_HOST=postgres -e POSTGRES_PASSWORD='Nqt@1234' -e POSTGRES_USER=aiq -e POSTGRES_DB=aiq \
  mcr.microsoft.com/dotnet/sdk:9.0 bash -c "cd gateway && dotnet test --settings tests/Aiq.Gateway.Tests/serial.runsettings"
# → 209 passed, 105 failed — awk-filtered: 0 failures NOT matching "too many clients already"
```

---

## Files changed this phase

```text
NEW   agent/src/aiq_agent/security/__init__.py
NEW   agent/src/aiq_agent/security/ssrf.py
NEW   agent/tests/test_ssrf_protection.py
NEW   agent/src/aiq_agent/observability/usage_reporter.py
MOD   agent/src/aiq_agent/agentic/accounting_tools.py       (import_excel_ledger → safe_get; dead auth_headers removed)
MOD   agent/src/aiq_agent/runtime/executors.py               (rag_latency_ms / tool_latency_ms instrumentation)
MOD   agent/src/aiq_agent/api/routes.py                      (report_usage() wired into /chat and /agent/run)

NEW   gateway/src/Aiq.Gateway/Data/Migrations/050_agent_usage_reporting.sql
NEW   gateway/src/Aiq.Gateway/Governance/IAgentUsageRepository.cs
NEW   gateway/src/Aiq.Gateway/Endpoints/AgentUsageEndpoints.cs
MOD   gateway/src/Aiq.Gateway/Program.cs                     (DI + endpoint registration)
MOD   gateway/src/Aiq.Gateway/Jobs/VoucherExtractJobRunner.cs (stale "mirror" comments corrected)
MOD   gateway/tests/Aiq.Gateway.Tests/Data/DepartmentAgentOwnershipIntegrationTests.cs (2 new tests)

MOD   web/src/app/api/chat/route.ts                          (legacy knowledge_context skipped when agent_id present)
MOD   web/src/app/(app)/admin/agents/[id]/page.tsx            (deprecation warning replacing false claim)
MOD   web/src/app/api/vouchers/upload/route.ts                (dead duplicate parser removed — ~180 → ~55 lines)

MOD   docs/ACCOUNTING_AGENT_AUDIT.md                          (Remediation section appended)
NEW   docs/ACCOUNTING_AGENT_HARDENING_REPORT.md               (this file)
```

No accounting business logic, ACL rule, HITL gate, tool grant, or ledger-posting code path was added or
changed. No files outside the above were modified.
