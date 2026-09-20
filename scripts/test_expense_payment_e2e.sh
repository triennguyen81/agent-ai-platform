#!/usr/bin/env bash
# Luồng 03 — Hóa đơn thanh toán (expense claim payment request) real HTTP
# E2E.
#
# Boots an isolated, empty Postgres + freshly built Gateway image (own
# network/volume, never touches the shared dev stack) and verifies:
#   - Deterministic outcome classification (POST /vouchers/{id}/expense-claim
#     -result/propose) against real sim_employees/sim_suppliers/vouchers
#     data: APPROVED, PARTIALLY_APPROVED (policy cap), RETURN_FOR_DOCUMENTS
#     (missing receipt), WAITING_EXCEPTION_APPROVAL (policy cap + exception
#     allowed) outcomes, plus REJECTED via explicit reject.
#   - Duplicate detection: an item whose vendor_mst+invoice_no matches an
#     already-booked voucher is blocked, not silently approved.
#   - Advance reconciliation both directions (approved > advance and
#     advance > approved).
#   - Supplier resolution against sim_suppliers.
#   - ADMIN confirm settles sim_employees (outstanding_advance/ytd_expense)
#     with the APPROVED amount, never the raw requested amount.
#   - ADMIN grant-exception un-caps a WAITING_EXCEPTION_APPROVAL claim.
#   - Idempotent retry (re-propose upserts in place; re-apply short-circuits).
#   - ACL: creator-or-admin propose, ADMIN-only reject/grant-exception/export,
#     Department-scoped read, cross-Department/Tenant denied.
#   - No direct ledger/payment execution: propose never touches
#     sim_employees; export never marks payment executed.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-exppay-e2e-net-${run_id}"
pg="aiq-exppay-e2e-postgres-${run_id}"
gw="aiq-exppay-e2e-gateway-${run_id}"
vol="aiq-exppay-e2e-pgdata-${run_id}"
db="aiq_exppay_e2e"
db_password="exppay_e2e_password"
image="aiq-agentic-gateway:contracttest"
base="http://localhost:18090"
gw_port=18090

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
    local args=(-sS -o /tmp/exppay_e2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/exppay_e2e_body.$$)"
    rm -f /tmp/exppay_e2e_body.$$
}

jqf() { echo "${body}" | jq -r "$1"; }

psqlc() { docker exec "${pg}" psql -v ON_ERROR_STOP=1 -At -U postgres -d "${db}" -c "$1" | { grep -v -E '^(INSERT|UPDATE|DELETE) [0-9]+( [0-9]+)?$' || true; }; }

# R14 — runs the mandatory 3-stage chain (dept review -> accounting review
# -> payment approval) using 3 distinct Accounting-dept admins
# (admin_accounting_token/admin_accounting2_token/admin_accounting3_token,
# set later once logins happen). Asserts each stage returns 200; leaves
# $body/$status set to the final (payment-approval) response.
approve_chain() {
    local voucher_id="$1" label="$2"
    req POST "/vouchers/${voucher_id}/expense-claim/department-review" "${admin_accounting_token}" ""
    expect_status "${label}: stage 1 department-review" 200 "${status}"
    req POST "/vouchers/${voucher_id}/expense-claim/accounting-review" "${admin_accounting2_token}" ""
    expect_status "${label}: stage 2 accounting-review" 200 "${status}"
    req POST "/vouchers/${voucher_id}/apply-expense" "${admin_accounting3_token}" ""
    expect_status "${label}: stage 3 payment-approval (apply-expense)" 200 "${status}"
}

echo "== Expense Payment (Luồng 03) E2E: booting isolated stack =="
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
    --env Jwt__Issuer=aiq.exppay.test --env Jwt__Audience=aiq.exppay.client \
    --env Jwt__SigningKey=exppay-e2e-test-signing-key-needs-32-bytes-long \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://minio.invalid:9000 --env Object__AccessKey=exppaye2e --env Object__SecretKey=exppaye2epassword \
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

req POST /auth/register "" '{"email":"root@exppaytest.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"

docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@exppaytest.local' -f /tmp/first-super-admin.sql >/dev/null

req POST /auth/login "" '{"email":"root@exppaytest.local","password":"RootPass123!"}'
super_token="$(jqf .token)"

echo "== Setup: Tenant A (+ Sales dept), Tenant B, Business Agent granting voucher.processing =="
req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-exppaytest","name":"Tenant A","ownerEmail":"owner-a@exppaytest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
expect_status "onboard Tenant A" 201 "${status}"
tenant_a="$(jqf .tenant.id)"

req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-b-exppaytest","name":"Tenant B","ownerEmail":"owner-b@exppaytest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner B"}'
expect_status "onboard Tenant B" 201 "${status}"
tenant_b="$(jqf .tenant.id)"

req GET /system/capabilities "${super_token}" ""
voucher_cap="$(echo "${body}" | jq -r '.items[] | select(.code=="voucher.processing") | .id')"
[[ -n "${voucher_cap}" && "${voucher_cap}" != "null" ]] && ok "voucher.processing exists in catalog" || bad "voucher.processing missing"

req POST /system/agents "${super_token}" '{"slug":"exppay-agent-e2e","name":"Expense Payment Agent","actionTier":"suggest"}'
expect_status "create Expense Payment Agent" 201 "${status}"
agent_id="$(jqf .id)"
req GET "/system/agents/${agent_id}/versions" "${super_token}" ""
draft_version_id="$(jqf '.items[0].id')"
req PATCH "/system/agents/${agent_id}/versions/${draft_version_id}" "${super_token}" "{\"capabilityIds\":[\"${voucher_cap}\"]}"
expect_status "assign voucher.processing to the draft" 200 "${status}"
req POST "/system/agents/${agent_id}/versions/${draft_version_id}/publish" "${super_token}" ""
expect_status "publish version" 200 "${status}"
req PUT "/system/agents/${agent_id}/tenants/${tenant_a}" "${super_token}" ""
expect_status "assign Expense Payment Agent to Tenant A" 204 "${status}"

docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status)
  SELECT id,'sales-exppaytest','Sales','active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
accounting_dept="$(psqlc "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
sales_dept="$(psqlc "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-exppaytest'")"

req POST /auth/login "" '{"email":"owner-a@exppaytest.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner assigns Expense Payment Agent to Accounting dept" 204 "${status}"
req PUT "/tenant/departments/${sales_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner assigns Expense Payment Agent to Sales dept" 204 "${status}"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@exppaytest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@exppaytest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "owner creates Admin Sales" 201 "${status}"
# R14 — the 3-stage approval chain needs 3 DISTINCT Accounting-dept admins
# (department review / accounting review / payment approval must each be a
# different actor).
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting2@exppaytest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting 2\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting 2" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting3@exppaytest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting 3\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting 3" 201 "${status}"
req POST /auth/login "" '{"email":"admin-accounting@exppaytest.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
req POST /auth/login "" '{"email":"admin-sales@exppaytest.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"
req POST /auth/login "" '{"email":"admin-accounting2@exppaytest.local","password":"AdminPass123!"}'
admin_accounting2_token="$(jqf .token)"
req POST /auth/login "" '{"email":"admin-accounting3@exppaytest.local","password":"AdminPass123!"}'
admin_accounting3_token="$(jqf .token)"

req PUT "/department/capabilities/${voucher_cap}/binding" "${admin_accounting_token}" "{\"agentId\":\"${agent_id}\"}"
expect_status "Admin Accounting binds voucher.processing -> Expense Payment Agent" 204 "${status}"

req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user@exppaytest.local","password":"UserPass123!","displayName":"Assigned User"}'
expect_status "Admin Accounting creates Assigned User" 201 "${status}"
assigned_user_id="$(psqlc "SELECT id FROM users WHERE email='assigned-user@exppaytest.local'")"
req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user2@exppaytest.local","password":"UserPass123!","displayName":"Assigned User 2"}'
expect_status "Admin Accounting creates Assigned User 2" 201 "${status}"
assigned_user2_id="$(psqlc "SELECT id FROM users WHERE email='assigned-user2@exppaytest.local'")"
req POST /tenant/users "${admin_accounting_token}" '{"email":"unassigned-user@exppaytest.local","password":"UserPass123!","displayName":"Unassigned User"}'
expect_status "Admin Accounting creates Unassigned User" 201 "${status}"

req PUT "/tenant/agents/${agent_id}/users/${assigned_user_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Expense Payment Agent to Assigned User" 204 "${status}"
req PUT "/tenant/agents/${agent_id}/users/${assigned_user2_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Expense Payment Agent to Assigned User 2" 204 "${status}"

req POST /auth/login "" '{"email":"assigned-user@exppaytest.local","password":"UserPass123!"}'
assigned_user_token="$(jqf .token)"
req POST /auth/login "" '{"email":"assigned-user2@exppaytest.local","password":"UserPass123!"}'
assigned_user2_token="$(jqf .token)"
req POST /auth/login "" '{"email":"unassigned-user@exppaytest.local","password":"UserPass123!"}'
unassigned_user_token="$(jqf .token)"

echo ""
echo "== §1 Seed fixtures: 4 employees, 2 suppliers, one already-booked invoice (for duplicate test) =="
emp1="$(psqlc "INSERT INTO sim_employees(tenant_id,employee_code,full_name,department,monthly_expense_budget,outstanding_advance) VALUES ('${tenant_a}','NV-01','Nguyen Van A','Kinh doanh',0,5000000) RETURNING id")"
emp3="$(psqlc "INSERT INTO sim_employees(tenant_id,employee_code,full_name,department,monthly_expense_budget,outstanding_advance) VALUES ('${tenant_a}','NV-03','Le Van C','Van phong',0,0) RETURNING id")"
emp4="$(psqlc "INSERT INTO sim_employees(tenant_id,employee_code,full_name,department,monthly_expense_budget,outstanding_advance) VALUES ('${tenant_a}','NV-04','Pham Thi D','Van phong',0,3000000) RETURNING id")"
emp5="$(psqlc "INSERT INTO sim_employees(tenant_id,employee_code,full_name,department,monthly_expense_budget,outstanding_advance) VALUES ('${tenant_a}','NV-05','Tran Van E','Van phong',0,0) RETURNING id")"
sup1="$(psqlc "INSERT INTO sim_suppliers(tenant_id,department_id,supplier_code,name,mst) VALUES ('${tenant_a}','${accounting_dept}','SUP-01','Vietjet Air','0200000001') RETURNING id")"
sup2="$(psqlc "INSERT INTO sim_suppliers(tenant_id,department_id,supplier_code,name,mst) VALUES ('${tenant_a}','${accounting_dept}','SUP-02','KS Melia','0200000002') RETURNING id")"
ok "seeded employees NV-01/NV-03/NV-04 + suppliers SUP-01/SUP-02"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"invoice_vat_in","documentNumber":"INV-A-001","sellerMst":"0200000001","totalAmount":500000,"currency":"VND"}'
expect_status "seed already-booked invoice INV-A-001/0200000001 (for duplicate test)" 201 "${status}"

echo ""
echo "== §2 Scenario 1 — valid claim: Supplier resolved, approved, advance reconciled (approved > advance), confirm, export =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-001"}'
expect_status "create V1 (Assigned User, own claim)" 201 "${status}"
v1="$(jqf .voucher.id)"
v1_payload=$(cat <<EOF
{"voucherType":"expense_claim","documentNumber":"EC-001","extractedFields":{"employee_code":"NV-01","total_claim":3500000,"advance_taken":3000000,"currency":"VND","items":[{"category":"airfare","description":"Ve may bay SGN-HAN","amount":3500000,"receipt_attached":true,"vendor_mst":"0200000001","invoice_no":"INV-A-101"}]}}
EOF
)
req PATCH "/vouchers/${v1}/apply-extraction" "${assigned_user_token}" "${v1_payload}"
expect_status "Assigned User stages extracted_fields for V1" 200 "${status}"

emp1_advance_before_propose="$(psqlc "SELECT outstanding_advance FROM sim_employees WHERE id='${emp1}'")"
req POST "/vouchers/${v1}/expense-claim-result/propose" "${assigned_user_token}" ""
expect_status "Assigned User (creator) proposes V1" 200 "${status}"
[[ "$(jqf .outcome)" == "APPROVED" ]] && ok "V1 -> APPROVED" || bad "V1 outcome: ${body}"
[[ "$(jqf .approvedAmount)" == "3500000.00" || "$(jqf .approvedAmount)" == "3500000" ]] && ok "V1 approvedAmount=3500000" || bad "V1 approvedAmount: ${body}"
[[ "$(jqf .advanceAmount)" == "3000000.00" || "$(jqf .advanceAmount)" == "3000000" ]] && ok "V1 advanceAmount=3000000" || bad "V1 advanceAmount: ${body}"
[[ "$(jqf .payableAmount)" == "500000.00" || "$(jqf .payableAmount)" == "500000" ]] && ok "V1 payableAmount=500000 (approved > advance, §7)" || bad "V1 payableAmount: ${body}"
[[ "$(echo "${body}" | jq -r '.resolvedVendorsJson' | jq -r '.[0].resolved')" == "true" ]] && ok "V1 Supplier resolved (SUP-01 via mst)" || bad "V1 resolvedVendors: ${body}"

echo "== §15a propose never touches the ledger =="
emp1_advance_after_propose="$(psqlc "SELECT outstanding_advance FROM sim_employees WHERE id='${emp1}'")"
[[ "${emp1_advance_before_propose}" == "${emp1_advance_after_propose}" ]] && ok "propose did not change sim_employees.outstanding_advance" || bad "propose mutated ledger: before=${emp1_advance_before_propose} after=${emp1_advance_after_propose}"

req GET "/vouchers/${v1}/expense-claim-export" "${admin_accounting_token}" ""
expect_status "export blocked before confirm" 400 "${status}"

# T1 fix — V1 carries a real invoice_no (INV-A-101), so payment approval is
# now gated on tax-portal verification (internal control gate #1).
req POST "/vouchers/${v1}/tax-verification/confirm" "${assigned_user_token}" ""
expect_status "T1 V1 tax verification confirmed before settlement" 200 "${status}"
approve_chain "${v1}" "V1"
[[ "$(jqf '.expense_claim_result.reviewStatus')" == "confirmed" ]] && ok "V1 reviewStatus=confirmed after the 3-stage chain" || bad "V1 apply result: ${body}"

emp1_row="$(psqlc "SELECT outstanding_advance || ',' || ytd_expense FROM sim_employees WHERE id='${emp1}'")"
[[ "${emp1_row}" == "2000000.00,3500000.00" ]] && ok "NV-01 settled: outstanding_advance=2,000,000 ytd_expense=3,500,000 (approved amount, not raw requested)" || bad "NV-01 settlement: ${emp1_row}"

req GET "/vouchers/${v1}/expense-claim-export" "${admin_accounting_token}" ""
expect_status "export available after confirm" 200 "${status}"
[[ "$(jqf .readyForPayment)" == "true" ]] && ok "export readyForPayment=true" || bad "export: ${body}"
[[ "$(jqf .paymentExecuted)" == "false" ]] && ok "§15b export never marks paymentExecuted=true (no real payment evidence)" || bad "export paymentExecuted: ${body}"

echo ""
echo "== §3 Scenario 2 — policy-limit reduction (hard cap, no exception) -> PARTIALLY_APPROVED =="
psqlc "INSERT INTO department_expense_policies(tenant_id,department_id,category,limit_amount,limit_unit,allow_exception,requires_receipt) VALUES ('${tenant_a}','${accounting_dept}','hotel',2400000,'per_item',false,true)" >/dev/null

req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-002"}'
v2="$(jqf .voucher.id)"
v2_payload=$(cat <<EOF
{"voucherType":"expense_claim","documentNumber":"EC-002","extractedFields":{"employee_code":"NV-03","total_claim":3000000,"advance_taken":0,"currency":"VND","items":[{"category":"hotel","description":"KS Melia 3 dem","amount":3000000,"receipt_attached":true,"vendor_mst":"0200000002","invoice_no":"INV-H-201"}]}}
EOF
)
req PATCH "/vouchers/${v2}/apply-extraction" "${assigned_user_token}" "${v2_payload}"
req POST "/vouchers/${v2}/expense-claim-result/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "PARTIALLY_APPROVED" ]] && ok "V2 -> PARTIALLY_APPROVED (policy cap, no exception configured)" || bad "V2 outcome: ${body}"
[[ "$(jqf .approvedAmount)" == "2400000.00" || "$(jqf .approvedAmount)" == "2400000" ]] && ok "V2 approvedAmount capped at configured 2,400,000 (never hardcoded)" || bad "V2 approvedAmount: ${body}"
[[ "$(jqf .cappedAmount)" == "600000.00" || "$(jqf .cappedAmount)" == "600000" ]] && ok "V2 cappedAmount=600000" || bad "V2 cappedAmount: ${body}"

echo ""
echo "== §4 Scenario 3 — duplicate invoice (already booked as INV-A-001/0200000001) -> blocked, not silently approved =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-003"}'
v3="$(jqf .voucher.id)"
v3_payload=$(cat <<EOF
{"voucherType":"expense_claim","documentNumber":"EC-003","extractedFields":{"employee_code":"NV-03","total_claim":700000,"advance_taken":0,"currency":"VND","items":[{"category":"meal","description":"An trua","amount":200000,"receipt_attached":true},{"category":"airfare","description":"Ve may bay (da ghi so o noi khac)","amount":500000,"receipt_attached":true,"vendor_mst":"0200000001","invoice_no":"INV-A-001"}]}}
EOF
)
req PATCH "/vouchers/${v3}/apply-extraction" "${assigned_user_token}" "${v3_payload}"
req POST "/vouchers/${v3}/expense-claim-result/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "PARTIALLY_APPROVED" ]] && ok "V3 -> PARTIALLY_APPROVED (1 legit + 1 duplicate line)" || bad "V3 outcome: ${body}"
[[ "$(jqf .approvedAmount)" == "200000.00" || "$(jqf .approvedAmount)" == "200000" ]] && ok "V3 approvedAmount excludes the duplicate line (no duplicate accounting suggestion)" || bad "V3 approvedAmount: ${body}"
[[ "$(jqf .blockedAmount)" == "500000.00" || "$(jqf .blockedAmount)" == "500000" ]] && ok "V3 blockedAmount=500000 (the duplicate line)" || bad "V3 blockedAmount: ${body}"
dup_reason="$(echo "${body}" | jq -r '.duplicateFlagsJson' | jq -r '.[0].reason')"
[[ "${dup_reason}" == "duplicate_of_existing_voucher" ]] && ok "duplicate flagged against the already-booked voucher" || bad "duplicate reason: ${dup_reason}"

echo ""
echo "== §5 Scenario 4 — missing supporting document (policy requires_receipt) -> RETURN_FOR_DOCUMENTS =="
psqlc "INSERT INTO department_expense_policies(tenant_id,department_id,category,limit_amount,limit_unit,allow_exception,requires_receipt) VALUES ('${tenant_a}','${accounting_dept}','gift',100000000,'per_claim',false,true)" >/dev/null

req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-004"}'
v4="$(jqf .voucher.id)"
v4_payload=$(cat <<EOF
{"voucherType":"expense_claim","documentNumber":"EC-004","extractedFields":{"employee_code":"NV-03","total_claim":300000,"advance_taken":0,"currency":"VND","items":[{"category":"gift","description":"Qua tang khach hang","amount":300000,"receipt_attached":false}]}}
EOF
)
req PATCH "/vouchers/${v4}/apply-extraction" "${assigned_user_token}" "${v4_payload}"
req POST "/vouchers/${v4}/expense-claim-result/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "RETURN_FOR_DOCUMENTS" ]] && ok "V4 -> RETURN_FOR_DOCUMENTS (missing receipt on a receipt-required category)" || bad "V4 outcome: ${body}"
[[ "$(jqf .missingDocuments)" != "null" ]] && ok "V4 states exactly what is missing: $(jqf .missingDocuments)" || bad "V4 missingDocuments empty"
n="$(psqlc "SELECT COUNT(*) FROM notifications WHERE ref_id='${v4}' AND kind='expense_claim_returned' AND user_id='${assigned_user_id}'")"
[[ "${n}" == "1" ]] && ok "R18: RETURN_FOR_DOCUMENTS notifies the claim's creator exactly once" || bad "R18 RETURN_FOR_DOCUMENTS notification count: ${n}"
req POST "/vouchers/${v4}/apply-expense" "${admin_accounting_token}" ""
expect_status "RETURN_FOR_DOCUMENTS cannot be applied (no final journal suggestion)" 400 "${status}"

echo ""
echo "== §6 Scenario 5 — policy exception (cap + allow_exception) -> WAITING_EXCEPTION_APPROVAL -> ADMIN grants =="
psqlc "INSERT INTO department_expense_policies(tenant_id,department_id,category,limit_amount,limit_unit,allow_exception,requires_receipt) VALUES ('${tenant_a}','${accounting_dept}','conference',2000000,'per_item',true,true)" >/dev/null

req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-005"}'
v5="$(jqf .voucher.id)"
v5_payload=$(cat <<EOF
{"voucherType":"expense_claim","documentNumber":"EC-005","extractedFields":{"employee_code":"NV-03","total_claim":3000000,"advance_taken":0,"currency":"VND","items":[{"category":"conference","description":"Hoi thao nganh","amount":3000000,"receipt_attached":true,"vendor_mst":"0200000001","invoice_no":"INV-C-301"}]}}
EOF
)
req PATCH "/vouchers/${v5}/apply-extraction" "${assigned_user_token}" "${v5_payload}"
req POST "/vouchers/${v5}/expense-claim-result/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "WAITING_EXCEPTION_APPROVAL" ]] && ok "V5 -> WAITING_EXCEPTION_APPROVAL" || bad "V5 outcome: ${body}"
[[ "$(jqf .approvedAmount)" == "2000000.00" || "$(jqf .approvedAmount)" == "2000000" ]] && ok "V5 provisional approvedAmount=capped 2,000,000 pending decision" || bad "V5 approvedAmount: ${body}"
n="$(psqlc "SELECT COUNT(*) FROM notifications WHERE ref_id='${v5}' AND kind='expense_claim_exception_pending'")"
[[ "${n}" == "3" ]] && ok "R18: WAITING_EXCEPTION_APPROVAL notifies all 3 Accounting-dept admins" || bad "R18 WAITING_EXCEPTION_APPROVAL notification count: ${n}"

req POST "/vouchers/${v5}/apply-expense" "${assigned_user_token}" ""
expect_status "Assigned User (non-admin) cannot apply/decide V5" 403 "${status}"

req POST "/vouchers/${v5}/grant-expense-exception" "${admin_accounting_token}" ""
expect_status "ADMIN grants the exception on V5" 200 "${status}"
[[ "$(jqf .outcome)" == "APPROVED" ]] && ok "V5 -> APPROVED after grant (un-capped)" || bad "V5 post-grant outcome: ${body}"
[[ "$(jqf .approvedAmount)" == "3000000.00" || "$(jqf .approvedAmount)" == "3000000" ]] && ok "V5 approvedAmount un-capped to full 3,000,000" || bad "V5 post-grant approvedAmount: ${body}"
# R14 — per the business doc ("...sau đó quay lại nhánh duyệt"), granting
# an exception does NOT itself settle the ledger — it resets to 'proposed'
# so the claim still has to go through the full 3-stage chain.
[[ "$(jqf .reviewStatus)" == "proposed" ]] && ok "V5 returns to 'proposed' after grant — exception-granting alone never settles the ledger" || bad "V5 post-grant reviewStatus: ${body}"
emp3_ytd_after_grant="$(psqlc "SELECT ytd_expense FROM sim_employees WHERE id='${emp3}'")"
[[ "${emp3_ytd_after_grant}" == "0.00" ]] && ok "grant-exception alone does not touch sim_employees" || bad "NV-03 ytd_expense after grant (should still be 0): ${emp3_ytd_after_grant}"

# T1 fix — V5 carries a real invoice_no (INV-C-301).
req POST "/vouchers/${v5}/tax-verification/confirm" "${assigned_user_token}" ""
expect_status "T1 V5 tax verification confirmed before settlement" 200 "${status}"
approve_chain "${v5}" "V5 (post-grant)"
emp3_ytd="$(psqlc "SELECT ytd_expense FROM sim_employees WHERE id='${emp3}'")"
# V2/V3/V4 (also NV-03) were only ever proposed, never confirmed — only the
# completed 3-stage chain touches sim_employees, so V5's un-capped
# 3,000,000 is the only amount that should have posted.
[[ "${emp3_ytd}" == "3000000.00" ]] && ok "NV-03 ytd_expense=3,000,000 — only V5 (completed the full chain) posted; V2/V3/V4 (proposed-only/returned) never touched the ledger" || bad "NV-03 ytd_expense: ${emp3_ytd}"

echo ""
echo "== §7 Scenario 6 — advance > approved expense -> employee owes company back =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-006"}'
v6="$(jqf .voucher.id)"
v6_payload=$(cat <<EOF
{"voucherType":"expense_claim","documentNumber":"EC-006","extractedFields":{"employee_code":"NV-04","total_claim":1000000,"advance_taken":3000000,"currency":"VND","items":[{"category":"meal","description":"An uong cong tac","amount":1000000,"receipt_attached":true}]}}
EOF
)
req PATCH "/vouchers/${v6}/apply-extraction" "${assigned_user_token}" "${v6_payload}"
req POST "/vouchers/${v6}/expense-claim-result/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "APPROVED" ]] && ok "V6 -> APPROVED" || bad "V6 outcome: ${body}"
[[ "$(jqf .payableAmount)" == "-2000000.00" || "$(jqf .payableAmount)" == "-2000000" ]] && ok "V6 payableAmount=-2,000,000 (NV-04 owes company back, §6)" || bad "V6 payableAmount: ${body}"

approve_chain "${v6}" "V6"
emp4_row="$(psqlc "SELECT outstanding_advance || ',' || ytd_expense FROM sim_employees WHERE id='${emp4}'")"
[[ "${emp4_row}" == "0.00,1000000.00" ]] && ok "NV-04 settled: outstanding_advance=0 (fully consumed) ytd_expense=1,000,000" || bad "NV-04 settlement: ${emp4_row}"

echo ""
echo "== §8 Idempotent retry =="
req POST "/vouchers/${v1}/expense-claim-result/propose" "${assigned_user_token}" ""
expect_status "re-propose on an already-confirmed claim does not throw" 200 "${status}"
[[ "$(jqf .reviewStatus)" == "confirmed" ]] && ok "re-propose on confirmed V1 returns the settled row unchanged" || bad "re-propose confirmed: ${body}"
req POST "/vouchers/${v1}/apply-expense" "${admin_accounting_token}" ""
expect_status "re-apply on an already-verified voucher is idempotent" 200 "${status}"
[[ "$(jqf .status)" == "already_applied" ]] && ok "re-apply short-circuits as already_applied" || bad "re-apply: ${body}"
emp1_row_after_retry="$(psqlc "SELECT outstanding_advance || ',' || ytd_expense FROM sim_employees WHERE id='${emp1}'")"
[[ "${emp1_row_after_retry}" == "2000000.00,3500000.00" ]] && ok "retry does not double-post NV-01's ledger" || bad "NV-01 after retry: ${emp1_row_after_retry}"

echo "== §8b reject -> re-propose recovers (upsert guard must not throw once rejected) =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-007"}'
v7="$(jqf .voucher.id)"
v7_payload=$(cat <<EOF
{"voucherType":"expense_claim","documentNumber":"EC-007","extractedFields":{"employee_code":"NV-03","total_claim":100000,"advance_taken":0,"currency":"VND","items":[{"category":"meal","description":"An trua","amount":100000,"receipt_attached":true}]}}
EOF
)
req PATCH "/vouchers/${v7}/apply-extraction" "${assigned_user_token}" "${v7_payload}"
req POST "/vouchers/${v7}/expense-claim-result/propose" "${assigned_user_token}" ""
req POST "/vouchers/${v7}/reject-expense-claim" "${admin_accounting_token}" ""
expect_status "ADMIN rejects V7" 200 "${status}"
[[ "$(jqf .reviewStatus)" == "rejected" ]] && ok "V7 review_status=rejected" || bad "V7 reject: ${body}"
n="$(psqlc "SELECT COUNT(*) FROM notifications WHERE ref_id='${v7}' AND kind='expense_claim_rejected' AND user_id='${assigned_user_id}'")"
[[ "${n}" == "1" ]] && ok "R18: rejection notifies the claim's creator exactly once" || bad "R18 expense_claim_rejected notification count: ${n}"
req POST "/vouchers/${v7}/expense-claim-result/propose" "${assigned_user_token}" ""
expect_status "re-propose after reject succeeds (no throw)" 200 "${status}"
[[ "$(jqf .reviewStatus)" == "proposed" ]] && ok "re-propose resets V7 back to 'proposed'" || bad "V7 re-propose: ${body}"

echo ""
echo "== §9 ACL: USER edit-own PASS / edit-other DENIED =="
# V1 is already 'verified' (finalized by §2's apply-expense) — a finalized
# voucher correctly rejects edits (409 voucher_finalized), so use V2
# (still 'proposed', never applied) to prove the ACL rule itself.
req PATCH "/vouchers/${v2}" "${assigned_user_token}" '{"documentNumber":"EC-002-edited"}'
expect_status "Assigned User edits their own (not-yet-finalized) voucher" 200 "${status}"
req PATCH "/vouchers/${v2}" "${assigned_user2_token}" '{"documentNumber":"EC-002-hijack"}'
expect_status "Assigned User 2 cannot edit Assigned User's voucher" 403 "${status}"

echo "== §10 ACL: creator-or-admin propose; ADMIN-only reject/grant-exception/export =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-008"}'
v8="$(jqf .voucher.id)"
req POST "/vouchers/${v8}/expense-claim-result/propose" "${assigned_user2_token}" ""
expect_status "Assigned User 2 (not creator, not admin) cannot propose on V1's own new claim" 403 "${status}"
req POST "/vouchers/${v8}/reject-expense-claim" "${assigned_user_token}" ""
expect_status "Assigned User (creator, non-admin) cannot reject (ADMIN-only)" 403 "${status}"

echo "== §11 USER without capability DENIED =="
req GET "/vouchers/${v1}/expense-claim-result" "${unassigned_user_token}" ""
expect_status "Unassigned User cannot read expense-claim-result" 403 "${status}"

echo "== §12 Cross-Department / cross-Tenant DENIED =="
req GET "/vouchers/${v1}/expense-claim-result" "${admin_sales_token}" ""
expect_status "Admin Sales (different Department) cannot read Accounting's expense-claim-result" 404 "${status}"

# /auth/* is a global 10/min fixed-window limiter (same pacing fix used in
# the Supplier/Expense-AP E2E scripts). R14's 3-stage chain needs 2 extra
# admin accounts registered+logged-in earlier in this run, which pushes the
# cumulative /auth/* call count past the window here — wait it out before
# the remaining logins.
sleep 61
req POST /auth/login "" '{"email":"owner-b@exppaytest.local","password":"OwnerPass123!"}'
owner_b_token="$(jqf .token)"
tenant_b_default_dept="$(psqlc "SELECT id FROM departments WHERE tenant_id='${tenant_b}' AND is_default")"
req POST /tenant/admins "${owner_b_token}" "{\"email\":\"admin-b@exppaytest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin B\",\"departmentId\":\"${tenant_b_default_dept}\"}"
expect_status "owner B creates Admin B (own tenant, own department)" 201 "${status}"
req POST /auth/login "" '{"email":"admin-b@exppaytest.local","password":"AdminPass123!"}'
admin_b_token="$(jqf .token)"
req GET "/vouchers/${v1}/expense-claim-result" "${admin_b_token}" ""
expect_status "Admin B (Tenant B) cannot read Tenant A's expense-claim-result (cross-tenant denied)" 404 "${status}"

echo ""
echo "== §13 R16 — separation of duties: ADMIN cannot confirm/reject/grant-exception on a claim they themselves created =="
req POST /vouchers "${admin_accounting_token}" '{"voucherType":"expense_claim","documentNumber":"EC-SELF-01"}'
expect_status "ADMIN creates their own expense claim" 201 "${status}"
v_self="$(jqf .voucher.id)"
self_payload=$(cat <<EOF
{"voucherType":"expense_claim","documentNumber":"EC-SELF-01","extractedFields":{"employee_code":"NV-03","total_claim":100000,"advance_taken":0,"currency":"VND","items":[{"category":"meal","description":"An trua","amount":100000,"receipt_attached":true}]}}
EOF
)
req PATCH "/vouchers/${v_self}/apply-extraction" "${admin_accounting_token}" "${self_payload}"
req POST "/vouchers/${v_self}/expense-claim-result/propose" "${admin_accounting_token}" ""
expect_status "ADMIN (creator) may still propose/classify their own claim (read-side, not a ledger consequence)" 200 "${status}"
req POST "/vouchers/${v_self}/apply-expense" "${admin_accounting_token}" ""
expect_status "ADMIN cannot apply-expense on their own claim (self-approval blocked)" 403 "${status}"
req POST "/vouchers/${v_self}/reject-expense-claim" "${admin_accounting_token}" ""
expect_status "ADMIN cannot reject their own claim either" 403 "${status}"
req GET "/vouchers/${v_self}/expense-claim-result" "${admin_accounting_token}" ""
[[ "$(jqf .reviewStatus)" == "proposed" ]] && ok "claim remains unsettled after both self-approval attempts were blocked" || bad "unexpected reviewStatus: ${body}"

echo ""
echo "== §14 R15 — VAT-deductible / CIT-deductible are separate values, never fabricated =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-VAT-01"}'
expect_status "create V_VAT" 201 "${status}"
v_vat="$(jqf .voucher.id)"
v_vat_payload=$(cat <<EOF
{"voucherType":"expense_claim","documentNumber":"EC-VAT-01","extractedFields":{"employee_code":"NV-05","total_claim":4650000,"advance_taken":0,"currency":"VND","items":[
  {"category":"meal","description":"An uong co hoa don","amount":1100000,"vat_amount":100000,"receipt_attached":true,"invoice_no":"INV-VAT-01"},
  {"category":"meal","description":"Taxi khong hoa don","amount":550000,"vat_amount":50000,"receipt_attached":true},
  {"category":"hotel","description":"KS Melia 3 dem","amount":3000000,"vat_amount":300000,"receipt_attached":true,"vendor_mst":"0200000002","invoice_no":"INV-VAT-02"}
]}}
EOF
)
req PATCH "/vouchers/${v_vat}/apply-extraction" "${assigned_user_token}" "${v_vat_payload}"
expect_status "Assigned User stages extracted_fields for V_VAT" 200 "${status}"
req POST "/vouchers/${v_vat}/expense-claim-result/propose" "${assigned_user_token}" ""
expect_status "propose V_VAT" 200 "${status}"
[[ "$(jqf .outcome)" == "PARTIALLY_APPROVED" ]] && ok "V_VAT -> PARTIALLY_APPROVED (hotel line capped by existing per-item policy)" || bad "V_VAT outcome: ${body}"
[[ "$(jqf .approvedAmount)" == "4050000.00" || "$(jqf .approvedAmount)" == "4050000" ]] && ok "V_VAT approvedAmount=4,050,000 (1,100,000 + 550,000 + 2,400,000 capped)" || bad "V_VAT approvedAmount: ${body}"
[[ "$(jqf .nonDeductibleAmount)" == "550000.00" || "$(jqf .nonDeductibleAmount)" == "550000" ]] && ok "CIT-non-deductible=550,000 (only the no-invoice taxi line)" || bad "V_VAT nonDeductibleAmount: ${body}"
[[ "$(jqf .vatAmount)" == "450000.00" || "$(jqf .vatAmount)" == "450000" ]] && ok "vatAmount=450,000 (sum of all 3 lines' identified VAT, never fabricated)" || bad "V_VAT vatAmount: ${body}"
[[ "$(jqf .vatDeductibleAmount)" == "340000.00" || "$(jqf .vatDeductibleAmount)" == "340000" ]] && ok "vatDeductibleAmount=340,000 (100,000 full + 0 no-invoice + 240,000 prorated for the capped hotel line — VAT and CIT tracked independently)" || bad "V_VAT vatDeductibleAmount: ${body}"

echo ""
echo "== §17 T1 — tax-portal verification is an enforced gate, not just a capability =="
req POST "/vouchers/${v_vat}/expense-claim/department-review" "${admin_accounting_token}" ""
expect_status "T1 V_VAT stage 1 department-review" 200 "${status}"
req POST "/vouchers/${v_vat}/expense-claim/accounting-review" "${admin_accounting2_token}" ""
expect_status "T1 V_VAT stage 2 accounting-review" 200 "${status}"
req POST "/vouchers/${v_vat}/apply-expense" "${admin_accounting3_token}" ""
expect_status "T1 required-but-unverified invoice blocks final payment approval (bypass attempt)" 409 "${status}"
[[ "$(jqf .error)" == "tax_verification_required" ]] && ok "T1 conflict reason is tax_verification_required" || bad "T1 unverified block reason: ${body}"

req POST "/vouchers/${v_vat}/tax-verification/reject" "${admin_accounting_token}" '{"reason":"Hoá đơn đã bị huỷ trên cổng thuế","providerUnavailable":false}'
expect_status "T1 ADMIN records an invalid/cancelled TCT result" 200 "${status}"
req POST "/vouchers/${v_vat}/apply-expense" "${admin_accounting3_token}" ""
expect_status "T1 invalid/cancelled invoice still blocks final payment approval" 409 "${status}"
req GET "/vouchers/${v_vat}" "${assigned_user_token}" ""
[[ "$(jqf .voucher.taxVerificationStatus)" == "invalid" ]] && ok "T1 UI can read the invalid status + reason" || bad "T1 status not exposed: ${body}"

req POST "/vouchers/${v_vat}/tax-verification/reject" "${admin_accounting_token}" '{"reason":"Cổng thuế không phản hồi (timeout)","providerUnavailable":true}'
expect_status "T1 ADMIN records a provider-unavailable result" 200 "${status}"
req GET "/vouchers/${v_vat}" "${assigned_user_token}" ""
[[ "$(jqf .voucher.taxVerificationStatus)" == "unavailable" ]] && ok "T1 unavailable is an explicit state, never fabricated as success" || bad "T1 unavailable status: ${body}"
req POST "/vouchers/${v_vat}/apply-expense" "${admin_accounting3_token}" ""
expect_status "T1 provider-unavailable also blocks — never a fake pass" 409 "${status}"

req POST "/vouchers/${v_vat}/tax-verification/confirm" "${assigned_user_token}" ""
expect_status "T1 real positive verification recorded" 200 "${status}"
req POST "/vouchers/${v_vat}/apply-expense" "${admin_accounting3_token}" ""
expect_status "T1 verified invoice may now continue — final approval succeeds" 200 "${status}"
[[ "$(jqf '.expense_claim_result.reviewStatus')" == "confirmed" ]] && ok "T1 V_VAT confirmed after verification gate cleared" || bad "T1 V_VAT final state: ${body}"
req GET "/vouchers/${v_vat}/expense-claim-export" "${admin_accounting_token}" ""
[[ "$(jqf .vatDeductibleAmount)" == "340000.00" || "$(jqf .vatDeductibleAmount)" == "340000" ]] && ok "export reflects the real vatDeductibleAmount (no longer the mis-derived residual)" || bad "export vatDeductibleAmount: ${body}"
[[ "$(jqf .citNonDeductibleAmount)" == "550000.00" || "$(jqf .citNonDeductibleAmount)" == "550000" ]] && ok "export exposes citNonDeductibleAmount distinctly from vatDeductibleAmount" || bad "export citNonDeductibleAmount: ${body}"

echo ""
echo "== §15 R14 — 3-stage sequence enforcement: cannot skip, cannot repeat actor, self-approval blocked per stage =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-STAGE-01"}'
v_stage="$(jqf .voucher.id)"
stage_payload=$(cat <<EOF
{"voucherType":"expense_claim","documentNumber":"EC-STAGE-01","extractedFields":{"employee_code":"NV-05","total_claim":100000,"advance_taken":0,"currency":"VND","items":[{"category":"meal","description":"An trua","amount":100000,"receipt_attached":true}]}}
EOF
)
req PATCH "/vouchers/${v_stage}/apply-extraction" "${assigned_user_token}" "${stage_payload}"
req POST "/vouchers/${v_stage}/expense-claim-result/propose" "${assigned_user_token}" ""
expect_status "propose V_STAGE" 200 "${status}"

req POST "/vouchers/${v_stage}/expense-claim/accounting-review" "${admin_accounting_token}" ""
expect_status "cannot skip straight to stage 2 (still 'proposed')" 409 "${status}"
req POST "/vouchers/${v_stage}/apply-expense" "${admin_accounting_token}" ""
expect_status "cannot skip straight to stage 3 (still 'proposed')" 409 "${status}"

req POST "/vouchers/${v_stage}/expense-claim/department-review" "${assigned_user_token}" ""
expect_status "creator cannot self-approve stage 1" 403 "${status}"

req POST "/vouchers/${v_stage}/expense-claim/department-review" "${admin_accounting_token}" ""
expect_status "stage 1 (department-review) succeeds" 200 "${status}"

req POST "/vouchers/${v_stage}/apply-expense" "${admin_accounting2_token}" ""
expect_status "cannot skip straight to stage 3 from 'dept_reviewed'" 409 "${status}"
req POST "/vouchers/${v_stage}/expense-claim/department-review" "${admin_accounting2_token}" ""
expect_status "cannot repeat stage 1 once already dept_reviewed" 409 "${status}"

req POST "/vouchers/${v_stage}/expense-claim/accounting-review" "${assigned_user_token}" ""
expect_status "creator cannot self-approve stage 2 either" 403 "${status}"
req POST "/vouchers/${v_stage}/expense-claim/accounting-review" "${admin_accounting_token}" ""
expect_status "same actor as stage 1 cannot also perform stage 2 (separation of duties)" 409 "${status}"

req POST "/vouchers/${v_stage}/expense-claim/accounting-review" "${admin_accounting2_token}" ""
expect_status "stage 2 (accounting-review) succeeds with a different actor" 200 "${status}"

req POST "/vouchers/${v_stage}/apply-expense" "${admin_accounting_token}" ""
expect_status "stage-1 actor cannot also give final payment approval" 409 "${status}"
req POST "/vouchers/${v_stage}/apply-expense" "${admin_accounting2_token}" ""
expect_status "stage-2 actor cannot also give final payment approval" 409 "${status}"
req POST "/vouchers/${v_stage}/apply-expense" "${admin_accounting3_token}" ""
expect_status "stage 3 (payment approval) succeeds with a third distinct actor — claim now confirmed" 200 "${status}"
[[ "$(jqf '.expense_claim_result.reviewStatus')" == "confirmed" ]] && ok "V_STAGE fully confirmed through the enforced 3-stage sequence" || bad "V_STAGE final state: ${body}"

echo ""
echo "== §16 J1 — deterministic expense-settlement journal routing (configured category -> account) =="
psqlc "INSERT INTO department_expense_policies(tenant_id,department_id,category,limit_amount,limit_unit,allow_exception,requires_receipt,debit_account) VALUES ('${tenant_a}','${accounting_dept}','consulting_j1',100000000,'per_claim',false,false,'641')" >/dev/null
emp_j1="$(psqlc "INSERT INTO sim_employees(tenant_id,employee_code,full_name,department,monthly_expense_budget,outstanding_advance) VALUES ('${tenant_a}','NV-J1','Vo Van J1','Van phong',0,5000000) RETURNING id")"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-J1-01"}'
v_j1="$(jqf .voucher.id)"
v_j1_payload=$(cat <<EOF
{"voucherType":"expense_claim","documentNumber":"EC-J1-01","extractedFields":{"employee_code":"NV-J1","total_claim":7230000,"advance_taken":5000000,"currency":"VND","items":[{"category":"consulting_j1","description":"Tu van du an","amount":7230000,"receipt_attached":true}]}}
EOF
)
req PATCH "/vouchers/${v_j1}/apply-extraction" "${assigned_user_token}" "${v_j1_payload}"
req POST "/vouchers/${v_j1}/expense-claim-result/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "APPROVED" ]] && ok "J1 V_J1 -> APPROVED" || bad "J1 V_J1 outcome: ${body}"
req GET "/vouchers/${v_j1}" "${assigned_user_token}" ""
[[ "$(jqf '.journalSuggestions | map(select(.creditAccount=="141")) | map(.amount) | add')" == "5000000" ]] && ok "J1 settlement credits 141 (advance offset) = 5,000,000" || bad "J1 141 total: ${body}"
[[ "$(jqf '.journalSuggestions | map(select(.creditAccount=="334")) | map(.amount) | add')" == "2230000" ]] && ok "J1 settlement credits 334 (residual payable) = 2,230,000" || bad "J1 334 total: ${body}"
[[ "$(jqf '.journalSuggestions | map(select(.debitAccount=="641")) | map(.amount) | add')" == "7230000" ]] && ok "J1 settlement debits the configured 641 account for the full 7,230,000" || bad "J1 641 total: ${body}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-J1-02"}'
v_j1_unconf="$(jqf .voucher.id)"
v_j1_unconf_payload=$(cat <<EOF
{"voucherType":"expense_claim","documentNumber":"EC-J1-02","extractedFields":{"employee_code":"NV-05","total_claim":500000,"advance_taken":0,"currency":"VND","items":[{"category":"unconfigured_j1_category","description":"Chi phi khac","amount":500000,"receipt_attached":true}]}}
EOF
)
req PATCH "/vouchers/${v_j1_unconf}/apply-extraction" "${assigned_user_token}" "${v_j1_unconf_payload}"
req POST "/vouchers/${v_j1_unconf}/expense-claim-result/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "APPROVED" ]] && ok "J1 V_J1_UNCONF -> APPROVED (no policy row at all for this category)" || bad "J1 V_J1_UNCONF outcome: ${body}"
req GET "/vouchers/${v_j1_unconf}" "${assigned_user_token}" ""
[[ "$(jqf '.journalSuggestions | length')" == "0" ]] && ok "J1 unconfigured category -> no journal entry proposed (no invented GL account)" || bad "J1 unconfigured entry count: ${body}"

echo ""
echo "== §18 P1x — rank-based (cấp bậc) expense policy: position-specific limit beats the department default =="
psqlc "INSERT INTO department_expense_policies(tenant_id,department_id,category,limit_amount,limit_unit,allow_exception,requires_receipt,position) VALUES ('${tenant_a}','${accounting_dept}','hotel_rank','1500000','per_item',false,true,'Chuyen vien')" >/dev/null
psqlc "INSERT INTO department_expense_policies(tenant_id,department_id,category,limit_amount,limit_unit,allow_exception,requires_receipt,position) VALUES ('${tenant_a}','${accounting_dept}','hotel_rank','2500000','per_item',false,true,'Truong phong')" >/dev/null
emp_junior="$(psqlc "INSERT INTO sim_employees(tenant_id,employee_code,full_name,department,position,monthly_expense_budget,outstanding_advance) VALUES ('${tenant_a}','NV-JUNIOR','Junior NV','Van phong','Chuyen vien',0,0) RETURNING id")"
emp_senior="$(psqlc "INSERT INTO sim_employees(tenant_id,employee_code,full_name,department,position,monthly_expense_budget,outstanding_advance) VALUES ('${tenant_a}','NV-SENIOR','Senior NV','Van phong','Truong phong',0,0) RETURNING id")"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-RANK-JUNIOR"}'
v_rank_junior="$(jqf .voucher.id)"
req PATCH "/vouchers/${v_rank_junior}/apply-extraction" "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-RANK-JUNIOR","extractedFields":{"employee_code":"NV-JUNIOR","total_claim":2000000,"advance_taken":0,"currency":"VND","items":[{"category":"hotel_rank","description":"KS cong tac","amount":2000000,"receipt_attached":true}]}}'
req POST "/vouchers/${v_rank_junior}/expense-claim-result/propose" "${assigned_user_token}" ""
[[ "$(jqf .approvedAmount)" == "1500000.00" || "$(jqf .approvedAmount)" == "1500000" ]] && ok "P1x Chuyên viên capped at their own rank's limit (1,500,000), not the generic one" || bad "P1x junior approvedAmount: ${body}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-RANK-SENIOR"}'
v_rank_senior="$(jqf .voucher.id)"
req PATCH "/vouchers/${v_rank_senior}/apply-extraction" "${assigned_user_token}" '{"voucherType":"expense_claim","documentNumber":"EC-RANK-SENIOR","extractedFields":{"employee_code":"NV-SENIOR","total_claim":2000000,"advance_taken":0,"currency":"VND","items":[{"category":"hotel_rank","description":"KS cong tac","amount":2000000,"receipt_attached":true}]}}'
req POST "/vouchers/${v_rank_senior}/expense-claim-result/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "APPROVED" ]] && ok "P1x Trưởng phòng's higher rank-specific limit (2,500,000) covers the full 2,000,000 claim, uncapped" || bad "P1x senior outcome: ${body}"

echo ""
echo "================================================================"
echo "EXPENSE PAYMENT (LUỒNG 03) E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
