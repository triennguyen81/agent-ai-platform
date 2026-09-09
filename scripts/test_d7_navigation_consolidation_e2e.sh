#!/usr/bin/env bash
# Phase D7 real, server-rendered E2E: D7 is Web-only navigation/menu
# consolidation, not an authorization redesign — D3/D4/D5/D6's own E2E
# scripts remain the authoritative proof of the underlying security model
# and are re-run unmodified for regression (see
# scripts/test_d3_department_execution_e2e.sh,
# scripts/test_d4_business_capability_e2e.sh,
# scripts/test_d5_department_management_e2e.sh,
# scripts/test_d6_business_page_conversion_e2e.sh). This script instead
# drives the actual Next.js Web server (not just the Gateway) with real
# session cookies for each of the four roles and checks: canonical routes
# render, deprecated routes redirect to their canonical replacement, the
# one discovered Web-only authorization gap (vouchers excluding literal
# `user`) is fixed, dead UI is actually gone, and capability-driven
# accounting sub-nav shows/hides tabs correctly — all via HTTP status
# codes and rendered HTML, no browser required.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-d7-e2e-net-${run_id}"
pg="aiq-d7-e2e-postgres-${run_id}"
gw="aiq-d7-e2e-gateway-${run_id}"
web="aiq-d7-e2e-web-${run_id}"
vol="aiq-d7-e2e-pgdata-${run_id}"
db="aiq_d7_e2e"
db_password="d7_e2e_password"
gw_image="aiq-agentic-gateway:latest"
web_image="aiq-web:d7test"
gw_base="http://localhost:18085"
web_base="http://localhost:13085"
gw_port=18085
web_port=13085

pass=0
fail=0
status=""
body=""
location=""

cleanup() {
    docker rm -f "${web}" >/dev/null 2>&1 || true
    docker rm -f "${gw}" >/dev/null 2>&1 || true
    docker rm -f "${pg}" >/dev/null 2>&1 || true
    docker volume rm "${vol}" >/dev/null 2>&1 || true
    docker network rm "${net}" >/dev/null 2>&1 || true
}
[[ -z "${KEEP_STACK:-}" ]] && trap cleanup EXIT

ok()   { pass=$((pass+1)); echo "  OK   $1"; }
bad()  { fail=$((fail+1)); echo "  FAIL $1"; }

expect_status() {
    local desc="$1" expected="$2" actual="$3"
    if [[ "${actual}" == "${expected}" ]]; then ok "${desc} (HTTP ${actual})"; else bad "${desc} (expected ${expected}, got ${actual})"; fi
}
expect_true() {
    local desc="$1" cond="$2"
    if [[ "${cond}" == "true" ]]; then ok "${desc}"; else bad "${desc} (got: ${cond})"; fi
}
# Gateway JSON API calls (bootstrap only).
req() {
    local method="$1" path="$2" token="${3:-}" req_payload="${4:-}"
    local args=(-sS -o /tmp/d7e2e_body.$$ -w '%{http_code}' -X "${method}" "${gw_base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/d7e2e_body.$$)"
    rm -f /tmp/d7e2e_body.$$
}
jqf() { echo "${body}" | jq -r "$1"; }

# Web page requests, authenticated as the given role's session cookie
# (the same httpOnly JWT the login server action sets — see
# web/src/lib/auth.ts's COOKIE_NAME). -i captures status + headers so
# redirects (Next.js `redirect()` from a Server Component/layout) are
# visible as real HTTP 307s with a Location header on a direct GET, same
# as a browser's first navigation.
webreq() {
    local path="$1" token="${2:-}"
    local args=(-sS -D /tmp/d7e2e_headers.$$ -o /tmp/d7e2e_body.$$ -X GET "${web_base}${path}")
    [[ -n "${token}" ]] && args+=(-H "Cookie: aiq.session=${token}")
    curl "${args[@]}" >/dev/null
    status="$(head -n1 /tmp/d7e2e_headers.$$ | awk '{print $2}')"
    location="$(grep -i '^location:' /tmp/d7e2e_headers.$$ | tail -n1 | sed 's/^[Ll]ocation: //' | tr -d '\r' || true)"
    body="$(cat /tmp/d7e2e_body.$$)"
    rm -f /tmp/d7e2e_headers.$$ /tmp/d7e2e_body.$$
}
expect_redirect_to() {
    local desc="$1" expected_path="$2"
    if [[ "${status}" == "307" || "${status}" == "302" ]] && [[ "${location}" == *"${expected_path}"* ]]; then
        ok "${desc} (-> ${location})"
    else
        bad "${desc} (status=${status}, location=${location})"
    fi
}
body_contains() { [[ "${body}" == *"$1"* ]] && echo true || echo false; }

echo "== D7 E2E: booting isolated Postgres + Gateway + Web stack =="
docker network create "${net}" >/dev/null
docker volume create "${vol}" >/dev/null
docker run --detach --name "${pg}" --network "${net}" --network-alias postgres \
    --env POSTGRES_USER=postgres --env POSTGRES_PASSWORD="${db_password}" --env POSTGRES_DB="${db}" \
    --volume "${vol}:/var/lib/postgresql/data" \
    --volume "${repo_root}/infra/postgres/init:/docker-entrypoint-initdb.d:ro" \
    pgvector/pgvector:pg16 >/dev/null
for _ in $(seq 1 90); do
    docker logs "${pg}" 2>&1 | grep -q 'PostgreSQL init process complete' && break
    sleep 1
done
docker logs "${pg}" 2>&1 | grep -q 'PostgreSQL init process complete'

docker run --detach --name "${gw}" --network "${net}" --network-alias gateway -p "${gw_port}:8080" \
    --env ASPNETCORE_ENVIRONMENT=Production --env ASPNETCORE_URLS=http://+:8080 \
    --env "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=${db};Username=postgres;Password=${db_password};Pooling=true" \
    --env Jwt__Issuer=aiq.d7.test --env Jwt__Audience=aiq.d7.client \
    --env Jwt__SigningKey=d7-e2e-test-signing-key-needs-32-bytes-min \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://object.invalid:9000 --env Object__AccessKey=test --env Object__SecretKey=test-password \
    "${gw_image}" >/dev/null
for _ in $(seq 1 60); do
    curl -sf --max-time 2 "${gw_base}/healthz/ready" >/dev/null 2>&1 && break
    sleep 1
done
curl -sf "${gw_base}/healthz/ready" >/dev/null
echo "Gateway ready."

docker build -q -f "${repo_root}/web/Dockerfile" -t "${web_image}" "${repo_root}/web" >/dev/null
docker run --detach --name "${web}" --network "${net}" --network-alias web -p "${web_port}:3000" \
    --env GATEWAY_BASE_URL=http://gateway:8080 \
    --env AGENT_BASE_URL=http://agent.invalid:8000 \
    "${web_image}" >/dev/null
for _ in $(seq 1 60); do
    curl -sf --max-time 2 "${web_base}/login" >/dev/null 2>&1 && break
    sleep 1
done
curl -sf "${web_base}/login" >/dev/null
echo "Web ready."

echo "== Bootstrap: tenant, departments, roles, one capability-scoped agent =="
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO tenants(code,name,status) VALUES ('default','Default','active');
  INSERT INTO departments(tenant_id,code,name,status,is_default)
  SELECT id,'default','Default','active',true FROM tenants WHERE code='default';
" >/dev/null
req POST /auth/register "" '{"email":"root@d7test.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"
docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@d7test.local' -f /tmp/first-super-admin.sql >/dev/null
req POST /auth/login "" '{"email":"root@d7test.local","password":"RootPass123!"}'
super_token="$(jqf .token)"

req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-d7e2e","name":"Tenant A","ownerEmail":"owner-a@d7test.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
tenant_a="$(jqf .tenant.id)"
req POST /auth/login "" '{"email":"owner-a@d7test.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"
accounting_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@d7test.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "create Admin Accounting" 201 "${status}"
req POST /auth/login "" '{"email":"admin-accounting@d7test.local","password":"AdminPass123!"}'
admin_token="$(jqf .token)"
req POST /tenant/users "${admin_token}" '{"email":"user-accounting@d7test.local","password":"UserPass123!","displayName":"User Accounting"}'
expect_status "create User Accounting" 201 "${status}"
user_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='user-accounting@d7test.local'")"
req POST /auth/login "" '{"email":"user-accounting@d7test.local","password":"UserPass123!"}'
user_token="$(jqf .token)"

# One agent declaring only accounting.tax, so the USER's /me/capabilities
# resolves to exactly that one code — the minimal fixture needed to prove
# the accounting sub-nav shows the authorized tab and hides the rest
# (D4's ListForPrincipalAsync derives visibility strictly from the
# directly-assigned agent's own declared capability set).
req GET /system/capabilities "${super_token}" ""
tax_cap="$(echo "${body}" | jq -r '.items[]|select(.code=="accounting.tax")|.id')"
req POST /system/agents "${super_token}" '{"slug":"tax-only-agent-d7e2e","name":"Tax Only Agent","actionTier":"suggest"}'
agent_id="$(jqf .id)"
req GET "/system/agents/${agent_id}/versions" "${super_token}" ""
version_id="$(jqf '.items[0].id')"
req PATCH "/system/agents/${agent_id}/versions/${version_id}" "${super_token}" "{\"capabilityIds\":[\"${tax_cap}\"]}"
req POST "/system/agents/${agent_id}/versions/${version_id}/publish" "${super_token}" ""
req PUT "/system/agents/${agent_id}/tenants/${tenant_a}" "${super_token}" ""
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
req PUT "/tenant/agents/${agent_id}/users/${user_id}" "${admin_token}" ""
expect_status "USER directly assigned the tax-only agent" 204 "${status}"
req GET "/me/capabilities/accounting.tax/agent" "${user_token}" ""
expect_status "sanity: USER now resolves accounting.tax" 200 "${status}"

echo ""
echo "== §13 SUPER_ADMIN regression: every canonical platform page still reachable =="
# /admin/skills and /admin/audit call the Agent service directly (skills
# list, Agent-side audit log) rather than the Gateway — this isolated
# stack deliberately has no live Agent container (Agent__BaseUrl points at
# an unreachable host, same as D3-D6's Gateway-only E2Es), so those two
# 500 on a fetch failure regardless of any D7 change. Their route/role
# gating (unchanged by D7) is confirmed by reading
# admin/skills/layout.tsx and admin/audit/layout.tsx directly instead.
for path in /admin/agent-builder /admin/tenants /admin/users /admin/tools /admin/health /admin/agents; do
    webreq "${path}" "${super_token}"
    expect_status "SUPER_ADMIN GET ${path}" 200 "${status}"
done
webreq /admin/capabilities "${super_token}"
expect_status "SUPER_ADMIN GET new Nghiệp vụ Catalog page" 200 "${status}"
expect_true "catalog page lists accounting.tax" "$(body_contains 'accounting.tax')"

echo ""
echo "== D7 route-migration check: /admin/agents redirects non-super-admin to the canonical /admin/tenant-agents =="
webreq /admin/agents "${owner_a_token}"
expect_redirect_to "TENANT_OWNER GET /admin/agents redirects" "/admin/tenant-agents"
webreq /admin/agents "${admin_token}"
expect_redirect_to "ADMIN GET /admin/agents redirects" "/admin/tenant-agents"
webreq /admin/tenant-agents "${owner_a_token}"
expect_status "TENANT_OWNER GET canonical /admin/tenant-agents" 200 "${status}"
webreq /admin/tenant-agents "${admin_token}"
expect_status "ADMIN GET canonical /admin/tenant-agents" 200 "${status}"

echo ""
echo "== §14 TENANT_OWNER regression: company/resources/operations pages reachable, platform pages denied =="
for path in /admin/tenant-departments /admin/tenant-users /admin/tenant-status; do
    webreq "${path}" "${owner_a_token}"
    expect_status "TENANT_OWNER GET ${path}" 200 "${status}"
done
for path in /admin/tenants /admin/users /admin/tools /admin/skills /admin/health /admin/agent-builder /admin/capabilities; do
    webreq "${path}" "${owner_a_token}"
    expect_redirect_to "TENANT_OWNER GET ${path} denied" "/forbidden"
done

echo ""
echo "== §15 ADMIN regression: department pages reachable, platform + tenant-wide pages denied =="
webreq /admin/tenant-users "${admin_token}"
expect_status "ADMIN GET Department Members (/admin/tenant-users)" 200 "${status}"
for path in /admin/tenants /admin/users /admin/tools /admin/skills /admin/health /admin/agent-builder /admin/capabilities /admin/tenant-departments; do
    webreq "${path}" "${admin_token}"
    expect_redirect_to "ADMIN GET ${path} denied" "/forbidden"
done
webreq /vouchers "${admin_token}"
expect_status "ADMIN GET /vouchers (unchanged)" 200 "${status}"

echo ""
echo "== ACL V2-F exact workspace/navigation + server-rendered route guards =="
webreq /system/models "${super_token}"
expect_status "SUPER_ADMIN reaches platform Models / Providers" 200 "${status}"
for path in /system/capabilities /system/skills /system/knowledge; do
    webreq "${path}" "${super_token}"
    expect_status "SUPER_ADMIN manages ${path}" 200 "${status}"
done
expect_true "SUPER_ADMIN nav has Platform management" "$(body_contains 'Platform')"
expect_true "SUPER_ADMIN nav hides AI Workspace" "$([[ "${body}" != *'AI Workspace'* ]] && echo true || echo false)"
webreq /department/agents "${super_token}"
expect_redirect_to "SUPER_ADMIN cannot open Department Agent UI" "/forbidden"

webreq /tenant/users "${owner_a_token}"
expect_redirect_to "TENANT_OWNER canonical Users route redirects to the existing company account UI" "/admin/tenant-users"
webreq /dashboard "${owner_a_token}"
expect_true "TENANT_OWNER nav has Company governance" "$(body_contains 'Department Admins')"
expect_true "TENANT_OWNER nav hides AI Workspace" "$([[ "${body}" != *'AI Workspace'* ]] && echo true || echo false)"
webreq /department/workflows "${owner_a_token}"
expect_redirect_to "TENANT_OWNER cannot open Department Workflow UI" "/forbidden"

for path in /department/agents /department/capabilities /department/workflows /department/skills /department/knowledge /department/tools /department/review; do
    webreq "${path}" "${admin_token}"
    expect_status "ADMIN GET ${path}" 200 "${status}"
done
expect_true "ADMIN nav has full AI Workspace" "$(body_contains 'AI Workspace')"
expect_true "ADMIN nav hides Platform management" "$([[ "${body}" != *'Platform Templates'* ]] && echo true || echo false)"
webreq /system/settings "${admin_token}"
expect_redirect_to "ADMIN cannot open Platform settings" "/forbidden"
webreq /system/skills "${admin_token}"
expect_redirect_to "ADMIN cannot mutate/browse through Platform management UI" "/forbidden"

webreq "/chat?agent=${agent_id}" "${user_token}"
expect_true "USER nav hides all management menus" "$([[ "${body}" != *'AI Workspace'* && "${body}" != *'>Department<'* && "${body}" != *'>Platform<'* && "${body}" != *'>Company<'* ]] && echo true || echo false)"
webreq /department/knowledge "${user_token}"
expect_redirect_to "USER cannot open Department Knowledge UI" "/forbidden"

echo ""
echo "== §11 dead-UI removal: legacy per-user legacy-agent-ACL route is actually gone =="
webreq "/admin/users/${user_id}/agents" "${admin_token}"
expect_status "removed route /admin/users/{id}/agents now 404s" 404 "${status}"

echo ""
echo "== D7 bug fix + §16 USER regression =="
webreq /vouchers "${user_token}"
expect_status "USER GET /vouchers (Web-gate bug fixed — backend already allowed this)" 200 "${status}"
webreq /my-agents "${user_token}"
expect_status "USER GET /my-agents" 200 "${status}"
webreq /chat "${user_token}"
expect_redirect_to "USER GET /chat with no ?agent= redirects to pick one (pre-existing D3 UX, unchanged)" "/my-agents"
webreq "/chat?agent=${agent_id}" "${user_token}"
expect_status "USER GET /chat?agent=<their own directly-assigned agent>" 200 "${status}"
for path in /dashboard /admin/tenant-users /admin/tenants /admin/capabilities; do
    webreq "${path}" "${user_token}"
    expect_redirect_to "USER GET ${path} denied (no management UI)" "/forbidden"
done

echo ""
echo "== §7/§17 capability-driven Nghiệp vụ nav: USER sees only the accounting tab they're authorized for =="
webreq /accounting "${user_token}"
expect_status "USER GET /accounting" 200 "${status}"
expect_true "shows authorized accounting.tax tab" "$(body_contains '/accounting/tax-calc')"
expect_true "hides unauthorized accounting.journal tab" "$([[ "${body}" != *'href="/accounting/journal"'* ]] && echo true || echo false)"
expect_true "hides unauthorized accounting.invoice tab" "$([[ "${body}" != *'href="/accounting/invoice"'* ]] && echo true || echo false)"
expect_true "hides unauthorized accounting.report tab" "$([[ "${body}" != *'href="/accounting/reports"'* ]] && echo true || echo false)"
expect_true "always-on Deadline tab still shown" "$(body_contains '/accounting/deadline')"

echo ""
echo "== unauthenticated: canonical login gate unchanged =="
webreq /dashboard ""
expect_redirect_to "no session GET /dashboard redirects to login" "/login"

echo ""
echo "================================================================"
echo "D7 E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
