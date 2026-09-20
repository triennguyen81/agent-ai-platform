#!/usr/bin/env bash
# Supplier module (Nha cung cap) real HTTP E2E.
#
# Boots an isolated, empty Postgres + freshly built Gateway image (own
# network/volume, never touches the shared dev stack) and verifies:
#   USER (assigned Agent + supplier.management): list/search own Department
#     suppliers, create, edit own (creator-scoped), cannot edit another
#     USER's supplier, cannot deactivate (ADMIN-only).
#   ADMIN: create/edit/deactivate/reactivate any Department supplier
#     regardless of creator; cross-Department/cross-Tenant denied either way.
#   Duplicate handling: same tax code / supplier code rejected (409);
#     fuzzy same-Department name match surfaced as a non-blocking warning.
#   Tax format validation: malformed MST rejected deterministically, no
#     external call.
#   Lifecycle: deactivate is a soft state flip, never a hard delete —
#     Voucher AP history referencing the supplier stays intact.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-sup-e2e-net-${run_id}"
pg="aiq-sup-e2e-postgres-${run_id}"
gw="aiq-sup-e2e-gateway-${run_id}"
vol="aiq-sup-e2e-pgdata-${run_id}"
db="aiq_sup_e2e"
db_password="sup_e2e_password"
image="aiq-agentic-gateway:contracttest"
base="http://localhost:18085"
gw_port=18085

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
    local args=(-sS -o /tmp/sup_e2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/sup_e2e_body.$$)"
    rm -f /tmp/sup_e2e_body.$$
}

jqf() { echo "${body}" | jq -r "$1"; }

echo "== Supplier Module E2E: booting isolated stack =="
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
    --env Jwt__Issuer=aiq.sup.test --env Jwt__Audience=aiq.sup.client \
    --env Jwt__SigningKey=sup-e2e-test-signing-key-needs-32-bytes-long \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://minio.invalid:9000 --env Object__AccessKey=supe2e --env Object__SecretKey=supe2epassword \
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

req POST /auth/register "" '{"email":"root@suptest.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"

docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@suptest.local' -f /tmp/first-super-admin.sql >/dev/null

req POST /auth/login "" '{"email":"root@suptest.local","password":"RootPass123!"}'
super_token="$(jqf .token)"

echo "== Setup: Tenant A (+ Sales dept), Tenant B, Supplier Agent granting supplier.management =="
req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-suptest","name":"Tenant A","ownerEmail":"owner-a@suptest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
expect_status "onboard Tenant A" 201 "${status}"
tenant_a="$(jqf .tenant.id)"

req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-b-suptest","name":"Tenant B","ownerEmail":"owner-b@suptest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner B"}'
expect_status "onboard Tenant B" 201 "${status}"
tenant_b="$(jqf .tenant.id)"

req GET /system/capabilities "${super_token}" ""
supplier_cap="$(echo "${body}" | jq -r '.items[] | select(.code=="supplier.management") | .id')"
[[ -n "${supplier_cap}" && "${supplier_cap}" != "null" ]] && ok "supplier.management exists in catalog" || bad "supplier.management missing from catalog"
expense_cap="$(echo "${body}" | jq -r '.items[] | select(.code=="expense.processing") | .id')"
[[ -n "${expense_cap}" && "${expense_cap}" != "null" ]] && ok "expense.processing exists in catalog" || bad "expense.processing missing from catalog"

req POST /system/agents "${super_token}" '{"slug":"supplier-agent-e2e","name":"Supplier Agent","actionTier":"suggest"}'
expect_status "create Supplier Agent" 201 "${status}"
agent_id="$(jqf .id)"
req GET "/system/agents/${agent_id}/versions" "${super_token}" ""
draft_version_id="$(jqf '.items[0].id')"
req PATCH "/system/agents/${agent_id}/versions/${draft_version_id}" "${super_token}" "{\"capabilityIds\":[\"${supplier_cap}\"]}"
expect_status "assign supplier.management to the draft" 200 "${status}"
req POST "/system/agents/${agent_id}/versions/${draft_version_id}/publish" "${super_token}" ""
expect_status "publish version" 200 "${status}"
req PUT "/system/agents/${agent_id}/tenants/${tenant_a}" "${super_token}" ""
expect_status "assign Supplier Agent to Tenant A" 204 "${status}"

echo "== Setup: a second Expense Agent granting ONLY expense.processing (read access, no supplier.management) =="
req POST /system/agents "${super_token}" '{"slug":"expense-agent-e2e","name":"Expense Agent","actionTier":"suggest"}'
expect_status "create Expense Agent" 201 "${status}"
expense_agent_id="$(jqf .id)"
req GET "/system/agents/${expense_agent_id}/versions" "${super_token}" ""
expense_draft_version_id="$(jqf '.items[0].id')"
req PATCH "/system/agents/${expense_agent_id}/versions/${expense_draft_version_id}" "${super_token}" "{\"capabilityIds\":[\"${expense_cap}\"]}"
expect_status "assign expense.processing to the draft" 200 "${status}"
req POST "/system/agents/${expense_agent_id}/versions/${expense_draft_version_id}/publish" "${super_token}" ""
expect_status "publish Expense Agent version" 200 "${status}"
req PUT "/system/agents/${expense_agent_id}/tenants/${tenant_a}" "${super_token}" ""
expect_status "assign Expense Agent to Tenant A" 204 "${status}"

docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status)
  SELECT id,'sales-suptest','Sales','active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
accounting_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
sales_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-suptest'")"

req POST /auth/login "" '{"email":"owner-a@suptest.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner assigns Supplier Agent to Accounting dept" 204 "${status}"
req PUT "/tenant/departments/${accounting_dept}/agents/${expense_agent_id}" "${owner_a_token}" ""
expect_status "owner assigns Expense Agent to Accounting dept" 204 "${status}"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@suptest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@suptest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "owner creates Admin Sales" 201 "${status}"
req POST /auth/login "" '{"email":"admin-accounting@suptest.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
req POST /auth/login "" '{"email":"admin-sales@suptest.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"

req PUT "/department/capabilities/${supplier_cap}/binding" "${admin_accounting_token}" "{\"agentId\":\"${agent_id}\"}"
expect_status "Admin Accounting binds supplier.management -> Supplier Agent" 204 "${status}"
req PUT "/department/capabilities/${expense_cap}/binding" "${admin_accounting_token}" "{\"agentId\":\"${expense_agent_id}\"}"
expect_status "Admin Accounting binds expense.processing -> Expense Agent" 204 "${status}"

req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user@suptest.local","password":"UserPass123!","displayName":"Assigned User"}'
expect_status "Admin Accounting creates Assigned User" 201 "${status}"
assigned_user_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='assigned-user@suptest.local'")"
req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user2@suptest.local","password":"UserPass123!","displayName":"Assigned User 2"}'
expect_status "Admin Accounting creates Assigned User 2" 201 "${status}"
assigned_user2_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='assigned-user2@suptest.local'")"
req POST /tenant/users "${admin_accounting_token}" '{"email":"unassigned-user@suptest.local","password":"UserPass123!","displayName":"Unassigned User"}'
expect_status "Admin Accounting creates Unassigned User" 201 "${status}"
req POST /tenant/users "${admin_accounting_token}" '{"email":"expense-user@suptest.local","password":"UserPass123!","displayName":"Expense User"}'
expect_status "Admin Accounting creates Expense User" 201 "${status}"
expense_user_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='expense-user@suptest.local'")"

req PUT "/tenant/agents/${agent_id}/users/${assigned_user_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Supplier Agent to Assigned User" 204 "${status}"
req PUT "/tenant/agents/${agent_id}/users/${assigned_user2_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Supplier Agent to Assigned User 2" 204 "${status}"
req PUT "/tenant/agents/${expense_agent_id}/users/${expense_user_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Expense Agent to Expense User" 204 "${status}"

req POST /auth/login "" '{"email":"assigned-user@suptest.local","password":"UserPass123!"}'
assigned_user_token="$(jqf .token)"
req POST /auth/login "" '{"email":"assigned-user2@suptest.local","password":"UserPass123!"}'
assigned_user2_token="$(jqf .token)"
req POST /auth/login "" '{"email":"unassigned-user@suptest.local","password":"UserPass123!"}'
unassigned_user_token="$(jqf .token)"
req POST /auth/login "" '{"email":"expense-user@suptest.local","password":"UserPass123!"}'
expense_user_token="$(jqf .token)"

echo ""
echo "== §1 USER Add: Assigned User creates their own Supplier =="
req POST /suppliers "${assigned_user_token}" '{"supplierCode":"SUP-U-001","name":"Cong ty ABC","mst":"0101234567","supplierType":"company","phone":"0901234567"}'
expect_status "Assigned User creates a supplier" 201 "${status}"
user_supplier_id="$(jqf .supplier.id)"
user_supplier_created_by="$(jqf .supplier.createdBy)"
[[ "${user_supplier_created_by}" == "${assigned_user_id}" ]] && ok "created_by is the authenticated USER, never client-supplied" || bad "created_by mismatch: ${user_supplier_created_by}"

echo "== §2 Tax format validation (local, deterministic — no external call) =="
req POST /suppliers "${assigned_user_token}" '{"supplierCode":"SUP-U-BAD","name":"MST sai dinh dang","mst":"abc123"}'
expect_status "malformed MST rejected deterministically" 400 "${status}"

echo "== §3 Duplicate handling =="
req POST /suppliers "${admin_accounting_token}" '{"supplierCode":"SUP-U-001","name":"Trung ma KH"}'
expect_status "duplicate supplier_code rejected (409)" 409 "${status}"
req POST /suppliers "${admin_accounting_token}" '{"supplierCode":"SUP-A-002","name":"Trung MST","mst":"0101234567"}'
expect_status "duplicate mst rejected (409)" 409 "${status}"
req POST /suppliers "${admin_accounting_token}" '{"supplierCode":"SUP-A-003","name":"Cong ty ABC "}'
expect_status "fuzzy-duplicate name still creates (warning only, non-blocking)" 201 "${status}"
dup_count="$(jqf '.possibleDuplicates | length')"
[[ "${dup_count}" -ge 1 ]] && ok "possibleDuplicates surfaced a near-match warning" || bad "expected a possibleDuplicates warning, got: ${body}"
fuzzy_dup_supplier_id="$(jqf .supplier.id)"

echo ""
echo "== §4 USER edit-own / cannot edit others =="
req PATCH "/suppliers/${user_supplier_id}" "${assigned_user_token}" '{"phone":"0909999999"}'
expect_status "Assigned User edits their own supplier" 200 "${status}"
req PATCH "/suppliers/${fuzzy_dup_supplier_id}" "${assigned_user_token}" '{"phone":"0900000000"}'
expect_status "Assigned User cannot edit ADMIN-created supplier (not the creator)" 403 "${status}"
req PATCH "/suppliers/${user_supplier_id}" "${assigned_user2_token}" '{"phone":"0908888888"}'
expect_status "Assigned User 2 cannot edit Assigned User's supplier (not the creator)" 403 "${status}"

echo "== §5 USER cannot deactivate (ADMIN-only) =="
req POST "/suppliers/${user_supplier_id}/deactivate" "${assigned_user_token}" ""
expect_status "Assigned User cannot deactivate any supplier" 403 "${status}"

echo ""
echo "== §6 ADMIN full Department authority =="
req PATCH "/suppliers/${user_supplier_id}" "${admin_accounting_token}" '{"phone":"0907777777"}'
expect_status "ADMIN edits a USER-created supplier (not creator-scoped)" 200 "${status}"
req GET /suppliers "${admin_accounting_token}" ""
expect_status "ADMIN lists Department suppliers" 200 "${status}"
admin_sees_user_supplier="$(echo "${body}" | jq -r ".items[] | select(.id==\"${user_supplier_id}\") | .id")"
[[ "${admin_sees_user_supplier}" == "${user_supplier_id}" ]] && ok "ADMIN's list includes the USER-created supplier" || bad "ADMIN cannot see USER-created supplier"

echo "== §7 Lifecycle: deactivate/reactivate is a soft flip, list excludes inactive by default =="
req POST "/suppliers/${user_supplier_id}/deactivate" "${admin_accounting_token}" ""
expect_status "ADMIN deactivates the supplier" 200 "${status}"
req GET /suppliers "${assigned_user_token}" ""
still_listed="$(echo "${body}" | jq -r ".items[] | select(.id==\"${user_supplier_id}\") | .id")"
[[ -z "${still_listed}" ]] && ok "deactivated supplier no longer appears in the default (active-only) list" || bad "deactivated supplier leaked into default list"
req GET "/suppliers/${user_supplier_id}" "${assigned_user_token}" ""
expect_status "deactivated supplier is still readable directly (soft-delete, not gone)" 200 "${status}"
req PATCH "/suppliers/${user_supplier_id}" "${admin_accounting_token}" '{"phone":"0906666666"}'
expect_status "an inactive supplier cannot be edited until reactivated" 409 "${status}"
req POST "/suppliers/${user_supplier_id}/reactivate" "${admin_accounting_token}" ""
expect_status "ADMIN reactivates the supplier" 200 "${status}"

echo ""
echo "== §8 USER without capability denied =="
req GET /suppliers "${unassigned_user_token}" ""
expect_status "Unassigned User cannot list suppliers" 403 "${status}"
req POST /suppliers "${unassigned_user_token}" '{"supplierCode":"SUP-X","name":"No cap"}'
expect_status "Unassigned User cannot create a supplier" 403 "${status}"

echo "== §8b USER with expense.processing ONLY (not supplier.management): read yes, create no =="
req GET /suppliers "${expense_user_token}" ""
expect_status "Expense User (expense.processing only) can list suppliers (read-level OR-capability)" 200 "${status}"
req POST /suppliers "${expense_user_token}" '{"supplierCode":"SUP-EXP","name":"Should not be created"}'
expect_status "Expense User cannot create a supplier (mutation requires supplier.management specifically)" 403 "${status}"

echo ""
echo "== §9 Cross-Department / cross-Tenant denied =="
req GET /suppliers "${admin_sales_token}" ""
expect_status "Admin Sales's own supplier list works (own Department)" 200 "${status}"
sales_sees_accounting="$(echo "${body}" | jq -r ".items[] | select(.id==\"${user_supplier_id}\") | .id")"
[[ -z "${sales_sees_accounting}" ]] && ok "Admin Sales's list does NOT contain Accounting's supplier" || bad "cross-Department leak"
req GET "/suppliers/${user_supplier_id}" "${admin_sales_token}" ""
expect_status "Admin Sales cannot fetch Accounting's supplier directly" 404 "${status}"

tenant_b_default_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_b}' AND is_default")"
# /auth/* is a global fixed-window limiter (10/min, by design — pre-login
# there's no user to key by). This script's setup alone issues 9 login/
# register calls; wait out the window before the last 2 (owner-b, admin-b)
# so this — a real product safeguard, not a bug — doesn't flake the test.
sleep 61
req POST /auth/login "" '{"email":"owner-b@suptest.local","password":"OwnerPass123!"}'
owner_b_token="$(jqf .token)"
req POST /tenant/admins "${owner_b_token}" "{\"email\":\"admin-b@suptest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin B\",\"departmentId\":\"${tenant_b_default_dept}\"}"
expect_status "owner B creates Admin B (own tenant, own department)" 201 "${status}"
req POST /auth/login "" '{"email":"admin-b@suptest.local","password":"AdminPass123!"}'
expect_status "admin B logs in" 200 "${status}"
admin_b_token="$(jqf .token)"
req GET "/suppliers/${user_supplier_id}" "${admin_b_token}" ""
expect_status "Admin B (Tenant B) cannot fetch Tenant A's supplier (cross-tenant denied)" 404 "${status}"

echo ""
echo "== §10 Voucher AP integration unaffected — resolves by MST, not a second master =="
req GET /suppliers/by-mst/0101234567 "${admin_accounting_token}" ""
expect_status "Supplier resolvable by MST (same lookup Voucher AP enrichment uses)" 200 "${status}"

echo ""
echo "================================================================"
echo "SUPPLIER MODULE E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
