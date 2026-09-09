# D8 — Production Readiness Audit

**Analysis-only report.** Per explicit scoping for this pass: audit and
report across all 10 requested areas, do not implement fixes yet, and stay
scoped to this repository's code/config/CI — never touch the live BSN pilot
host (`app.cattuong.ai`, referenced in `docs/huong-dan.md`) or its
credentials. Everything below was verified by reading source/config
directly and, where noted, by running tests/E2E against **isolated,
throwaway Docker stacks** created and destroyed by this audit — never the
shared local dev stack (`aiq-web`/`aiq-gateway`/`aiq-agent`/... containers
already running on this machine) and never any remote host.

Findings are tagged **CRITICAL / HIGH / MEDIUM / LOW / INFO** by realistic
impact if left unaddressed, not by how interesting they are.

---

## 1. Full security regression

Re-ran, against a freshly built, unmodified Gateway image (`:latest` — no
Gateway/Agent source has changed since D7):

| Suite | Result |
|---|---|
| Gateway `dotnet test` vs. real isolated Postgres, serial execution | **147/147 passed** |
| Agent `pytest` | **57/57 passed** (confirmed after a network-throttled first attempt hung on a cold dependency install — see Addendum) |
| `scripts/test_fresh_database_bootstrap.sh` | **passed** — 23 migrations apply cleanly to an empty volume, Gateway healthy before and after a restart |
| `scripts/test_d3_department_execution_e2e.sh` | **46/46 passed** |
| `scripts/test_d4_business_capability_e2e.sh` | **42/42 passed** |
| `scripts/test_d5_department_management_e2e.sh` | **49/49 passed** |
| `scripts/test_d6_business_page_conversion_e2e.sh` | **45/45 passed** |
| `scripts/test_d7_navigation_consolidation_e2e.sh` | **53/53 passed** |

**Total: 439 automated checks green** across unit/integration and real-HTTP
E2E, zero regressions. This confirms the D1-D7 security model (department
RBAC, capability resolution, cascade-revoke semantics, navigation gating)
is intact as of today. It does **not** by itself prove CI enforces this —
see §2.

**INFO** — none of this is new coverage; it's a repeat of what D3-D7 already
established, run once more as a snapshot for this audit.

## 2. CI/test isolation

| Finding | Severity | Evidence |
|---|---|---|
| CI never runs Gateway's DB-backed integration tests against a real Postgres — `dotnet test` in `.github/workflows/ci.yml`'s `gateway-build` job has no Postgres service container and sets no `POSTGRES_*` env vars, so every integration test's setup/teardown silently no-ops (`ResolveTestConnectionString()` returns null, test bodies `return` early). CI green today means **only** that the code compiles and the ~0-connection-string test bodies didn't throw — it does not exercise cross-department/cross-tenant authorization logic at all. | **HIGH** | `.github/workflows/ci.yml`; `gateway/tests/Aiq.Gateway.Tests/Data/DepartmentRbacIntegrationTests.cs:49-64` |
| Web has **no CI job at all** — no `tsc`, no `next build`, no `next lint`, no `npm test` (the new Vitest suite from D7). A broken Web build or a failing nav-model test would not be caught before merge. | **HIGH** | grep across `.github/workflows/*.yml` for `vitest`/`next build`/`next lint`/`web/` — zero hits |
| All six real-HTTP E2E scripts (fresh-bootstrap, D3-D7) exist only under `scripts/` and are never invoked by any workflow — they only run when a developer remembers to run them manually (as this session has, every phase). | **HIGH** | grep across workflows for `scripts/test_` — zero hits |
| The documented `serial.runsettings` workaround for the shared-Postgres connection-ceiling race (`Npgsql.PostgresException 53300` above ~144 parallel tests) is **not checked into the repo** — it exists only as an ad hoc file a developer creates locally each time (this session recreated it in a scratch directory). If Postgres is ever added to CI without this file, tests will intermittently fail once the suite crosses ~144 tests (it's at 147 now). | **MEDIUM** | no `*.runsettings` file anywhere in the repo; documented in `docs/PHASE_D5_DEPARTMENT_MANAGEMENT_REPORT.md` §12 and `PHASE_D6_...REPORT.md` §11 |
| No non-blocking failure gates found (no `continue-on-error` wired to test steps) — a real test failure would fail the job. Actual GitHub branch-protection/required-checks configuration is not visible from the repo and cannot be confirmed here. | INFO (already fine, with a caveat) | grep across workflows |

**Bottom line:** CI currently verifies "it builds" for Gateway/Agent and
nothing for Web, and the ~380 checks in §1 that actually prove the security
model works are **100% manual**. This is the single biggest gap in this
audit — everything else assumes a human remembers to run the E2E suite
before every deploy.

## 3. Fresh + upgrade migration verification

- **Fresh install: verified.** `scripts/test_fresh_database_bootstrap.sh`
  confirms all 23 migrations apply cleanly to an empty volume and the count
  is stable across a Gateway restart (proves the migration runner is
  idempotent/re-entrant-safe on an already-migrated database).
- **Upgrade path onto a populated database: not tested anywhere.** The
  fresh-bootstrap script never seeds meaningful data before restarting —
  it proves "migrations don't re-run," not "a new migration lands safely
  on a database that already has real tenants/departments/users/agent
  assignments in the affected tables." A production upgrade (e.g. the next
  migration, 024) will run against the live BSN tenant's actual rows for
  the first time with zero prior rehearsal. Given this repo's own
  migrations have included non-trivial backfills (D1's default-department
  backfill, D2's role-aware invariants), this is a real gap for a system
  with a live pilot tenant.
- **MEDIUM** — no automated test applies a new migration to a
  realistically-populated database and asserts data integrity afterward.

## 4. Secrets/config hardening

| Finding | Severity | Evidence |
|---|---|---|
| `docker-compose.yml` ships hardcoded weak fallback credentials for infrastructure services: `POSTGRES_PASSWORD:-aiq`, `REDIS_PASSWORD:-aiq`, `POSTGRES_USER:-aiq`, `Object__AccessKey:-aiq`, `GF_SECURITY_ADMIN_PASSWORD:-aiq` — all default to the literal `"aiq"` if `.env` is absent. Same pattern in `docker-compose.cosmos.yml`. | **HIGH** | `docker-compose.yml:10,51,59,78,88,126,304`; `docker-compose.cosmos.yml:50` |
| MinIO root password fallback (`MINIO_ROOT_PASSWORD:-changeme_min_32_chars_before_prod`) is self-documenting as unsafe but still a working default that would silently function in a real deployment that never created a `.env`. The Gateway's own S3 client credentials (`Object__AccessKey`/`Object__SecretKey`) inherit this same weak default. | **MEDIUM** | `docker-compose.yml:88-89,255,281` |
| JWT signing key handling is solid: no default in compose (`${JWT_SIGNING_KEY}` bare), `appsettings.json` ships only a placeholder, and `Program.cs` throws at startup if missing or under 32 chars. | INFO (already fine) | `docker-compose.yml:80`; `gateway/src/Aiq.Gateway/Program.cs:110-112` |
| All other third-party secrets (`ExternalAuth__SharedSecret`, `QDRANT_API_KEY`, `GATEWAY_SHARED_SECRET`, `ANTHROPIC_API_KEY`, `CLERK_SECRET_KEY`, etc.) default to empty string, never a working fallback. | INFO (already fine) | `docker-compose.yml` |
| `.gitignore` covers `.env`/`.env.local`/`.env.*.local`/`*.env`; no `.env` was ever committed (`git log --all --full-history -- .env` empty); `.env.example` uses only clearly-fake placeholders. | INFO (already fine) | `.gitignore:2-4` |
| Bootstrap SQL (`infra/postgres/bootstrap/first-super-admin.sql`) embeds no password. | INFO (already fine) | |
| Repo-wide grep for real-credential shapes (`sk-ant-`, AWS access-key pattern, PEM headers) found nothing; all password-literal hits are throwaway E2E fixtures in `scripts/test_d*_e2e.sh`. | INFO (already fine) | |

**Bottom line:** the things that would be catastrophic if leaked (JWT
signing key, third-party API keys) are handled correctly (fail closed, env
only). The things that are merely bad-if-defaulted (Postgres/Redis/MinIO/
Grafana passwords) ship a working weak default in a file that's in the
repo — fine for local dev, a real risk if anyone ever runs
`docker compose up` against a reachable host without first creating a
proper `.env`.

## 5. Audit/log hygiene

- **Generic state-changing-request audit trail is clean.**
  `AuditMiddleware.cs` records method+path, resource type/id, status code,
  query string, IP, user-agent — never request/response body, never
  headers, never tokens. Confirmed by reading the middleware directly, not
  just trusting prior phase reports.
- **D3-D6's capability/department audit writes are clean** (re-confirmed
  this session by reading `CapabilityEndpoints.cs`'s `AuditEntry` call):
  tenant/department/user id, capability code, resolved agent id, denial
  reason — no secrets.
- **Finding:** `gateway/src/Aiq.Gateway/Books/IAgentClient.cs` logs the raw
  upstream response body from the Agent service on any non-success status,
  in three places (`RunOcrPipelineAsync`, `RunOcrImageAsync`,
  `RunAgentTaskAsync` — the last one is the D6 bug-fix call site).
  This is the Agent's own error response, not a credential, but it could
  echo back user-submitted task text (which may contain real business data
  — e.g. a pasted invoice or a voucher's extracted content) into the
  Gateway's structured logs, which per `docs/huong-dan.md` flow into Loki
  with a real (confirmed, see below) 14-day retention. Not a secrets leak,
  but a "sensitive business data in application logs on the error path"
  finding worth a decision (log a truncated/redacted body, or accept the
  tradeoff since it's error-path-only).
- **LOW** for the above — it only fires on failure responses, not the
  golden path, and never includes the Authorization header/token itself
  (confirmed: only `response.Content`, headers are never logged).
- **Log retention independently verified, not just trusted from
  `huong-dan.md`:** `infra/loki/loki-config.yaml:41` sets
  `retention_period: 336h` (14 days, not the doc's stated "15 ngày" — a
  trivial doc inaccuracy, not a real issue).

## 6. Docker/runtime resilience

`docs/huong-dan.md` claims "container hardening (read-only filesystem, no
extra OS capabilities, outbound network blocked)." Verified against actual
config rather than trusted:

| Finding | Severity | Evidence |
|---|---|---|
| The claim is **partially true and only in a separate VPS-specific overlay** (`docker-compose.cosmos.yml`), not the base `docker-compose.yml` (zero `read_only`/`cap_drop`/`security_opt` there). The overlay applies `read_only`, `tmpfs`, `cap_drop: [ALL]`, `security_opt: [no-new-privileges]` to **only the `web` service** (added after a documented 2026-07-26 crypto-miner incident). Gateway and Agent — arguably higher-value targets, since Agent makes outbound LLM calls and Gateway holds DB/JWT secrets — get no equivalent hardening in the overlay that exists. | **MEDIUM** | `docker-compose.cosmos.yml:102-113` vs. gateway/agent service blocks in the same file |
| "Outbound network blocked" is not literally implemented anywhere found in these files — the cosmos overlay removes inbound host port bindings for internal services, which is a different thing from egress control. No `internal: true` network, no egress firewall rule in either compose file. **Cannot verify from this repo** whether egress is blocked at the host/firewall level outside these files — the audit agent correctly flagged this as unconfirmable rather than assuming either way. | INFO (unverifiable from repo) | |
| Restart policies (`unless-stopped`), healthchecks, and `depends_on: condition: service_healthy` are correctly wired for Postgres/Redis/MinIO/Qdrant/Gateway/Agent. Web/Caddy/Grafana/Promtail depend on start-order only (no healthcheck gate) — low severity for those roles. | INFO (already fine, minor gap) | `docker-compose.yml:72-74,139-141,277-279` |
| All stateful services use named/persistent volumes; no anonymous volumes. | INFO (already fine) | `docker-compose.yml:316-327` |
| Backup/restore is documented and real: hourly `pg_dump` via `infra/backup/postgres-backup.sh`, daily Qdrant snapshot, 14-day local + 90-day NAS retention, documented `pg_restore` runbook. | INFO (already fine) | `docs/operations/disaster-recovery.md` |
| All three Dockerfiles (gateway/web/agent) are multi-stage and run as a non-root `USER`. | INFO (already fine) | |
| **The `claude_cli` LLM provider (used in production per the cosmos compose's `LLM_PROVIDER=claude_cli`) spawns its CLI subprocess with no timeout at all** — `max_turns` bounds conversation length, not wall-clock time. A stalled `claude` subprocess can hang that request indefinitely with nothing in the Agent's own code to cut it off. The Gateway's own outbound HTTP timeout to the Agent (`Agent__TimeoutSeconds`, default 300s) would eventually surface this as a user-facing failure, but doesn't kill the stuck subprocess inside the Agent container — it would keep consuming a slot/resources until the container itself is restarted. | **MEDIUM** | `agent/src/aiq_agent/llm/claude_cli.py`; `gateway/src/Aiq.Gateway/Program.cs:159`, `AgentOptions.cs:13` |
| The Anthropic direct-API provider has no explicit timeout/retry override, relying on SDK defaults (~10 min) — bounded, but worth being intentional about rather than implicit. | LOW | `agent/src/aiq_agent/llm/anthropic_provider.py` |
| Ollama provider has an explicit, configurable timeout. | INFO (already fine) | `agent/src/aiq_agent/llm/ollama.py:25` |

## 7. Performance smoke checks

Ran the existing `tests/load/auth.js` k6 script (register → login → /me,
ramping 0→20 VUs over 2 minutes) against a freshly built, isolated
Gateway + Postgres stack (own network/volume, destroyed after):

- 1,094 iterations completed, 0 interrupted.
- The script's own `checks` (which correctly treat 429/409 as acceptable
  outcomes under its documented rate-limit test) passed at 98.17%.
- **Finding:** the script's k6 `thresholds` block does **not** account for
  its own accepted-status set — k6's built-in `http_req_failed` metric only
  counts 200-399 as non-failed by default, so every legitimately-expected
  429 (the auth rate limiter correctly firing, which is the entire point of
  this test) counts as a "failed" request in that metric, and the
  `http_req_failed rate<0.25` threshold fails every time the rate limiter
  works as designed. This means the documented CI policy in
  `tests/load/README.md` ("If thresholds fail, the load test is considered
  a regression and the build is rejected") would **always reject a
  correctly-functioning rate limiter** — the test's own success condition
  contradicts its purpose. This is a test-authoring bug, not a runtime
  bug: the rate limiter itself worked exactly as intended (p95 latency
  1.24ms, well under the 800ms threshold; the only failing metric is the
  mis-scoped one).
- **MEDIUM** (test tooling correctness, not a runtime defect) — fix by
  either using `http.expectedStatuses(200, 201, 409, 429)` with k6's
  response-callback API, or dropping `http_req_failed` from this
  particular script's thresholds and relying on the custom `checks` alone.
- `books.js` and `agent_chat.js` were not run this session (the former
  needs a pre-existing bearer token and seeded books data; the latter needs
  a live LLM provider) — noted as untested this pass, not as a finding.
- No load test is wired into CI (confirmed alongside §2's findings) despite
  the README's stated intent that failures should reject a build.

## 8. Full browser-equivalent E2E

- D3-D7 collectively provide **235 real HTTP/server-rendered E2E checks**
  (46+42+49+45+53) driving actual Gateway JWTs and, as of D7, an actual
  Next.js server via `curl` — proven effective at catching real defects
  this session (D7's own accounting-nav-filtering bug was caught exactly
  this way).
- **No actual browser automation exists anywhere in the repo** — no
  Playwright, Cypress, or Selenium config/tests found. Nothing exercises
  client-side JavaScript behavior (dropdown menus, client-side routing,
  the `"use client"` `AppNav` component's interactive state, form
  validation feedback, toast/error rendering) the way a real user's browser
  would. The HTTP-driven E2E approach proves server-side authorization and
  initial-render HTML correctly, but cannot catch a client-side-only bug
  (e.g., a broken `onClick` handler, a React hydration mismatch, a dropdown
  that never opens).
- **MEDIUM** — this is a real, acknowledged gap, consistent with every
  prior phase's explicit scoping note ("this does not drive a live browser,
  HTTP status/HTML is the proof point"). Given the product is demoed live
  to a real client (per `huong-dan.md`'s script), a client-side regression
  in, say, the voucher upload dropzone would not be caught by anything in
  this repo's test suite today.

## 9. Dead/legacy backend path review

| Item | Status | Evidence |
|---|---|---|
| `tenant_members` overlapping `users.tenant_id` | **Still live, unresolved** | `ITenantRepository.cs:220-350` still reads/writes it; canonical tenant is `users.tenant_id` |
| Legacy `agent_tool_grants` vs. current `agent_version_tool_grants` | **Still live, unresolved, both actively used** | Legacy: `IToolRepository.cs`, `IAgentAssignmentRepository.cs:84`, table from `infra/postgres/init/13_governance_tools.sql`, seeded by `scripts/seed_demo_agents.sql`. Current (what dynamic execution actually uses): `IDynamicAgentRepository.cs`. No Agent/Python code touches either — Gateway-only duality. |
| Legacy agent ACL system (`agent_acl` table, `grantAgentAcl`/`revokeAgentAcl`) | **Still live and NOT vestigial** — real seeded rows exist on fresh bootstrap (`infra/postgres/init/12_backfill_default_tenant.sql:90-93`) and in every demo-tenant seed script (BSN pilot included). Actively used by `/admin/agents/[id]`'s ACL section (the D7-confirmed canonical replacement for the now-deleted per-user variant). | `AgentEndpoints.cs:25-27`, `IAgentRepository.cs` |
| Simulator/business schema outside checksummed migrations | **Resolved** (D1.5) — promoted into `020_simulator_business_schema.sql`; `infra/postgres/init/README.md` now forbids app schema in init scripts | |
| Project-wide `[Required]`(and other validation attributes)-on-positional-record-parameter bug | **Still open, ~90 occurrences repo-wide**, only 2 files ever narrowly fixed (D2's `UserEndpoints.cs`, D5's `DepartmentEndpoints.cs`). Full remaining list (by file): `TenantEndpoints.cs`, `CapabilityEndpoints.cs`, `AgentKnowledgeEndpoints.cs`, `HitlEndpoints.cs`, `IHitlRepository.cs`, `IAgentRepository.cs`, `ITenantRepository.cs`, `IDynamicAgentRepository.cs` (the largest concentration — 20 occurrences), `IReviewBoardRepository.cs`, `IWorkflowRepository.cs`, `IToolRepository.cs`, `ContractRecords.cs`, `IChecklistRepository.cs`, `VoucherRecords.cs`, `NoteRecords.cs`, `BookRecords.cs`, **`Auth/Dtos.cs`** (register/login requests themselves). | **HIGH** — this is the most concretely actionable and highest-severity item in this entire audit: `Validator.TryValidateObject` silently never runs these validators because the attribute binds to the record's constructor parameter, not the generated property. Malformed/empty input that "looks validated" in the code is not actually rejected at that layer anywhere except the two already-fixed files. This has been noted and explicitly deferred in every phase since D2 without ever being closed. |
| Repo-wide TODO/FIXME/HACK/XXX grep | Clean — none found; all "legacy"/"deprecated" mentions are intentional, documented back-compat comments, not distress markers | |

**Severity note on the `[Required]` gap:** whether this is exploitable
depends on what each affected endpoint does with unvalidated input
downstream (many are protected by a second layer, as D2 confirmed for the
one role-change endpoint it audited — `TenantUserRoleChangePolicy.Validate`
independently rejects bad input regardless of the broken attribute). This
audit did **not** re-verify a second independent layer exists for all ~90
occurrences — that would be its own significant undertaking. Flagging as
HIGH because of the sheer breadth (auth requests included) and because no
phase has ever systematically checked whether a second layer covers every
one of them, not because exploitability was newly proven here.

## 10. Production readiness — summary

| # | Area | Overall status |
|---|---|---|
| 1 | Full security regression | ✅ 439/439 automated checks green |
| 2 | CI/test isolation | 🔴 HIGH — CI doesn't run DB-backed tests, Web has no CI, E2E is manual-only |
| 3 | Fresh + upgrade migration | 🟡 Fresh: verified. Upgrade-onto-populated-data: never tested |
| 4 | Secrets/config hardening | 🔴 HIGH — weak hardcoded infra-credential fallbacks in compose; app-level secrets (JWT, API keys) are solid |
| 5 | Audit/log hygiene | 🟢 Clean, one LOW finding (error-path response-body logging) |
| 6 | Docker/runtime resilience | 🟡 MEDIUM — hardening overlay covers only `web`, not gateway/agent; no LLM subprocess timeout |
| 7 | Performance smoke | 🟡 MEDIUM — rate limiter itself works; the load-test script's own threshold is self-contradictory |
| 8 | Full browser-equivalent E2E | 🟡 MEDIUM — 235 HTTP-driven checks exist; zero real browser automation |
| 9 | Dead/legacy backend paths | 🔴 HIGH — the repo-wide `[Required]` validation gap is the standout actionable item |
| 10 | This report | — |

**No code was changed in this pass.** The next decision is which of the
above to actually fix, and in what order — this report doesn't presume
that ordering. If useful next steps: (a) wire Postgres + `serial.runsettings`
+ Web `tsc`/build/`npm test` into CI (closes §2, the biggest structural
gap), (b) fix the `[Required]`-on-positional-record-parameter pattern
project-wide in one pass rather than per-file (§9), (c) remove the weak
`docker-compose.yml` credential fallbacks or make them fail closed like the
JWT key already does (§4).

---

## Addendum — Agent pytest result

**57/57 passed** (`pytest -q`, `agent/`), confirming the D3-D6 Agent-side
security/runtime tests are unaffected — expected, since zero Agent Python
files have changed since D6.

Getting there took two attempts, worth recording as its own minor
operational note: the first attempt (a fresh `pip install -e .` with no
cache) stalled — this repo's Agent dependency tree pulls in `fastembed`,
which drags in `onnxruntime`/`huggingface-hub` and a large transitive
closure, and the sandbox's network was throttled to ~29 KB/s at the time,
making a fully cold install impractical within any reasonable timeout. This
was an environment/network condition at the moment of testing, not a code
or dependency-declaration defect — re-running with a persistent pip-cache
Docker volume (so downloads only had to happen once) completed the same
install + test run in seconds once cached. Not filed as a numbered finding
above since it reflects transient sandbox network conditions, not something
in the repo to fix — but worth knowing that a genuinely cold, from-scratch
Agent environment build can be slow if it ever needs to happen under
constrained bandwidth (e.g. a resource-limited CI runner).
