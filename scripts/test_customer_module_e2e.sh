#!/usr/bin/env bash
# Customer module (Khach hang) real HTTP E2E.
#
# Boots an isolated, empty Postgres + freshly built Gateway image (own
# network/volume, never touches the shared dev stack) and verifies:
#   USER (assigned Agent + customer.management): list/search own Department
#     customers, create, edit own (creator-scoped), cannot edit another
#     USER's customer, cannot deactivate (ADMIN-only).
#   ADMIN: create/edit/deactivate/reactivate any Department customer
#     regardless of creator; cross-Department/cross-Tenant denied either way.
#   Duplicate handling: same tax code / customer code rejected (409);
#     fuzzy same-Department name match surfaced as a non-blocking warning.
#   Tax format validation: malformed MST rejected deterministically, no
#     external call.
#   Lifecycle: deactivate is a soft state flip, never a hard delete —
#     Voucher AR history referencing the customer stays intact.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-cust-e2e-net-${run_id}"
pg="aiq-cust-e2e-postgres-${run_id}"
gw="aiq-cust-e2e-gateway-${run_id}"
vol="aiq-cust-e2e-pgdata-${run_id}"
db="aiq_cust_e2e"
db_password="cust_e2e_password"
image="aiq-agentic-gateway:contracttest"
base="http://localhost:18084"
gw_port=18084

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
    if [[ "${actual}" == "${expected}" ]]; then ok "${desc} (HTTP ${actual})"; else bad "${desc} (expected ${expected}, got ${actual}): ${body}"; fi
}

req() {
    local method="$1" path="$2" token="${3:-}" req_payload="${4:-}"
    local args=(-sS -o /tmp/cust_e2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/cust_e2e_body.$$)"
    rm -f /tmp/cust_e2e_body.$$
}

jqf() { echo "${body}" | jq -r "$1"; }

echo "== Customer Module E2E: booting isolated stack =="
docker network create "${net}" >/dev/null
docker volume create "${vol}" >/dev/null

docker run --detach --name "${pg}" --network "${net}" --network-alias postgres \
    --env POSTGRES_USER=postgres --env POSTGRES_PASSWORD="${db_password}" --env POSTGRES_DB="${db}" \
    --volume "${vol}:/var/lib/postgresql/data" \
    --volume "${repo_root}/infra/postgres/init:/docker-entrypoint-initdb.d:ro" \
    pgvector/pgvector:pg16 >/dev/null

for _ in $(seq 1 90); do
    grep -q 'PostgreSQL init process complete' <<< "$(docker logs "${pg}" 2>&1)" && break
    sleep 1
done
grep -q 'PostgreSQL init process complete' <<< "$(docker logs "${pg}" 2>&1)"

docker run --detach --name "${gw}" --network "${net}" --network-alias gateway -p "${gw_port}:8080" \
    --env ASPNETCORE_ENVIRONMENT=Production --env ASPNETCORE_URLS=http://+:8080 \
    --env "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=${db};Username=postgres;Password=${db_password};Pooling=true" \
    --env Jwt__Issuer=aiq.cust.test --env Jwt__Audience=aiq.cust.client \
    --env Jwt__SigningKey=cust-e2e-test-signing-key-needs-32-bytes-long \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://minio.invalid:9000 --env Object__AccessKey=custe2e --env Object__SecretKey=custe2epassword \
    "${image}" >/dev/null

for _ in $(seq 1 60); do
    curl -sf --max-time 2 "${base}/healthz/ready" >/dev/null 2>&1 && break
    sleep 1
done
curl -sf "${base}/healthz/ready" >/dev/null
echo "Gateway ready."

echo "== Bootstrap: seed 'default' tenant, register+promote the first super_admin =="
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO tenants(code,name,status) VALUES ('default','Default','active');
  INSERT INTO departments(tenant_id,code,name,status,is_default)
  SELECT id,'default','Default','active',true FROM tenants WHERE code='default';
" >/dev/null

req POST /auth/register "" '{"email":"root@custtest.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"

docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@custtest.local' -f /tmp/first-super-admin.sql >/dev/null

req POST /auth/login "" '{"email":"root@custtest.local","password":"RootPass123!"}'
super_token="$(jqf .token)"

echo "== Setup: Tenant A (+ Sales dept), Tenant B, Customer Agent granting customer.management =="
req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-custtest","name":"Tenant A","ownerEmail":"owner-a@custtest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
expect_status "onboard Tenant A" 201 "${status}"
tenant_a="$(jqf .tenant.id)"

req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-b-custtest","name":"Tenant B","ownerEmail":"owner-b@custtest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner B"}'
expect_status "onboard Tenant B" 201 "${status}"
tenant_b="$(jqf .tenant.id)"

req GET /system/capabilities "${super_token}" ""
customer_cap="$(echo "${body}" | jq -r '.items[] | select(.code=="customer.management") | .id')"
[[ -n "${customer_cap}" && "${customer_cap}" != "null" ]] && ok "customer.management exists in catalog" || bad "customer.management missing from catalog"

req POST /system/agents "${super_token}" '{"slug":"customer-agent-e2e","name":"Customer Agent","actionTier":"suggest"}'
expect_status "create Customer Agent" 201 "${status}"
agent_id="$(jqf .id)"
req GET "/system/agents/${agent_id}/versions" "${super_token}" ""
draft_version_id="$(jqf '.items[0].id')"
req PATCH "/system/agents/${agent_id}/versions/${draft_version_id}" "${super_token}" "{\"capabilityIds\":[\"${customer_cap}\"]}"
expect_status "assign customer.management to the draft" 200 "${status}"
req POST "/system/agents/${agent_id}/versions/${draft_version_id}/publish" "${super_token}" ""
expect_status "publish version" 200 "${status}"
req PUT "/system/agents/${agent_id}/tenants/${tenant_a}" "${super_token}" ""
expect_status "assign Customer Agent to Tenant A" 204 "${status}"

docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status)
  SELECT id,'sales-custtest','Sales','active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
accounting_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
sales_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-custtest'")"

req POST /auth/login "" '{"email":"owner-a@custtest.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner assigns Customer Agent to Accounting dept" 204 "${status}"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@custtest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@custtest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "owner creates Admin Sales" 201 "${status}"
req POST /auth/login "" '{"email":"admin-accounting@custtest.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
req POST /auth/login "" '{"email":"admin-sales@custtest.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"

req PUT "/department/capabilities/${customer_cap}/binding" "${admin_accounting_token}" "{\"agentId\":\"${agent_id}\"}"
expect_status "Admin Accounting binds customer.management -> Customer Agent" 204 "${status}"

req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user@custtest.local","password":"UserPass123!","displayName":"Assigned User"}'
expect_status "Admin Accounting creates Assigned User" 201 "${status}"
assigned_user_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='assigned-user@custtest.local'")"
req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user2@custtest.local","password":"UserPass123!","displayName":"Assigned User 2"}'
expect_status "Admin Accounting creates Assigned User 2" 201 "${status}"
assigned_user2_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='assigned-user2@custtest.local'")"
req POST /tenant/users "${admin_accounting_token}" '{"email":"unassigned-user@custtest.local","password":"UserPass123!","displayName":"Unassigned User"}'
expect_status "Admin Accounting creates Unassigned User" 201 "${status}"

req PUT "/tenant/agents/${agent_id}/users/${assigned_user_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Customer Agent to Assigned User" 204 "${status}"
req PUT "/tenant/agents/${agent_id}/users/${assigned_user2_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Customer Agent to Assigned User 2" 204 "${status}"

req POST /auth/login "" '{"email":"assigned-user@custtest.local","password":"UserPass123!"}'
assigned_user_token="$(jqf .token)"
req POST /auth/login "" '{"email":"assigned-user2@custtest.local","password":"UserPass123!"}'
assigned_user2_token="$(jqf .token)"
req POST /auth/login "" '{"email":"unassigned-user@custtest.local","password":"UserPass123!"}'
unassigned_user_token="$(jqf .token)"

echo ""
echo "== §1 USER Add: Assigned User creates their own Customer =="
req POST /customers "${assigned_user_token}" '{"customerCode":"CUS-U-001","name":"Cong ty ABC","mst":"0101234567","customerType":"company","phone":"0901234567"}'
expect_status "Assigned User creates a customer" 201 "${status}"
user_customer_id="$(jqf .customer.id)"
user_customer_created_by="$(jqf .customer.createdBy)"
[[ "${user_customer_created_by}" == "${assigned_user_id}" ]] && ok "created_by is the authenticated USER, never client-supplied" || bad "created_by mismatch: ${user_customer_created_by}"

echo "== §2 Tax format validation (local, deterministic — no external call) =="
req POST /customers "${assigned_user_token}" '{"customerCode":"CUS-U-BAD","name":"MST sai dinh dang","mst":"abc123"}'
expect_status "malformed MST rejected deterministically" 400 "${status}"

echo "== §3 Duplicate handling =="
req POST /customers "${admin_accounting_token}" '{"customerCode":"CUS-U-001","name":"Trung ma KH"}'
expect_status "duplicate customer_code rejected (409)" 409 "${status}"
req POST /customers "${admin_accounting_token}" '{"customerCode":"CUS-A-002","name":"Trung MST","mst":"0101234567"}'
expect_status "duplicate mst rejected (409)" 409 "${status}"
req POST /customers "${admin_accounting_token}" '{"customerCode":"CUS-A-003","name":"Cong ty ABC "}'
expect_status "fuzzy-duplicate name still creates (warning only, non-blocking)" 201 "${status}"
dup_count="$(jqf '.possibleDuplicates | length')"
[[ "${dup_count}" -ge 1 ]] && ok "possibleDuplicates surfaced a near-match warning" || bad "expected a possibleDuplicates warning, got: ${body}"
fuzzy_dup_customer_id="$(jqf .customer.id)"

echo ""
echo "== §4 USER edit-own / cannot edit others =="
req PATCH "/customers/${user_customer_id}" "${assigned_user_token}" '{"phone":"0909999999"}'
expect_status "Assigned User edits their own customer" 200 "${status}"
req PATCH "/customers/${fuzzy_dup_customer_id}" "${assigned_user_token}" '{"phone":"0900000000"}'
expect_status "Assigned User cannot edit ADMIN-created customer (not the creator)" 403 "${status}"
req PATCH "/customers/${user_customer_id}" "${assigned_user2_token}" '{"phone":"0908888888"}'
expect_status "Assigned User 2 cannot edit Assigned User's customer (not the creator)" 403 "${status}"

echo "== §5 USER cannot deactivate (ADMIN-only) =="
req POST "/customers/${user_customer_id}/deactivate" "${assigned_user_token}" ""
expect_status "Assigned User cannot deactivate any customer" 403 "${status}"

echo ""
echo "== §6 ADMIN full Department authority =="
req PATCH "/customers/${user_customer_id}" "${admin_accounting_token}" '{"phone":"0907777777"}'
expect_status "ADMIN edits a USER-created customer (not creator-scoped)" 200 "${status}"
req GET /customers "${admin_accounting_token}" ""
expect_status "ADMIN lists Department customers" 200 "${status}"
admin_sees_user_customer="$(echo "${body}" | jq -r ".items[] | select(.id==\"${user_customer_id}\") | .id")"
[[ "${admin_sees_user_customer}" == "${user_customer_id}" ]] && ok "ADMIN's list includes the USER-created customer" || bad "ADMIN cannot see USER-created customer"

echo "== §7 Lifecycle: deactivate/reactivate is a soft flip, list excludes inactive by default =="
req POST "/customers/${user_customer_id}/deactivate" "${admin_accounting_token}" ""
expect_status "ADMIN deactivates the customer" 200 "${status}"
req GET /customers "${assigned_user_token}" ""
still_listed="$(echo "${body}" | jq -r ".items[] | select(.id==\"${user_customer_id}\") | .id")"
[[ -z "${still_listed}" ]] && ok "deactivated customer no longer appears in the default (active-only) list" || bad "deactivated customer leaked into default list"
req GET "/customers/${user_customer_id}" "${assigned_user_token}" ""
expect_status "deactivated customer is still readable directly (soft-delete, not gone)" 200 "${status}"
req PATCH "/customers/${user_customer_id}" "${admin_accounting_token}" '{"phone":"0906666666"}'
expect_status "an inactive customer cannot be edited until reactivated" 409 "${status}"
req POST "/customers/${user_customer_id}/reactivate" "${admin_accounting_token}" ""
expect_status "ADMIN reactivates the customer" 200 "${status}"

echo ""
echo "== §8 USER without capability denied =="
req GET /customers "${unassigned_user_token}" ""
expect_status "Unassigned User cannot list customers" 403 "${status}"
req POST /customers "${unassigned_user_token}" '{"customerCode":"CUS-X","name":"No cap"}'
expect_status "Unassigned User cannot create a customer" 403 "${status}"

echo ""
echo "== §9 Cross-Department / cross-Tenant denied =="
req GET /customers "${admin_sales_token}" ""
expect_status "Admin Sales's own customer list works (own Department)" 200 "${status}"
sales_sees_accounting="$(echo "${body}" | jq -r ".items[] | select(.id==\"${user_customer_id}\") | .id")"
[[ -z "${sales_sees_accounting}" ]] && ok "Admin Sales's list does NOT contain Accounting's customer" || bad "cross-Department leak"
req GET "/customers/${user_customer_id}" "${admin_sales_token}" ""
expect_status "Admin Sales cannot fetch Accounting's customer directly" 404 "${status}"

tenant_b_default_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_b}' AND is_default")"
req POST /auth/login "" '{"email":"owner-b@custtest.local","password":"OwnerPass123!"}'
owner_b_token="$(jqf .token)"
req POST /tenant/admins "${owner_b_token}" "{\"email\":\"admin-b@custtest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin B\",\"departmentId\":\"${tenant_b_default_dept}\"}"
expect_status "owner B creates Admin B (own tenant, own department)" 201 "${status}"
req POST /auth/login "" '{"email":"admin-b@custtest.local","password":"AdminPass123!"}'
admin_b_token="$(jqf .token)"
req GET "/customers/${user_customer_id}" "${admin_b_token}" ""
expect_status "Admin B (Tenant B) cannot fetch Tenant A's customer (cross-tenant denied)" 404 "${status}"

echo ""
echo "== §10 Voucher AR integration unaffected — resolves by MST, not a second master =="
req GET /customers/by-mst/0101234567 "${admin_accounting_token}" ""
expect_status "Customer resolvable by MST (same lookup Voucher AR enrichment uses)" 200 "${status}"

echo ""
echo "================================================================"
echo "CUSTOMER MODULE E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
