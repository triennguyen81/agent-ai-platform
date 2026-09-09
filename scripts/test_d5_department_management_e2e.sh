#!/usr/bin/env bash
# Phase D5 real HTTP E2E: department CRUD, TENANT_OWNER department move
# (transactional agent-assignment cleanup), and the promote/demote ->
# department-execution interaction, reusing D3's agent entitlement chain
# and D4's capability resolver unchanged.
#
# Boots an isolated, empty Postgres + freshly built Gateway image (own
# network/volume, never touches the shared dev stack) and drives the exact
# §18 scenario end to end over real HTTP with real JWTs.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-d5-e2e-net-${run_id}"
pg="aiq-d5-e2e-postgres-${run_id}"
gw="aiq-d5-e2e-gateway-${run_id}"
vol="aiq-d5-e2e-pgdata-${run_id}"
db="aiq_d5_e2e"
db_password="d5_e2e_password"
image="aiq-agentic-gateway:d5test"
base="http://localhost:18082"
gw_port=18082

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
    local args=(-sS -o /tmp/d5e2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/d5e2e_body.$$)"
    rm -f /tmp/d5e2e_body.$$
}

jqf() { echo "${body}" | jq -r "$1"; }

echo "== D5 E2E: booting isolated stack =="
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
    --env Jwt__Issuer=aiq.d5.test --env Jwt__Audience=aiq.d5.client \
    --env Jwt__SigningKey=d5-e2e-test-signing-key-needs-32-bytes-min \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://object.invalid:9000 --env Object__AccessKey=test --env Object__SecretKey=test-password \
    "${image}" >/dev/null

for _ in $(seq 1 60); do
    curl -sf --max-time 2 "${base}/healthz/ready" >/dev/null 2>&1 && break
    sleep 1
done
curl -sf "${base}/healthz/ready" >/dev/null
echo "Gateway ready."

echo "== Bootstrap =="
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO tenants(code,name,status) VALUES ('default','Default','active');
  INSERT INTO departments(tenant_id,code,name,status,is_default)
  SELECT id,'default','Default','active',true FROM tenants WHERE code='default';
" >/dev/null

req POST /auth/register "" '{"email":"root@d5test.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"
docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@d5test.local' -f /tmp/first-super-admin.sql >/dev/null

req POST /auth/login "" '{"email":"root@d5test.local","password":"RootPass123!"}'
super_token="$(jqf .token)"

req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-d5e2e","name":"Tenant A","ownerEmail":"owner-a@d5test.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
expect_status "onboard Tenant A" 201 "${status}"
tenant_a="$(jqf .tenant.id)"
req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-b-d5e2e","name":"Tenant B","ownerEmail":"owner-b@d5test.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner B"}'
tenant_b="$(jqf .tenant.id)"

req POST /auth/login "" '{"email":"owner-a@d5test.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"
req POST /auth/login "" '{"email":"owner-b@d5test.local","password":"OwnerPass123!"}'
owner_b_token="$(jqf .token)"

echo "== §2/§3: Department CRUD =="
req POST /tenant/departments "${owner_a_token}" '{"code":"accounting","name":"Accounting"}'
expect_status "owner creates Accounting" 201 "${status}"
accounting_dept="$(jqf .id)"
req POST /tenant/departments "${owner_a_token}" '{"code":"sales","name":"Sales"}'
expect_status "owner creates Sales" 201 "${status}"
sales_dept="$(jqf .id)"

req POST /tenant/departments "${owner_a_token}" '{"code":"accounting","name":"Duplicate"}'
expect_status "duplicate code within tenant rejected" 409 "${status}"

req POST /tenant/departments "${owner_b_token}" '{"code":"accounting","name":"Accounting"}'
expect_status "same code allowed in a DIFFERENT tenant" 201 "${status}"

req GET "/tenant/departments/${accounting_dept}" "${owner_a_token}" ""
expect_status "owner reads own department" 200 "${status}"
req GET "/tenant/departments/${accounting_dept}" "${owner_b_token}" ""
expect_status "cross-tenant department read denied" 404 "${status}"

req PATCH "/tenant/departments/${accounting_dept}" "${owner_a_token}" '{"name":"Kế toán"}'
expect_status "owner renames own department" 200 "${status}"
expect_true "rename took effect" "$([[ "$(jqf .name)" == "Kế toán" ]] && echo true || echo false)"
req PATCH "/tenant/departments/${accounting_dept}" "${owner_b_token}" '{"name":"Hijacked"}'
expect_status "cross-tenant rename denied" 404 "${status}"

echo "== §4: default department can never be deactivated =="
default_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
req POST "/tenant/departments/${default_dept}/deactivate" "${owner_a_token}" ""
expect_status "cannot deactivate the tenant's default department" 409 "${status}"
expect_true "reason names the default-department constraint" "$([[ "$(jqf .error)" == cannot_deactivate_default_department ]] && echo true || echo false)"

echo "== Setup: SUPER_ADMIN publishes Accounting Agent + Sales Agent, entitles both to Tenant A =="
create_and_publish_agent() {
    local slug="$1" name="$2"
    req POST /system/agents "${super_token}" "{\"slug\":\"${slug}\",\"name\":\"${name}\",\"actionTier\":\"suggest\"}"
    local aid; aid="$(jqf .id)"
    req GET "/system/agents/${aid}/versions" "${super_token}" ""
    local vid; vid="$(jqf '.items[0].id')"
    req POST "/system/agents/${aid}/versions/${vid}/publish" "${super_token}" ""
    req PUT "/system/agents/${aid}/tenants/${tenant_a}" "${super_token}" ""
    echo "${aid}"
}
accounting_agent="$(create_and_publish_agent accounting-agent-d5e2e "Accounting Agent")"
sales_agent="$(create_and_publish_agent sales-agent-d5e2e "Sales Agent")"

req PUT "/tenant/departments/${accounting_dept}/agents/${accounting_agent}" "${owner_a_token}" ""
expect_status "owner assigns Accounting Agent to Accounting" 204 "${status}"
req PUT "/tenant/departments/${sales_dept}/agents/${sales_agent}" "${owner_a_token}" ""
expect_status "owner assigns Sales Agent to Sales" 204 "${status}"

echo "== Step 2: owner creates Admin Accounting, User Accounting, Admin Sales =="
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@d5test.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@d5test.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "owner creates Admin Sales" 201 "${status}"

req POST /auth/login "" '{"email":"admin-accounting@d5test.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
req POST /tenant/users "${admin_accounting_token}" '{"email":"user-accounting@d5test.local","password":"UserPass123!","displayName":"User Accounting"}'
expect_status "Admin Accounting creates User Accounting" 201 "${status}"
user_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='user-accounting@d5test.local'")"

echo "== Step 4: Admin Accounting sees/uses Accounting Agent; Admin Sales does not =="
req POST /auth/login "" '{"email":"admin-sales@d5test.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"
req GET "/runtime/agents/${accounting_agent}/manifest" "${admin_accounting_token}" ""
expect_status "Admin Accounting executes Accounting Agent (no direct assignment)" 200 "${status}"
req GET "/runtime/agents/${accounting_agent}/manifest" "${admin_sales_token}" ""
expect_status "Admin Sales cannot execute Accounting Agent" 403 "${status}"

echo "== Step 5: owner/admin assigns Accounting Agent to User Accounting; USER can use it =="
req PUT "/tenant/agents/${accounting_agent}/users/${user_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Accounting Agent to User Accounting" 204 "${status}"
req POST /auth/login "" '{"email":"user-accounting@d5test.local","password":"UserPass123!"}'
user_token="$(jqf .token)"
req GET "/runtime/agents/${accounting_agent}/manifest" "${user_token}" ""
expect_status "User Accounting executes Accounting Agent before move" 200 "${status}"

echo "== §16 security: ADMIN cannot appoint ADMIN, cannot create/rename/deactivate departments =="
req PATCH "/tenant/users/${user_id}/role" "${admin_accounting_token}" '{"role":"admin"}'
[[ "${status}" == "401" || "${status}" == "403" ]] && ok "ADMIN cannot reach the owner-only role-change route (HTTP ${status})" || bad "expected 401/403, got ${status}"
req POST /tenant/departments "${admin_accounting_token}" '{"code":"new-dept","name":"New"}'
[[ "${status}" == "401" || "${status}" == "403" ]] && ok "ADMIN cannot create a department (HTTP ${status})" || bad "expected 401/403, got ${status}"
req PATCH "/tenant/departments/${accounting_dept}" "${admin_accounting_token}" '{"name":"Hacked"}'
[[ "${status}" == "401" || "${status}" == "403" ]] && ok "ADMIN cannot rename a department (HTTP ${status})" || bad "expected 401/403, got ${status}"
req POST "/tenant/departments/${sales_dept}/deactivate" "${admin_accounting_token}" ""
[[ "${status}" == "401" || "${status}" == "403" ]] && ok "ADMIN cannot deactivate a department (HTTP ${status})" || bad "expected 401/403, got ${status}"
req PATCH "/tenant/users/${user_id}/department" "${admin_accounting_token}" "{\"departmentId\":\"${sales_dept}\"}"
[[ "${status}" == "401" || "${status}" == "403" ]] && ok "ADMIN cannot move a user between departments (HTTP ${status})" || bad "expected 401/403, got ${status}"
req GET "/tenant/departments/${sales_dept}" "${admin_accounting_token}" ""
expect_status "ADMIN reading a DIFFERENT department's detail is denied" 404 "${status}"
req GET "/tenant/departments/${accounting_dept}" "${admin_accounting_token}" ""
expect_status "ADMIN can read its OWN department" 200 "${status}"

echo "== §13 ADMIN self view: /me carries own department identity =="
req GET /me "${admin_accounting_token}" ""
expect_true "/me returns Admin Accounting's own department id" "$([[ "$(jqf .departmentId)" == "${accounting_dept}" ]] && echo true || echo false)"

echo "== Step 6-7: owner moves User Accounting -> Sales; old Accounting Agent access disappears =="
req PATCH "/tenant/users/${user_id}/department" "${owner_a_token}" "{\"departmentId\":\"${sales_dept}\"}"
expect_status "owner moves User Accounting to Sales" 200 "${status}"

req GET "/runtime/agents/${accounting_agent}/manifest" "${user_token}" ""
expect_status "moved USER no longer executes the old-department-only Accounting Agent" 403 "${status}"
direct_status="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT status FROM agent_assignments WHERE user_id='${user_id}' AND agent_id='${accounting_agent}'")"
expect_true "the stale direct assignment was actually revoked (not just ineffective)" "$([[ "${direct_status}" == "revoked" ]] && echo true || echo false)"

echo "== Step 8: assign the correct Sales Agent to the moved user =="
req PUT "/tenant/agents/${sales_agent}/users/${user_id}" "${admin_sales_token}" ""
expect_status "Admin Sales assigns Sales Agent to the (now Sales) user" 204 "${status}"
req GET "/runtime/agents/${sales_agent}/manifest" "${user_token}" ""
expect_status "moved USER executes the new department's agent" 200 "${status}"

echo "== Step 9: promote USER to ADMIN; verify department-level ADMIN execution in the CURRENT (Sales) department =="
req PATCH "/tenant/users/${user_id}/role" "${owner_a_token}" '{"role":"admin"}'
expect_status "owner promotes the (now Sales) user to ADMIN" 200 "${status}"
req POST /auth/login "" '{"email":"user-accounting@d5test.local","password":"UserPass123!"}'
promoted_token="$(jqf .token)"
# Revoke the direct Sales Agent assignment to prove execution now comes from
# DEPARTMENT-level ADMIN entitlement, not the leftover direct row.
req DELETE "/tenant/agents/${sales_agent}/users/${user_id}" "${admin_sales_token}" ""
expect_status "revoke the direct assignment before proving admin-level execution" 204 "${status}"
req GET "/runtime/agents/${sales_agent}/manifest" "${promoted_token}" ""
expect_status "promoted ADMIN executes Sales Agent with NO direct assignment" 200 "${status}"

echo "== Step 10: demote back to USER; direct-assignment requirement returns =="
req PATCH "/tenant/users/${user_id}/role" "${owner_a_token}" '{"role":"user"}'
expect_status "owner demotes back to USER" 200 "${status}"
req POST /auth/login "" '{"email":"user-accounting@d5test.local","password":"UserPass123!"}'
demoted_token="$(jqf .token)"
req GET "/runtime/agents/${sales_agent}/manifest" "${demoted_token}" ""
expect_status "demoted USER denied — direct assignment required again" 403 "${status}"
expect_true "denial reason is the direct-assignment gap, not department/tenant" "$([[ "$(jqf .error)" == agent_not_assigned ]] && echo true || echo false)"

echo "== §5 deactivation blockers, then clean deactivate =="
req POST "/tenant/departments/${sales_dept}/deactivate" "${owner_a_token}" ""
expect_status "deactivate blocked while Sales still has active members" 409 "${status}"
expect_true "blocker reason is active members" "$([[ "$(jqf .error)" == active_members_remain ]] && echo true || echo false)"

req POST /tenant/departments "${owner_a_token}" '{"code":"empty","name":"Empty"}'
empty_dept="$(jqf .id)"
req POST "/tenant/departments/${empty_dept}/deactivate" "${owner_a_token}" ""
expect_status "clean, empty department deactivates successfully" 200 "${status}"
req POST "/tenant/departments/${empty_dept}/activate" "${owner_a_token}" ""
expect_status "reactivation restores it" 200 "${status}"

echo "== Step 11 / cross-tenant: every department operation on Tenant A denied for Owner B =="
req POST /tenant/departments "${owner_b_token}" "{\"code\":\"steal\",\"name\":\"Steal\"}"
expect_status "Owner B creating a department is scoped to Tenant B only (sanity)" 201 "${status}"
req PATCH "/tenant/users/${user_id}/department" "${owner_b_token}" "{\"departmentId\":\"${accounting_dept}\"}"
expect_status "Owner B cannot move a Tenant A user" 404 "${status}"
req PATCH "/tenant/departments/${sales_dept}" "${owner_b_token}" '{"name":"Hijacked"}'
expect_status "Owner B cannot rename a Tenant A department" 404 "${status}"
req POST "/tenant/departments/${sales_dept}/deactivate" "${owner_b_token}" ""
expect_status "Owner B cannot deactivate a Tenant A department" 404 "${status}"

echo "================================================================"
echo "D5 E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
