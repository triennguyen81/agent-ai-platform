#!/usr/bin/env bash
# Phase D4 real HTTP E2E: business capability catalog, version-scoped
# declarations, department capability binding, and the capability->agent
# resolver.
#
# Boots an isolated, empty Postgres + freshly built Gateway image (own
# network/volume, never touches the shared dev stack) and drives the exact
# §13 scenario end to end over real HTTP with real JWTs: SUPER_ADMIN
# publishes an Accounting Agent version declaring accounting.journal,
# assigns it to Tenant A, TENANT_OWNER assigns it to the Accounting
# department and binds accounting.journal -> Accounting Agent, ADMIN
# Accounting resolves and "executes" (manifest issuance) without a direct
# assignment, USER Accounting needs the full chain, then department
# revoke/restore and a cross-department attempt are verified.
#
# "Validate succeeds" / "AI suggestion succeeds" are verified the same way
# D3's E2E verified "Chat succeeds": GET /me/capabilities/{code}/agent
# resolving, followed by GET /runtime/agents/{id}/manifest issuing a full
# manifest — that pipeline is exactly what agent/src/aiq_agent/api/routes.py
# calls (resolve_capability_agent -> require_agent_execution) before ever
# reaching a provider, so a successful manifest at the resolved agent id IS
# "the capability executes". No LLM provider call is made (same scope as
# the D3 E2E).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-d4-e2e-net-${run_id}"
pg="aiq-d4-e2e-postgres-${run_id}"
gw="aiq-d4-e2e-gateway-${run_id}"
vol="aiq-d4-e2e-pgdata-${run_id}"
db="aiq_d4_e2e"
db_password="d4_e2e_password"
image="aiq-agentic-gateway:d4test"
base="http://localhost:18081"
gw_port=18081

pass=0
fail=0
status=""
body=""

cleanup() {
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

req() {
    local method="$1" path="$2" token="${3:-}" req_payload="${4:-}"
    local args=(-sS -o /tmp/d4e2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/d4e2e_body.$$)"
    rm -f /tmp/d4e2e_body.$$
}

jqf() { echo "${body}" | jq -r "$1"; }

echo "== D4 E2E: booting isolated stack =="
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
    --env Jwt__Issuer=aiq.d4.test --env Jwt__Audience=aiq.d4.client \
    --env Jwt__SigningKey=d4-e2e-test-signing-key-needs-32-bytes-min \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://object.invalid:9000 --env Object__AccessKey=test --env Object__SecretKey=test-password \
    "${image}" >/dev/null

for _ in $(seq 1 60); do
    curl -sf --max-time 2 "${base}/healthz/ready" >/dev/null 2>&1 && break
    sleep 1
done
curl -sf "${base}/healthz/ready" >/dev/null
echo "Gateway ready."

echo "== Bootstrap: seed 'default' tenant (+ its default department), register+promote the first super_admin =="
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO tenants(code,name,status) VALUES ('default','Default','active');
  INSERT INTO departments(tenant_id,code,name,status,is_default)
  SELECT id,'default','Default','active',true FROM tenants WHERE code='default';
" >/dev/null

req POST /auth/register "" '{"email":"root@d4test.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"

docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@d4test.local' -f /tmp/first-super-admin.sql >/dev/null

req POST /auth/login "" '{"email":"root@d4test.local","password":"RootPass123!"}'
expect_status "login as super_admin" 200 "${status}"
super_token="$(jqf .token)"

echo "== Step 1-2: SUPER_ADMIN creates+publishes Accounting Agent declaring accounting.journal, assigns to Tenant A =="
req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-d4e2e","name":"Tenant A","ownerEmail":"owner-a@d4test.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
expect_status "onboard Tenant A" 201 "${status}"
tenant_a="$(jqf .tenant.id)"

req GET /system/capabilities "${super_token}" ""
expect_status "super_admin lists capability catalog" 200 "${status}"
journal_capability_id="$(echo "${body}" | jq -r '.items[] | select(.code=="accounting.journal") | .id')"
[[ -n "${journal_capability_id}" && "${journal_capability_id}" != "null" ]] && ok "accounting.journal exists in the seeded catalog" || bad "accounting.journal missing from catalog"
invoice_capability_id="$(echo "${body}" | jq -r '.items[] | select(.code=="accounting.invoice") | .id')"

req POST /system/agents "${super_token}" '{"slug":"accounting-agent-d4e2e","name":"Accounting Agent","actionTier":"suggest"}'
expect_status "create Accounting Agent" 201 "${status}"
agent_id="$(jqf .id)"

req GET "/system/agents/${agent_id}/versions" "${super_token}" ""
draft_version_id="$(jqf '.items[0].id')"

req PATCH "/system/agents/${agent_id}/versions/${draft_version_id}" "${super_token}" "{\"capabilityIds\":[\"${journal_capability_id}\",\"${invoice_capability_id}\"]}"
expect_status "assign accounting.journal + accounting.invoice to the draft" 200 "${status}"
expect_true "draft version now declares both capabilities" "$(echo "${body}" | jq -r --arg j "${journal_capability_id}" --arg i "${invoice_capability_id}" '(.capabilityIds|sort) == ([$j,$i]|sort)')"

req POST "/system/agents/${agent_id}/versions/${draft_version_id}/publish" "${super_token}" ""
expect_status "publish version" 200 "${status}"
expect_true "published version still carries accounting.journal" "$(echo "${body}" | jq -r --arg j "${journal_capability_id}" '.capabilityIds | index($j) != null')"

req PUT "/system/agents/${agent_id}/tenants/${tenant_a}" "${super_token}" ""
expect_status "assign Accounting Agent to Tenant A" 204 "${status}"

echo "== §Agent-Builder: publish freezes the capability set =="
req PATCH "/system/agents/${agent_id}/versions/${draft_version_id}" "${super_token}" "{\"capabilityIds\":[]}"
[[ "${status}" == "404" || "${status}" == "409" ]] && ok "cannot edit capabilities of a published version (HTTP ${status})" || bad "expected published-version edit to be rejected, got ${status}"

echo "== Step: seed Sales department (no Department-CRUD endpoint yet) =="
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status)
  SELECT id,'sales-d4e2e','Sales','active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
accounting_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
sales_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-d4e2e'")"

req POST /auth/login "" '{"email":"owner-a@d4test.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"

echo "== Step 3: TENANT_OWNER assigns Accounting Agent to Accounting dept; binding is now ADMIN's job (ACL V2-D) =="
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner assigns Accounting Agent to Accounting dept" 204 "${status}"

req GET "/tenant/departments/${accounting_dept}/capabilities" "${owner_a_token}" ""
expect_status "owner lists Accounting dept's capabilities (oversight, read-only)" 200 "${status}"
expect_true "accounting.journal appears with Accounting Agent as sole eligible agent" \
  "$(echo "${body}" | jq -r --arg id "${agent_id}" '[.items[]|select(.code=="accounting.journal")][0].eligibleAgents | map(.agentId) == [$id]')"

echo "== ACL V2-D: binding mutation moved off TENANT_OWNER entirely =="
req PUT "/tenant/departments/${accounting_dept}/capabilities/${journal_capability_id}/binding" "${owner_a_token}" "{\"agentId\":\"${agent_id}\"}"
expect_status "TENANT_OWNER's old binding route is gone (route removed, not just denied)" 404 "${status}"

echo "== Step 4-5: ADMIN Accounting created, binds accounting.journal -> Accounting Agent itself, then sees + resolves + 'executes' it, no direct assignment =="
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@d4test.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@d4test.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "owner creates Admin Sales" 201 "${status}"

req POST /auth/login "" '{"email":"admin-accounting@d4test.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
req POST /auth/login "" '{"email":"admin-sales@d4test.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"

req PUT "/department/capabilities/${journal_capability_id}/binding" "${admin_accounting_token}" "{\"agentId\":\"${agent_id}\"}"
expect_status "ADMIN Accounting binds accounting.journal -> its own Accounting Agent" 204 "${status}"
req PUT "/department/capabilities/${journal_capability_id}/binding" "${admin_accounting_token}" "{\"agentId\":\"${agent_id}\"}"
expect_status "repeated bind is idempotent" 204 "${status}"

echo "== §Binding validation: cross-department agent is not eligible for Sales's own binding =="
req PUT "/department/capabilities/${journal_capability_id}/binding" "${admin_sales_token}" "{\"agentId\":\"${agent_id}\"}"
[[ "${status}" == "400" ]] && ok "Admin Sales binding the Accounting-only agent to its own dept is rejected (HTTP 400)" || bad "expected 400, got ${status}"

req GET /me/capabilities "${admin_accounting_token}" ""
expect_status "Admin Accounting GET /me/capabilities" 200 "${status}"
expect_true "Nghiệp vụ menu contains accounting.journal for Admin Accounting" \
  "$(echo "${body}" | jq -r '[.items[]|select(.code=="accounting.journal")]|length==1')"

req GET "/me/capabilities/accounting.journal/agent" "${admin_accounting_token}" ""
expect_status "Admin Accounting resolves accounting.journal" 200 "${status}"
expect_true "resolves to the bound Accounting Agent" "$([[ "$(jqf .agentId)" == "${agent_id}" ]] && echo true || echo false)"

req GET "/runtime/agents/${agent_id}/manifest" "${admin_accounting_token}" ""
expect_status "Admin Accounting manifest for resolved agent succeeds (no direct assignment)" 200 "${status}"

echo "== Step 6: cross-department — Admin Sales cannot see or resolve accounting.journal =="
req POST /auth/login "" '{"email":"admin-sales@d4test.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"
req GET /me/capabilities "${admin_sales_token}" ""
expect_true "Admin Sales' Nghiệp vụ menu does NOT contain accounting.journal" \
  "$(echo "${body}" | jq -r '[.items[]|select(.code=="accounting.journal")]|length==0')"
req GET "/me/capabilities/accounting.journal/agent" "${admin_sales_token}" ""
expect_status "Admin Sales cannot resolve accounting.journal" 403 "${status}"

echo "== Step 7-8: ADMIN assigns Accounting Agent to USER; USER resolves + 'executes' accounting.journal =="
req POST /tenant/users "${admin_accounting_token}" '{"email":"user-accounting@d4test.local","password":"UserPass123!","displayName":"User Accounting"}'
expect_status "Admin Accounting creates User Accounting" 201 "${status}"
user_accounting_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='user-accounting@d4test.local'")"

req PUT "/tenant/agents/${agent_id}/users/${user_accounting_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Accounting Agent to User Accounting" 204 "${status}"

req POST /auth/login "" '{"email":"user-accounting@d4test.local","password":"UserPass123!"}'
user_accounting_token="$(jqf .token)"

req GET /me/capabilities "${user_accounting_token}" ""
expect_true "User Accounting's Nghiệp vụ menu contains accounting.journal" \
  "$(echo "${body}" | jq -r '[.items[]|select(.code=="accounting.journal")]|length==1')"

req GET "/me/capabilities/accounting.journal/agent" "${user_accounting_token}" ""
expect_status "User Accounting resolves accounting.journal (full chain present)" 200 "${status}"
req GET "/runtime/agents/${agent_id}/manifest" "${user_accounting_token}" ""
expect_status "User Accounting manifest succeeds — Validate/AI-suggestion would execute" 200 "${status}"

echo "== §11 security: USER cannot spoof capability/agent to bypass authorization =="
req GET "/me/capabilities/supplier.management/agent" "${user_accounting_token}" ""
expect_status "User Accounting cannot resolve a capability no assigned agent supports" 403 "${status}"
random_agent_id="00000000-0000-0000-0000-000000000099"
req GET "/runtime/agents/${random_agent_id}/manifest" "${user_accounting_token}" ""
expect_status "User Accounting cannot spoof an unrelated agent id directly" 403 "${status}"

echo "== Step 9-10: revoke department entitlement; ADMIN + USER capability execution both denied; restore =="
req DELETE "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner revokes department entitlement" 204 "${status}"

req GET "/me/capabilities/accounting.journal/agent" "${admin_accounting_token}" ""
expect_status "Admin Accounting capability resolution denied after department revoke" 403 "${status}"
req GET "/me/capabilities/accounting.journal/agent" "${user_accounting_token}" ""
expect_status "User Accounting capability resolution denied after department revoke" 403 "${status}"
req GET /me/capabilities "${admin_accounting_token}" ""
expect_true "accounting.journal gone from Admin Accounting's Nghiệp vụ menu" \
  "$(echo "${body}" | jq -r '[.items[]|select(.code=="accounting.journal")]|length==0')"

req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner restores department entitlement" 204 "${status}"
req GET "/me/capabilities/accounting.journal/agent" "${admin_accounting_token}" ""
expect_status "Admin Accounting capability resolution recovers immediately" 200 "${status}"

echo ""
echo "== §10 audit: capability resolution recorded, no secrets =="
audit_row="$(docker exec "${pg}" psql -At -F'|' -U postgres -d "${db}" -c "
  SELECT action, resource_type, resource_id, payload::text
  FROM audit_log
  WHERE actor_id='$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='admin-accounting@d4test.local'")'
    AND action LIKE 'GET capability agent resolution%'
  ORDER BY created_at ASC LIMIT 1
")"
echo "  sample audit row: ${audit_row}"
if echo "${audit_row}" | grep -q "capability agent resolution" && echo "${audit_row}" | grep -q "accounting.journal"; then
  ok "audit row records capability resolution with the capability code"
else
  bad "audit row missing expected action/resource id: ${audit_row}"
fi
if echo "${audit_row}" | grep -qiE "eyJ|bearer|sk-ant|api[_-]?key"; then
  bad "audit row appears to contain a token/secret-shaped value"
else
  ok "audit row contains no JWT/token/secret-shaped value"
fi

echo ""
echo "================================================================"
echo "D4 E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
