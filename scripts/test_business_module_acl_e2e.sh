#!/usr/bin/env bash
# Business Module Department ACL real HTTP E2E — Ke toan (Vouchers), Khach
# hang (Customers), Nha cung cap (Suppliers), Chi phi (expense.processing).
#
# Boots an isolated, empty Postgres + freshly built Gateway image (own
# network/volume, never touches the shared dev stack) and verifies the
# permission model from the business-permission audit:
#   USER (assigned Agent + capability): read Department records, may
#     create/upload where the module supports it (Vouchers), may edit only
#     records they created and only while editable, cannot delete, cannot
#     approve/reject/apply (post AR/AP, decide journal entries).
#   ADMIN: full operational authority over every record in own Department
#     regardless of creator; may delete/approve/apply; still Department- and
#     Tenant-scoped (no cross-Department/cross-Tenant reach).
#   USER without the module's capability: denied entirely.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-bmacl-e2e-net-${run_id}"
pg="aiq-bmacl-e2e-postgres-${run_id}"
gw="aiq-bmacl-e2e-gateway-${run_id}"
vol="aiq-bmacl-e2e-pgdata-${run_id}"
db="aiq_bmacl_e2e"
db_password="bmacl_e2e_password"
image="aiq-agentic-gateway:contracttest"
base="http://localhost:18083"
gw_port=18083

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
    local args=(-sS -o /tmp/bmacl_e2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/bmacl_e2e_body.$$)"
    rm -f /tmp/bmacl_e2e_body.$$
}

jqf() { echo "${body}" | jq -r "$1"; }

echo "== Business Module ACL E2E: booting isolated stack =="
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
    --env Jwt__Issuer=aiq.bmacl.test --env Jwt__Audience=aiq.bmacl.client \
    --env Jwt__SigningKey=bmacl-e2e-test-signing-key-needs-32-bytes \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://minio.invalid:9000 --env Object__AccessKey=bmacle2e --env Object__SecretKey=bmacle2epassword \
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

req POST /auth/register "" '{"email":"root@bmacltest.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"

docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@bmacltest.local' -f /tmp/first-super-admin.sql >/dev/null

req POST /auth/login "" '{"email":"root@bmacltest.local","password":"RootPass123!"}'
expect_status "login as super_admin" 200 "${status}"
super_token="$(jqf .token)"

echo "== Setup: onboard Tenant A + Tenant B, publish one Business Agent granting all 4 module capabilities =="
req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-bmacltest","name":"Tenant A","ownerEmail":"owner-a@bmacltest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
expect_status "onboard Tenant A" 201 "${status}"
tenant_a="$(jqf .tenant.id)"

req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-b-bmacltest","name":"Tenant B","ownerEmail":"owner-b@bmacltest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner B"}'
expect_status "onboard Tenant B" 201 "${status}"
tenant_b="$(jqf .tenant.id)"

req GET /system/capabilities "${super_token}" ""
voucher_cap="$(echo "${body}" | jq -r '.items[] | select(.code=="voucher.processing") | .id')"
customer_cap="$(echo "${body}" | jq -r '.items[] | select(.code=="customer.management") | .id')"
supplier_cap="$(echo "${body}" | jq -r '.items[] | select(.code=="supplier.management") | .id')"
expense_cap="$(echo "${body}" | jq -r '.items[] | select(.code=="expense.processing") | .id')"
[[ -n "${voucher_cap}" && "${voucher_cap}" != "null" ]] && ok "voucher.processing exists in catalog" || bad "voucher.processing missing from catalog"
[[ -n "${customer_cap}" && "${customer_cap}" != "null" ]] && ok "customer.management exists in catalog" || bad "customer.management missing from catalog"
[[ -n "${supplier_cap}" && "${supplier_cap}" != "null" ]] && ok "supplier.management exists in catalog" || bad "supplier.management missing from catalog"
[[ -n "${expense_cap}" && "${expense_cap}" != "null" ]] && ok "expense.processing exists in catalog" || bad "expense.processing missing from catalog"

req POST /system/agents "${super_token}" '{"slug":"business-agent-e2e","name":"Business Agent","actionTier":"suggest"}'
expect_status "create Business Agent" 201 "${status}"
agent_id="$(jqf .id)"

req GET "/system/agents/${agent_id}/versions" "${super_token}" ""
draft_version_id="$(jqf '.items[0].id')"

req PATCH "/system/agents/${agent_id}/versions/${draft_version_id}" "${super_token}" "{\"capabilityIds\":[\"${voucher_cap}\",\"${customer_cap}\",\"${supplier_cap}\",\"${expense_cap}\"]}"
expect_status "assign all 4 capabilities to the draft" 200 "${status}"

req POST "/system/agents/${agent_id}/versions/${draft_version_id}/publish" "${super_token}" ""
expect_status "publish version" 200 "${status}"

req PUT "/system/agents/${agent_id}/tenants/${tenant_a}" "${super_token}" ""
expect_status "assign Business Agent to Tenant A" 204 "${status}"

echo "== Setup: seed Accounting (default) + Sales departments in Tenant A =="
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status)
  SELECT id,'sales-bmacltest','Sales','active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
accounting_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
sales_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-bmacltest'")"

req POST /auth/login "" '{"email":"owner-a@bmacltest.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"

req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner assigns Business Agent to Accounting dept" 204 "${status}"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@bmacltest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@bmacltest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "owner creates Admin Sales" 201 "${status}"

req POST /auth/login "" '{"email":"admin-accounting@bmacltest.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
req POST /auth/login "" '{"email":"admin-sales@bmacltest.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"

for cap in "${voucher_cap}" "${customer_cap}" "${supplier_cap}" "${expense_cap}"; do
  req PUT "/department/capabilities/${cap}/binding" "${admin_accounting_token}" "{\"agentId\":\"${agent_id}\"}"
  expect_status "Admin Accounting binds capability ${cap} -> Business Agent" 204 "${status}"
done

echo "== Setup: Assigned User, Assigned User 2 (same dept), Unassigned User (no agent grant) =="
req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user@bmacltest.local","password":"UserPass123!","displayName":"Assigned User"}'
expect_status "Admin Accounting creates Assigned User" 201 "${status}"
assigned_user_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='assigned-user@bmacltest.local'")"

req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user2@bmacltest.local","password":"UserPass123!","displayName":"Assigned User 2"}'
expect_status "Admin Accounting creates Assigned User 2" 201 "${status}"
assigned_user2_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='assigned-user2@bmacltest.local'")"

req POST /tenant/users "${admin_accounting_token}" '{"email":"unassigned-user@bmacltest.local","password":"UserPass123!","displayName":"Unassigned User"}'
expect_status "Admin Accounting creates Unassigned User" 201 "${status}"

req PUT "/tenant/agents/${agent_id}/users/${assigned_user_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Business Agent to Assigned User" 204 "${status}"
req PUT "/tenant/agents/${agent_id}/users/${assigned_user2_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Business Agent to Assigned User 2" 204 "${status}"

req POST /auth/login "" '{"email":"assigned-user@bmacltest.local","password":"UserPass123!"}'
assigned_user_token="$(jqf .token)"
req POST /auth/login "" '{"email":"assigned-user2@bmacltest.local","password":"UserPass123!"}'
assigned_user2_token="$(jqf .token)"
req POST /auth/login "" '{"email":"unassigned-user@bmacltest.local","password":"UserPass123!"}'
unassigned_user_token="$(jqf .token)"

echo "== Setup: seed sim_customers/sim_suppliers — one row in Accounting dept, one in Sales dept =="
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO sim_customers(tenant_id,department_id,customer_code,name,mst,category)
  VALUES ('${tenant_a}','${accounting_dept}','CUS-ACC-01','Khach hang Ke toan','0101111111','doanh_nghiep'),
         ('${tenant_a}','${sales_dept}','CUS-SALES-01','Khach hang Sales','0102222222','doanh_nghiep');
  INSERT INTO sim_suppliers(tenant_id,department_id,supplier_code,name,mst,country)
  VALUES ('${tenant_a}','${accounting_dept}','SUP-ACC-01','Nha cung cap Ke toan','0103333333','VN'),
         ('${tenant_a}','${sales_dept}','SUP-SALES-01','Nha cung cap Sales','0104444444','VN');
" >/dev/null
accounting_customer_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM sim_customers WHERE customer_code='CUS-ACC-01'")"
accounting_supplier_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM sim_suppliers WHERE supplier_code='SUP-ACC-01'")"

echo ""
echo "== §1 Voucher (Chung tu / Ke toan) — ADMIN Accounting: full CRUD regardless of creator =="
req POST /vouchers "${admin_accounting_token}" '{"voucherType":"invoice_vat_in","documentNumber":"HD-ADMIN-001","totalAmount":5000000,"currency":"VND"}'
expect_status "ADMIN Accounting creates a voucher" 201 "${status}"
admin_voucher_id="$(jqf .voucher.id)"

req GET "/vouchers/${admin_voucher_id}" "${admin_accounting_token}" ""
expect_status "ADMIN Accounting reads its own voucher" 200 "${status}"

req PATCH "/vouchers/${admin_voucher_id}" "${admin_accounting_token}" '{"documentNumber":"HD-ADMIN-001-EDIT"}'
expect_status "ADMIN Accounting updates its own voucher" 200 "${status}"

echo ""
echo "== §2 Voucher — Assigned User (capability + creator) =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"invoice_vat_in","documentNumber":"HD-USER-001","totalAmount":3000000,"currency":"VND"}'
expect_status "Assigned User creates their own voucher" 201 "${status}"
user_voucher_id="$(jqf .voucher.id)"
user_voucher_owner="$(jqf .voucher.userId)"
[[ "${user_voucher_owner}" == "${assigned_user_id}" ]] && ok "created_by is the authenticated USER, never client-supplied" || bad "created_by mismatch: ${user_voucher_owner}"

req GET /vouchers "${assigned_user_token}" ""
expect_status "Assigned User lists Department vouchers" 200 "${status}"
list_has_admin_voucher="$(echo "${body}" | jq -r ".items[] | select(.id==\"${admin_voucher_id}\") | .id")"
[[ "${list_has_admin_voucher}" == "${admin_voucher_id}" ]] && ok "Assigned User's list includes ADMIN's voucher too (Department-scoped read)" || bad "Assigned User cannot see ADMIN's voucher in the Department list"

req GET "/vouchers/${admin_voucher_id}" "${assigned_user_token}" ""
expect_status "Assigned User reads ADMIN's voucher (same Department)" 200 "${status}"

req PATCH "/vouchers/${user_voucher_id}" "${assigned_user_token}" '{"documentNumber":"HD-USER-001-EDIT"}'
expect_status "Assigned User edits their own voucher" 200 "${status}"

req PATCH "/vouchers/${admin_voucher_id}" "${assigned_user_token}" '{"documentNumber":"SHOULD-NOT-APPLY"}'
expect_status "Assigned User cannot edit ADMIN's voucher (not the creator)" 403 "${status}"

req DELETE "/vouchers/${user_voucher_id}" "${assigned_user_token}" ""
expect_status "Assigned User cannot delete any voucher (ADMIN-only)" 403 "${status}"

req POST "/vouchers/${user_voucher_id}/apply-invoice" "${assigned_user_token}" ""
expect_status "Assigned User cannot apply-invoice (ADMIN-only posting action)" 403 "${status}"
req POST "/vouchers/${user_voucher_id}/apply-expense" "${assigned_user_token}" ""
expect_status "Assigned User cannot apply-expense (ADMIN-only posting action)" 403 "${status}"
req POST "/vouchers/${user_voucher_id}/apply-receipt" "${assigned_user_token}" ""
expect_status "Assigned User cannot apply-receipt (ADMIN-only posting action)" 403 "${status}"

req POST "/vouchers/${user_voucher_id}/journal-suggestions" "${assigned_user_token}" '{"entries":[{"entryNo":1,"debitAccount":"642","creditAccount":"331","amount":3000000}]}'
expect_status "Assigned User CAN propose a journal suggestion on their own voucher" 200 "${status}"
entry_id="$(jqf '.[0].id')"

req POST "/vouchers/${user_voucher_id}/journal-suggestions/${entry_id}/decision" "${assigned_user_token}" '{"decision":"approve"}'
expect_status "Assigned User cannot decide (approve/reject) a journal suggestion (ADMIN-only)" 403 "${status}"

req POST "/vouchers/${user_voucher_id}/journal-suggestions/${entry_id}/decision" "${admin_accounting_token}" '{"decision":"approve"}'
expect_status "ADMIN Accounting CAN decide the journal suggestion" 200 "${status}"

echo ""
echo "== §3 Voucher — Assigned User 2 (same Department, different creator) =="
req PATCH "/vouchers/${user_voucher_id}" "${assigned_user2_token}" '{"documentNumber":"SHOULD-NOT-APPLY-EITHER"}'
expect_status "Assigned User 2 cannot edit Assigned User's voucher (not the creator)" 403 "${status}"

echo ""
echo "== §4 Voucher — Unassigned User (no capability grant) =="
req GET /vouchers "${unassigned_user_token}" ""
expect_status "Unassigned User cannot list vouchers at all (no voucher.processing)" 403 "${status}"
req POST /vouchers "${unassigned_user_token}" '{"voucherType":"invoice_vat_in","totalAmount":1000,"currency":"VND"}'
expect_status "Unassigned User cannot create a voucher" 403 "${status}"

echo ""
echo "== §5 Voucher — ADMIN Sales (different Department) =="
req GET /vouchers "${admin_sales_token}" ""
expect_status "Admin Sales's own voucher list still works (own Department)" 200 "${status}"
sales_sees_accounting="$(echo "${body}" | jq -r ".items[] | select(.id==\"${admin_voucher_id}\") | .id")"
[[ -z "${sales_sees_accounting}" ]] && ok "Admin Sales's list does NOT contain Accounting's voucher" || bad "Admin Sales leaked Accounting's voucher"

req GET "/vouchers/${admin_voucher_id}" "${admin_sales_token}" ""
expect_status "Admin Sales cannot fetch Accounting's voucher directly" 404 "${status}"

echo ""
echo "== §6 Customer / Supplier (Khach hang / Nha cung cap) — read-only Department-scoped views =="
req GET /customers "${assigned_user_token}" ""
expect_status "Assigned User lists Department customers" 200 "${status}"
has_acc_customer="$(echo "${body}" | jq -r ".items[] | select(.id==\"${accounting_customer_id}\") | .id")"
[[ "${has_acc_customer}" == "${accounting_customer_id}" ]] && ok "Assigned User's customer list contains Accounting's seeded customer" || bad "Accounting's customer missing from list"
has_sales_customer_leak="$(echo "${body}" | jq -r '.items[] | select(.customerCode=="CUS-SALES-01") | .id')"
[[ -z "${has_sales_customer_leak}" ]] && ok "Assigned User's customer list does NOT contain Sales' customer" || bad "Sales' customer leaked into Accounting's list"

req GET "/customers/${accounting_customer_id}" "${assigned_user_token}" ""
expect_status "Assigned User reads Accounting's customer by id" 200 "${status}"
req GET /customers/by-mst/0101111111 "${assigned_user_token}" ""
expect_status "Assigned User finds Accounting's customer by MST" 200 "${status}"

req GET /suppliers "${assigned_user_token}" ""
expect_status "Assigned User lists Department suppliers" 200 "${status}"
has_acc_supplier="$(echo "${body}" | jq -r ".items[] | select(.id==\"${accounting_supplier_id}\") | .id")"
[[ "${has_acc_supplier}" == "${accounting_supplier_id}" ]] && ok "Assigned User's supplier list contains Accounting's seeded supplier" || bad "Accounting's supplier missing from list"

req GET /customers "${unassigned_user_token}" ""
expect_status "Unassigned User cannot list customers (no customer.management)" 403 "${status}"
req GET /suppliers "${unassigned_user_token}" ""
expect_status "Unassigned User cannot list suppliers (no supplier.management)" 403 "${status}"

req GET /customers "${admin_sales_token}" ""
expect_status "Admin Sales's own customer list works (own Department)" 200 "${status}"
sales_admin_sees_accounting_customer="$(echo "${body}" | jq -r ".items[] | select(.id==\"${accounting_customer_id}\") | .id")"
[[ -z "${sales_admin_sees_accounting_customer}" ]] && ok "Admin Sales's customer list does NOT contain Accounting's customer" || bad "Admin Sales leaked Accounting's customer"
req GET "/customers/${accounting_customer_id}" "${admin_sales_token}" ""
expect_status "Admin Sales cannot fetch Accounting's customer directly" 404 "${status}"

echo ""
echo "== §7 Tenant B: fully isolated from Tenant A's Business Module data =="
req POST /auth/login "" '{"email":"owner-b@bmacltest.local","password":"OwnerPass123!"}'
owner_b_token="$(jqf .token)"
req GET /vouchers "${owner_b_token}" ""
expect_status "Tenant B owner is not a Department principal for vouchers either" 403 "${status}"

tenant_b_default_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_b}' AND is_default")"
req POST /tenant/admins "${owner_b_token}" "{\"email\":\"admin-b@bmacltest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin B\",\"departmentId\":\"${tenant_b_default_dept}\"}"
expect_status "owner B creates Admin B (own tenant, own department)" 201 "${status}"
req POST /auth/login "" '{"email":"admin-b@bmacltest.local","password":"AdminPass123!"}'
admin_b_token="$(jqf .token)"

req GET /vouchers "${admin_b_token}" ""
expect_status "Admin B (Tenant B) can call the vouchers endpoint for their own tenant/department" 200 "${status}"
admin_b_sees_tenant_a="$(echo "${body}" | jq -r '.items | length')"
[[ "${admin_b_sees_tenant_a}" == "0" ]] && ok "Admin B's voucher list contains none of Tenant A's data" || bad "Tenant B leaked Tenant A vouchers"

req GET "/vouchers/${admin_voucher_id}" "${admin_b_token}" ""
expect_status "Admin B cannot fetch Tenant A's voucher by id (cross-tenant denied)" 404 "${status}"

req GET "/customers/${accounting_customer_id}" "${admin_b_token}" ""
expect_status "Admin B cannot fetch Tenant A's customer by id (cross-tenant denied)" 404 "${status}"

echo ""
echo "================================================================"
echo "BUSINESS MODULE ACL E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
