#!/usr/bin/env bash
# Phase D3 real HTTP E2E: department-scoped agent entitlement + execution.
#
# Boots an isolated, empty Postgres + freshly built Gateway image on their
# own docker network (never touches the shared dev stack's data) and drives
# the exact scenario from the D3 audit spec end to end over real HTTP with
# real JWTs: TENANT_OWNER assigns a tenant-entitled agent to a department,
# ADMIN executes it with ZERO direct agent_assignments rows, ADMIN assigns
# it on to a USER, then department/tenant revocation is verified to cut off
# both ADMIN and USER access, and restoring the department entitlement is
# verified to behave exactly as IDynamicAgentRepository.RevokeDepartmentAgentAsync's
# transactional cascade predicts (USER's direct assignment does NOT come
# back automatically — it was revoked, not merely made ineffective).
#
# "Chat succeeds" is verified as: GET /runtime/agents/{id}/manifest returns
# 200 with a full manifest (system_prompt/model/tools/ToolExecutionToken) —
# manifest issuance is the actual, sole authorization gate the Agent service
# calls before ever reaching an LLM provider (see require_agent_execution /
# runtime/manifest.py), so a successful manifest IS "chat would succeed".
# This script does not drive a real LLM call (same scope as the D2 E2E: HTTP
# authorization only, not provider execution).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-d3-e2e-net-${run_id}"
pg="aiq-d3-e2e-postgres-${run_id}"
gw="aiq-d3-e2e-gateway-${run_id}"
vol="aiq-d3-e2e-pgdata-${run_id}"
db="aiq_d3_e2e"
db_password="d3_e2e_password"
image="aiq-agentic-gateway:d3test"
base="http://localhost:18080"
gw_port=18080

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

# req METHOD PATH TOKEN REQUEST_BODY -> sets globals $status and $body (the response)
req() {
    local method="$1" path="$2" token="${3:-}" req_payload="${4:-}"
    local args=(-sS -o /tmp/d3e2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/d3e2e_body.$$)"
    rm -f /tmp/d3e2e_body.$$
}

jqf() { echo "${body}" | jq -r "$1"; }

echo "== D3 E2E: booting isolated stack =="
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
    --env Jwt__Issuer=aiq.d3.test --env Jwt__Audience=aiq.d3.client \
    --env Jwt__SigningKey=d3-e2e-test-signing-key-needs-32-bytes-min \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://object.invalid:9000 --env Object__AccessKey=test --env Object__SecretKey=test-password \
    "${image}" >/dev/null

for _ in $(seq 1 60); do
    curl -sf --max-time 2 "${base}/healthz/ready" >/dev/null 2>&1 && break
    sleep 1
done
curl -sf "${base}/healthz/ready" >/dev/null
echo "Gateway ready."

echo "== Bootstrap: seed 'default' tenant (+ its required default department), register+promote the first super_admin =="
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO tenants(code,name,status) VALUES ('default','Default','active');
  INSERT INTO departments(tenant_id,code,name,status,is_default)
  SELECT id,'default','Default','active',true FROM tenants WHERE code='default';
" >/dev/null

req POST /auth/register "" '{"email":"root@d3test.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"

docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@d3test.local' -f /tmp/first-super-admin.sql >/dev/null

req POST /auth/login "" '{"email":"root@d3test.local","password":"RootPass123!"}'
expect_status "login as super_admin" 200 "${status}"
super_token="$(jqf .token)"

echo "== Step 1-2: SUPER_ADMIN onboards Tenant A, creates+publishes Accounting Agent, assigns it to Tenant A =="
req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-d3e2e","name":"Tenant A","ownerEmail":"owner-a@d3test.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
expect_status "onboard Tenant A" 201 "${status}"
tenant_a="$(jqf .tenant.id)"

req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-b-d3e2e","name":"Tenant B","ownerEmail":"owner-b@d3test.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner B"}'
tenant_b="$(jqf .tenant.id)"

req POST /system/agents "${super_token}" '{"slug":"accounting-agent-d3e2e","name":"Accounting Agent","actionTier":"suggest"}'
expect_status "create Accounting Agent" 201 "${status}"
agent_id="$(jqf .id)"

# agents get an auto-created draft v1 (migration 008's create_initial_agent_version
# trigger, AFTER INSERT ON agents) — a second explicit POST .../versions would
# collide with uq_agent_versions_one_draft (409 agent_already_has_draft), so
# publish that trigger-created draft directly; its defaults are already
# publish-valid (non-blank system_prompt, provider in the allowed set, no
# tool grants to validate).
req GET "/system/agents/${agent_id}/versions" "${super_token}" ""
expect_status "list auto-created draft version" 200 "${status}"
version_id="$(jqf '.items[0].id')"

req POST "/system/agents/${agent_id}/versions/${version_id}/publish" "${super_token}" ""
expect_status "publish version" 200 "${status}"
expect_true "agent is production after publish" "$([[ "$(jqf .status)" == published ]] && echo true || echo false)"

req PUT "/system/agents/${agent_id}/tenants/${tenant_a}" "${super_token}" ""
expect_status "assign Accounting Agent to Tenant A" 204 "${status}"

echo "== Step: seed Sales department (no Department-CRUD endpoint exists yet — direct SQL, same as the D3 test fixtures) =="
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status)
  SELECT id,'sales-d3e2e','Sales',  'active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
accounting_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
sales_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-d3e2e'")"

req POST /auth/login "" '{"email":"owner-a@d3test.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"

echo "== Step 3: TENANT_OWNER A assigns Accounting Agent to Accounting Department =="
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner assigns agent to Accounting dept" 204 "${status}"
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "repeated PUT is idempotent" 204 "${status}"

echo "== §6: cross-tenant / non-entitled / role-restriction checks on the owner-only mutation =="
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "" ""
expect_status "no token -> 401" 401 "${status}"

req POST /auth/login "" '{"email":"owner-b@d3test.local","password":"OwnerPass123!"}'
owner_b_token="$(jqf .token)"
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_b_token}" ""
# 400 active_department_required: Owner B's own tenant (Tenant B) never has a
# department row matching accounting_dept's (tenant_id, id) pair, so
# DepartmentRepository.FindAsync(tenantB, accounting_dept) comes back null —
# still an unconditional deny, just via a 400 rather than 403/404.
[[ "${status}" == "400" || "${status}" == "404" || "${status}" == "403" ]] && ok "Owner B cannot touch Tenant A's department (HTTP ${status})" || bad "Owner B cross-tenant department mutation (got ${status})"

req GET "/tenant/departments/${sales_dept}/agents" "${owner_a_token}" ""
expect_status "owner lists Sales dept agents (not yet assigned)" 200 "${status}"

echo "== Step 4-5: Admin Accounting exists with ZERO direct agent_assignments; Chat list + manifest succeed =="
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@d3test.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting" 201 "${status}"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@d3test.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "owner creates Admin Sales" 201 "${status}"

req POST /auth/login "" '{"email":"admin-accounting@d3test.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
admin_accounting_id="$(jqf .userId)"

zero_direct="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT count(*) FROM agent_assignments WHERE user_id='${admin_accounting_id}'")"
[[ "${zero_direct}" == "0" ]] && ok "Admin Accounting has zero direct agent_assignments rows" || bad "Admin Accounting unexpectedly has ${zero_direct} direct assignment rows"

req GET /me/agents "${admin_accounting_token}" ""
expect_status "Admin Accounting GET /me/agents" 200 "${status}"
expect_true "Chat list contains Accounting Agent for Admin Accounting" \
  "$(echo "${body}" | jq -r --arg id "${agent_id}" '[.items[]|select(.agentId==$id)]|length==1')"

req GET "/runtime/agents/${agent_id}/manifest" "${admin_accounting_token}" ""
expect_status "Admin Accounting manifest (no direct assignment needed)" 200 "${status}"
expect_true "manifest carries a minted ToolExecutionToken" "$([[ -n "$(jqf .toolExecutionToken)" && "$(jqf .toolExecutionToken)" != null ]] && echo true || echo false)"

echo "== Step 6: Admin Sales cannot see or execute the Accounting Agent, even naming its known id =="
req POST /auth/login "" '{"email":"admin-sales@d3test.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"

req GET /me/agents "${admin_sales_token}" ""
expect_true "Admin Sales' Chat list does NOT contain Accounting Agent" \
  "$(echo "${body}" | jq -r --arg id "${agent_id}" '[.items[]|select(.agentId==$id)]|length==0')"

req GET "/runtime/agents/${agent_id}/manifest" "${admin_sales_token}" ""
expect_status "Admin Sales direct manifest call on known Accounting agent id -> denied" 403 "${status}"
expect_true "denial reason is department-scoped, not a bare not-found" "$([[ "$(jqf .error)" == agent_not_assigned_to_department ]] && echo true || echo false)"

echo "== Step 7-8: Admin Accounting assigns the agent to User Accounting; USER chat succeeds =="
req POST /tenant/users "${admin_accounting_token}" '{"email":"user-accounting@d3test.local","password":"UserPass123!","displayName":"User Accounting"}'
expect_status "Admin Accounting creates User Accounting (department server-assigned)" 201 "${status}"
user_accounting_id="$(jqf .id // .userId)"
[[ "${user_accounting_id}" == "null" || -z "${user_accounting_id}" ]] && user_accounting_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='user-accounting@d3test.local'")"

req PUT "/tenant/agents/${agent_id}/users/${user_accounting_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns agent to User Accounting" 204 "${status}"

req POST /auth/login "" '{"email":"user-accounting@d3test.local","password":"UserPass123!"}'
user_accounting_token="$(jqf .token)"

req GET /me/agents "${user_accounting_token}" ""
expect_true "My Agents contains Accounting Agent for User Accounting" \
  "$(echo "${body}" | jq -r --arg id "${agent_id}" '[.items[]|select(.agentId==$id)]|length==1')"

req GET "/runtime/agents/${agent_id}/manifest" "${user_accounting_token}" ""
expect_status "User Accounting manifest (full chain present)" 200 "${status}"

echo "== §4: USER direct execution attempt cannot bypass the chain with a spoofed/unrelated agent id =="
random_agent_id="00000000-0000-0000-0000-000000000099"
req GET "/runtime/agents/${random_agent_id}/manifest" "${user_accounting_token}" ""
expect_status "User Accounting cannot spoof an unrelated agent id" 403 "${status}"

echo "== §6: neither ADMIN nor USER can reach the TENANT_OWNER-only department-agent mutation route =="
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${admin_accounting_token}" ""
expect_status "ADMIN cannot call the owner-only assign route (policy-level, not app-logic)" 403 "${status}"
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${user_accounting_token}" ""
expect_status "USER cannot call the owner-only assign route" 403 "${status}"

echo "== Step 9-10: revoke Department entitlement; verify ADMIN + USER both denied, agent disappears from listings =="
req DELETE "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner revokes department entitlement" 204 "${status}"

req GET "/runtime/agents/${agent_id}/manifest" "${admin_accounting_token}" ""
expect_status "Admin Accounting execution denied after department revoke" 403 "${status}"
req GET /me/agents "${admin_accounting_token}" ""
expect_true "Accounting Agent gone from Admin Accounting's Chat list" \
  "$(echo "${body}" | jq -r --arg id "${agent_id}" '[.items[]|select(.agentId==$id)]|length==0')"

req GET "/runtime/agents/${agent_id}/manifest" "${user_accounting_token}" ""
expect_status "User Accounting execution denied after department revoke" 403 "${status}"
req GET /me/agents "${user_accounting_token}" ""
expect_true "Accounting Agent gone from User Accounting's My Agents" \
  "$(echo "${body}" | jq -r --arg id "${agent_id}" '[.items[]|select(.agentId==$id)]|length==0')"

user_assignment_status="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT status FROM agent_assignments WHERE user_id='${user_accounting_id}' AND agent_id='${agent_id}'")"
[[ "${user_assignment_status}" == "revoked" ]] && ok "§5: department revoke cascaded to revoke (not merely ignore) the direct USER assignment, history preserved" \
  || bad "expected the direct assignment to be revoked by cascade, found status='${user_assignment_status}'"

echo "== Step 11: restore department entitlement; verify ADMIN recovers automatically, USER does NOT (cascade revoked history, by design) =="
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner restores department entitlement" 204 "${status}"

req GET "/runtime/agents/${agent_id}/manifest" "${admin_accounting_token}" ""
expect_status "Admin Accounting execution recovers immediately (department chain only)" 200 "${status}"

req GET "/runtime/agents/${agent_id}/manifest" "${user_accounting_token}" ""
expect_status "User Accounting stays denied (direct assignment was revoked, not just ineffective)" 403 "${status}"
expect_true "denial reason confirms the direct assignment, not the department, is now missing" "$([[ "$(jqf .error)" == agent_not_assigned ]] && echo true || echo false)"

echo "== Step 12: revoke Tenant entitlement; verify all descendant access fails (ADMIN + USER) =="
req DELETE "/system/agents/${agent_id}/tenants/${tenant_a}" "${super_token}" ""
expect_status "super_admin revokes tenant entitlement" 204 "${status}"

req GET "/runtime/agents/${agent_id}/manifest" "${admin_accounting_token}" ""
expect_status "Admin Accounting denied after tenant revoke" 403 "${status}"
expect_true "denial reason is tenant-level, cascaded above department" "$([[ "$(jqf .error)" == agent_not_assigned_to_tenant ]] && echo true || echo false)"
req GET "/runtime/agents/${agent_id}/manifest" "${user_accounting_token}" ""
expect_status "User Accounting denied after tenant revoke" 403 "${status}"

echo ""
echo "== §10: audit trail sanity — successful admin execution recorded the right fields, no secrets =="
audit_row="$(docker exec "${pg}" psql -At -F'|' -U postgres -d "${db}" -c "
  SELECT action, resource_type, resource_id, payload::text
  FROM audit_log
  WHERE actor_id='${admin_accounting_id}' AND action LIKE 'GET runtime agent manifest%'
  ORDER BY created_at ASC LIMIT 1
")"
echo "  sample audit row: ${audit_row}"
if echo "${audit_row}" | grep -q "admin department execution" && echo "${audit_row}" | grep -q "${agent_id}"; then
  ok "audit row records the admin-department-execution event type and agent id"
else
  bad "audit row missing expected event type / agent id: ${audit_row}"
fi
if echo "${audit_row}" | grep -qiE "eyJ|bearer|sk-ant|api[_-]?key"; then
  bad "audit row appears to contain a token/secret-shaped value"
else
  ok "audit row contains no JWT/token/secret-shaped value"
fi

echo ""
echo "================================================================"
echo "D3 E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
