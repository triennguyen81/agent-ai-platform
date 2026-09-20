#!/usr/bin/env bash
# Luồng 02 — Phiếu thu công nợ (Receipt/AR matching) real HTTP E2E.
#
# Boots an isolated, empty Postgres + freshly built Gateway image (own
# network/volume, never touches the shared dev stack) and verifies:
#   - Deterministic outcome classification (POST /vouchers/{id}/receipt-match
#     /propose) against real sim_customers/sim_ar_ledger/contract_* data for
#     8 of the 9 canonical outcomes: FULL_MATCH, PARTIAL_PAYMENT, OVERPAYMENT,
#     ADVANCE_PAYMENT, CUSTOMER_NO_OPEN_RECEIVABLE, UNKNOWN_PAYER,
#     REVERSED_TRANSACTION, FOREIGN_CURRENCY. (OVERDUE_UNPAID is a declared
#     but not-yet-classified outcome — see docs/RECEIPT_AR_FLOW_TODO.md Bugs.)
#   - Idempotent retry: re-proposing a still-'proposed' row upserts in place
#     (same id), never duplicates.
#   - ACL: creator-or-admin may propose; ADMIN-only may reject; Department-
#     scoped read; cross-Department/cross-Tenant denied.
#   - Reject → re-propose recovers (regression test for the upsert guard that
#     used to throw once a row was 'rejected' instead of upserting).
#   - ADMIN confirm via apply-receipt: settles sim_ar_ledger/current_ar (pre-
#     existing) AND now also the linked contract_payment_schedules row
#     (FULL_MATCH/OVERPAYMENT -> 'paid', PARTIAL_PAYMENT -> 'partial').
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-rcp-e2e-net-${run_id}"
pg="aiq-rcp-e2e-postgres-${run_id}"
gw="aiq-rcp-e2e-gateway-${run_id}"
vol="aiq-rcp-e2e-pgdata-${run_id}"
db="aiq_rcp_e2e"
db_password="rcp_e2e_password"
image="aiq-agentic-gateway:contracttest"
base="http://localhost:18089"
gw_port=18089

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
    local args=(-sS -o /tmp/rcp_e2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/rcp_e2e_body.$$)"
    rm -f /tmp/rcp_e2e_body.$$
}

jqf() { echo "${body}" | jq -r "$1"; }

psqlc() { docker exec "${pg}" psql -v ON_ERROR_STOP=1 -At -U postgres -d "${db}" -c "$1" | { grep -v -E '^(INSERT|UPDATE|DELETE) [0-9]+( [0-9]+)?$' || true; }; }

echo "== Receipt/AR E2E: booting isolated stack =="
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
    --env Jwt__Issuer=aiq.rcp.test --env Jwt__Audience=aiq.rcp.client \
    --env Jwt__SigningKey=rcp-e2e-test-signing-key-needs-32-bytes-long \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://minio.invalid:9000 --env Object__AccessKey=rcpe2e --env Object__SecretKey=rcpe2epassword \
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

req POST /auth/register "" '{"email":"root@rcptest.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"

docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@rcptest.local' -f /tmp/first-super-admin.sql >/dev/null

req POST /auth/login "" '{"email":"root@rcptest.local","password":"RootPass123!"}'
super_token="$(jqf .token)"

echo "== Setup: Tenant A (+ Sales dept), Tenant B, Business Agent granting voucher.processing + customer.management =="
req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-rcptest","name":"Tenant A","ownerEmail":"owner-a@rcptest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
expect_status "onboard Tenant A" 201 "${status}"
tenant_a="$(jqf .tenant.id)"

req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-b-rcptest","name":"Tenant B","ownerEmail":"owner-b@rcptest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner B"}'
expect_status "onboard Tenant B" 201 "${status}"
tenant_b="$(jqf .tenant.id)"

req GET /system/capabilities "${super_token}" ""
voucher_cap="$(echo "${body}" | jq -r '.items[] | select(.code=="voucher.processing") | .id')"
customer_cap="$(echo "${body}" | jq -r '.items[] | select(.code=="customer.management") | .id')"
[[ -n "${voucher_cap}" && "${voucher_cap}" != "null" ]] && ok "voucher.processing exists in catalog" || bad "voucher.processing missing"
[[ -n "${customer_cap}" && "${customer_cap}" != "null" ]] && ok "customer.management exists in catalog" || bad "customer.management missing"

req POST /system/agents "${super_token}" '{"slug":"receipt-agent-e2e","name":"Receipt Agent","actionTier":"suggest"}'
expect_status "create Receipt Agent" 201 "${status}"
agent_id="$(jqf .id)"
req GET "/system/agents/${agent_id}/versions" "${super_token}" ""
draft_version_id="$(jqf '.items[0].id')"
req PATCH "/system/agents/${agent_id}/versions/${draft_version_id}" "${super_token}" "{\"capabilityIds\":[\"${voucher_cap}\",\"${customer_cap}\"]}"
expect_status "assign capabilities to the draft" 200 "${status}"
req POST "/system/agents/${agent_id}/versions/${draft_version_id}/publish" "${super_token}" ""
expect_status "publish version" 200 "${status}"
req PUT "/system/agents/${agent_id}/tenants/${tenant_a}" "${super_token}" ""
expect_status "assign Receipt Agent to Tenant A" 204 "${status}"

docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status)
  SELECT id,'sales-rcptest','Sales','active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
accounting_dept="$(psqlc "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
sales_dept="$(psqlc "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-rcptest'")"

req POST /auth/login "" '{"email":"owner-a@rcptest.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner assigns Receipt Agent to Accounting dept" 204 "${status}"
req PUT "/tenant/departments/${sales_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner assigns Receipt Agent to Sales dept" 204 "${status}"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@rcptest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@rcptest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "owner creates Admin Sales" 201 "${status}"
req POST /auth/login "" '{"email":"admin-accounting@rcptest.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
admin_accounting_id="$(psqlc "SELECT id FROM users WHERE email='admin-accounting@rcptest.local'")"
req POST /auth/login "" '{"email":"admin-sales@rcptest.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"

req PUT "/department/capabilities/${voucher_cap}/binding" "${admin_accounting_token}" "{\"agentId\":\"${agent_id}\"}"
expect_status "Admin Accounting binds voucher.processing -> Receipt Agent" 204 "${status}"
req PUT "/department/capabilities/${customer_cap}/binding" "${admin_accounting_token}" "{\"agentId\":\"${agent_id}\"}"
expect_status "Admin Accounting binds customer.management -> Receipt Agent" 204 "${status}"

req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user@rcptest.local","password":"UserPass123!","displayName":"Assigned User"}'
expect_status "Admin Accounting creates Assigned User" 201 "${status}"
assigned_user_id="$(psqlc "SELECT id FROM users WHERE email='assigned-user@rcptest.local'")"
req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user2@rcptest.local","password":"UserPass123!","displayName":"Assigned User 2"}'
expect_status "Admin Accounting creates Assigned User 2" 201 "${status}"
req POST /tenant/users "${admin_accounting_token}" '{"email":"unassigned-user@rcptest.local","password":"UserPass123!","displayName":"Unassigned User"}'
expect_status "Admin Accounting creates Unassigned User" 201 "${status}"

req PUT "/tenant/agents/${agent_id}/users/${assigned_user_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Receipt Agent to Assigned User" 204 "${status}"
assigned_user2_id="$(psqlc "SELECT id FROM users WHERE email='assigned-user2@rcptest.local'")"
req PUT "/tenant/agents/${agent_id}/users/${assigned_user2_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Receipt Agent to Assigned User 2" 204 "${status}"

req POST /auth/login "" '{"email":"assigned-user@rcptest.local","password":"UserPass123!"}'
assigned_user_token="$(jqf .token)"
req POST /auth/login "" '{"email":"assigned-user2@rcptest.local","password":"UserPass123!"}'
assigned_user2_token="$(jqf .token)"
req POST /auth/login "" '{"email":"unassigned-user@rcptest.local","password":"UserPass123!"}'
unassigned_user_token="$(jqf .token)"

echo ""
echo "== §1 Seed AR/contract fixtures: Customer C1 (open invoice + linked contract installment), C2 (no invoice, linked contract), C3 (no invoice, no contract), C4 (own invoice + linked contract, for the partial-confirm test) =="
req POST /customers "${assigned_user_token}" '{"customerCode":"CUS-C1","name":"Cong ty C1","mst":"0100000001","customerType":"company"}'
expect_status "create Customer C1" 201 "${status}"
c1_id="$(jqf .customer.id)"
req POST /customers "${assigned_user_token}" '{"customerCode":"CUS-C2","name":"Cong ty C2","mst":"0100000002","customerType":"company"}'
expect_status "create Customer C2" 201 "${status}"
c2_id="$(jqf .customer.id)"
req POST /customers "${assigned_user_token}" '{"customerCode":"CUS-C3","name":"Cong ty C3","mst":"0100000003","customerType":"company"}'
expect_status "create Customer C3" 201 "${status}"
req POST /customers "${assigned_user_token}" '{"customerCode":"CUS-C4","name":"Cong ty C4","mst":"0100000004","customerType":"company"}'
expect_status "create Customer C4" 201 "${status}"
c4_id="$(jqf .customer.id)"

psqlc "INSERT INTO sim_ar_ledger(tenant_id,customer_id,document_date,document_number,document_type,debit,credit,due_date) VALUES ('${tenant_a}','${c1_id}',CURRENT_DATE,'INV-C1-001','invoice',2000000,0,CURRENT_DATE+15)" >/dev/null
psqlc "INSERT INTO sim_ar_ledger(tenant_id,customer_id,document_date,document_number,document_type,debit,credit,due_date) VALUES ('${tenant_a}','${c4_id}',CURRENT_DATE,'INV-C4-001','invoice',1500000,0,CURRENT_DATE+15)" >/dev/null

contract_c1="$(psqlc "INSERT INTO contracts(user_id,tenant_id,department_id,title) VALUES ('${assigned_user_id}','${tenant_a}','${accounting_dept}','HD C1') RETURNING id")"
psqlc "INSERT INTO contract_parties(contract_id,tenant_id,department_id,party_role,name,tax_code) VALUES ('${contract_c1}','${tenant_a}','${accounting_dept}','buyer','Cong ty C1','0100000001')" >/dev/null
installment_c1="$(psqlc "INSERT INTO contract_payment_schedules(contract_id,tenant_id,department_id,ordinal,description,percentage,subtotal_amount,vat_amount,total_amount,due_date,status) VALUES ('${contract_c1}','${tenant_a}','${accounting_dept}',1,'Dot 1',100,2000000,0,2000000,CURRENT_DATE+30,'pending') RETURNING id")"
milestone_c1_m2="$(psqlc "INSERT INTO contract_milestones(contract_id,tenant_id,department_id,ordinal,name,trigger_event,duration_months,status) VALUES ('${contract_c1}','${tenant_a}','${accounting_dept}',2,'Nghiem thu giai doan 1','Sau khi thanh toan dot 1',2,'pending') RETURNING id")"

contract_c2="$(psqlc "INSERT INTO contracts(user_id,tenant_id,department_id,title) VALUES ('${assigned_user_id}','${tenant_a}','${accounting_dept}','HD C2') RETURNING id")"
psqlc "INSERT INTO contract_parties(contract_id,tenant_id,department_id,party_role,name,tax_code) VALUES ('${contract_c2}','${tenant_a}','${accounting_dept}','buyer','Cong ty C2','0100000002')" >/dev/null
psqlc "INSERT INTO contract_payment_schedules(contract_id,tenant_id,department_id,ordinal,description,percentage,subtotal_amount,vat_amount,total_amount,due_date,status) VALUES ('${contract_c2}','${tenant_a}','${accounting_dept}',1,'Tam ung Dot 1',50,1000000,0,1000000,CURRENT_DATE+30,'pending')" >/dev/null

contract_c4="$(psqlc "INSERT INTO contracts(user_id,tenant_id,department_id,title) VALUES ('${assigned_user_id}','${tenant_a}','${accounting_dept}','HD C4') RETURNING id")"
psqlc "INSERT INTO contract_parties(contract_id,tenant_id,department_id,party_role,name,tax_code) VALUES ('${contract_c4}','${tenant_a}','${accounting_dept}','buyer','Cong ty C4','0100000004')" >/dev/null
installment_c4="$(psqlc "INSERT INTO contract_payment_schedules(contract_id,tenant_id,department_id,ordinal,description,percentage,subtotal_amount,vat_amount,total_amount,due_date,status) VALUES ('${contract_c4}','${tenant_a}','${accounting_dept}',1,'Dot 1',100,1500000,0,1500000,CURRENT_DATE+30,'pending') RETURNING id")"

ok "fixtures seeded (2 open invoices, 3 contract installments)"

echo ""
echo "== §2 Outcome classification — propose against real AR/contract data =="

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-FULL","buyerMst":"0100000001","totalAmount":2000000,"currency":"VND"}'
expect_status "create V_FULL" 201 "${status}"
v_full="$(jqf .voucher.id)"
req POST "/vouchers/${v_full}/receipt-match/propose" "${assigned_user_token}" ""
expect_status "propose V_FULL" 200 "${status}"
[[ "$(jqf .outcome)" == "FULL_MATCH" ]] && ok "V_FULL -> FULL_MATCH" || bad "V_FULL outcome: ${body}"
[[ "$(jqf .matchedAmount)" == "2000000.00" || "$(jqf .matchedAmount)" == "2000000" ]] && ok "V_FULL matchedAmount=2000000" || bad "V_FULL matchedAmount: ${body}"
[[ "$(jqf .unallocatedAmount)" == "0.00" || "$(jqf .unallocatedAmount)" == "0" ]] && ok "V_FULL unallocatedAmount=0" || bad "V_FULL unallocatedAmount: ${body}"
[[ "$(jqf .installmentId)" == "${installment_c1}" ]] && ok "V_FULL linked to C1's contract installment" || bad "V_FULL installmentId: ${body}"
[[ "$(jqf .customerMatchConfidence)" == "1.000" || "$(jqf .customerMatchConfidence)" == "1" ]] && ok "V_FULL customer match confidence 1.0 (exact MST)" || bad "confidence: ${body}"
v_full_match_id="$(jqf .id)"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-PARTIAL","buyerMst":"0100000001","totalAmount":800000,"currency":"VND"}'
v_partial="$(jqf .voucher.id)"
req POST "/vouchers/${v_partial}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "PARTIAL_PAYMENT" ]] && ok "V_PARTIAL -> PARTIAL_PAYMENT" || bad "V_PARTIAL outcome: ${body}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-OVER","buyerMst":"0100000001","totalAmount":2500000,"currency":"VND"}'
v_over="$(jqf .voucher.id)"
req POST "/vouchers/${v_over}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "OVERPAYMENT" ]] && ok "V_OVER -> OVERPAYMENT" || bad "V_OVER outcome: ${body}"
[[ "$(jqf .unallocatedAmount)" == "500000.00" || "$(jqf .unallocatedAmount)" == "500000" ]] && ok "V_OVER unallocatedAmount=500000" || bad "V_OVER unallocatedAmount: ${body}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-ADVANCE","buyerMst":"0100000002","totalAmount":1000000,"currency":"VND"}'
v_advance="$(jqf .voucher.id)"
req POST "/vouchers/${v_advance}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "ADVANCE_PAYMENT" ]] && ok "V_ADVANCE -> ADVANCE_PAYMENT (no open invoice, contract linked)" || bad "V_ADVANCE outcome: ${body}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-NORECV","buyerMst":"0100000003","totalAmount":500000,"currency":"VND"}'
v_norecv="$(jqf .voucher.id)"
req POST "/vouchers/${v_norecv}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "CUSTOMER_NO_OPEN_RECEIVABLE" ]] && ok "V_NORECV -> CUSTOMER_NO_OPEN_RECEIVABLE" || bad "V_NORECV outcome: ${body}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-UNKNOWN","buyerMst":"0199999999","totalAmount":300000,"currency":"VND"}'
v_unknown="$(jqf .voucher.id)"
req POST "/vouchers/${v_unknown}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "UNKNOWN_PAYER" ]] && ok "V_UNKNOWN -> UNKNOWN_PAYER" || bad "V_UNKNOWN outcome: ${body}"
n="$(psqlc "SELECT COUNT(*) FROM notifications WHERE ref_id='${v_unknown}' AND kind='receipt_unknown_payer' AND user_id='${admin_accounting_id}'")"
[[ "${n}" == "1" ]] && ok "R18: UNKNOWN_PAYER notifies Admin Accounting exactly once" || bad "R18 UNKNOWN_PAYER notification count: ${n}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-REVERSED","buyerMst":"0100000001","totalAmount":-100000,"currency":"VND"}'
v_reversed="$(jqf .voucher.id)"
req POST "/vouchers/${v_reversed}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "REVERSED_TRANSACTION" ]] && ok "V_REVERSED -> REVERSED_TRANSACTION" || bad "V_REVERSED outcome: ${body}"
n="$(psqlc "SELECT COUNT(*) FROM notifications WHERE ref_id='${v_reversed}' AND kind='receipt_reversed_transaction' AND user_id='${admin_accounting_id}'")"
[[ "${n}" == "1" ]] && ok "R18: REVERSED_TRANSACTION notifies Admin Accounting exactly once" || bad "R18 REVERSED_TRANSACTION notification count: ${n}"

req POST "/vouchers/${v_unknown}/receipt-match/propose" "${assigned_user_token}" ""
n="$(psqlc "SELECT COUNT(*) FROM notifications WHERE ref_id='${v_unknown}' AND kind='receipt_unknown_payer'")"
[[ "${n}" == "1" ]] && ok "R18: re-propose does not duplicate the notification (dedup key holds)" || bad "R18 dedup re-propose count: ${n}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-FX","buyerMst":"0100000001","totalAmount":500,"currency":"USD"}'
v_fx="$(jqf .voucher.id)"
req POST "/vouchers/${v_fx}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "FOREIGN_CURRENCY" ]] && ok "V_FX -> FOREIGN_CURRENCY" || bad "V_FX outcome: ${body}"

echo ""
echo "== §3 Idempotent retry — re-proposing a still-'proposed' row upserts in place =="
req POST "/vouchers/${v_full}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .id)" == "${v_full_match_id}" ]] && ok "re-propose keeps the same match row id (upsert, not duplicate)" || bad "re-propose id changed: ${body}"
[[ "$(jqf .outcome)" == "FULL_MATCH" ]] && ok "re-propose recomputes the same outcome" || bad "re-propose outcome: ${body}"

echo ""
echo "== §4 ACL: creator-or-admin propose, ADMIN-only reject, Department-scoped read, cross-Tenant denied =="
req POST "/vouchers/${v_full}/receipt-match/propose" "${assigned_user2_token}" ""
expect_status "Assigned User 2 (not creator, not admin) cannot propose on V_FULL" 403 "${status}"
req GET "/vouchers/${v_full}/receipt-match" "${unassigned_user_token}" ""
expect_status "Unassigned User (no voucher.processing) cannot read receipt-match" 403 "${status}"
req POST "/vouchers/${v_unknown}/reject-receipt-match" "${assigned_user_token}" ""
expect_status "Assigned User (creator, non-admin) cannot reject (ADMIN-only)" 403 "${status}"
req GET "/vouchers/${v_full}/receipt-match" "${admin_sales_token}" ""
expect_status "Admin Sales (different Department) cannot read Accounting's receipt-match" 404 "${status}"

req POST /auth/login "" '{"email":"owner-b@rcptest.local","password":"OwnerPass123!"}'
owner_b_token="$(jqf .token)"
tenant_b_default_dept="$(psqlc "SELECT id FROM departments WHERE tenant_id='${tenant_b}' AND is_default")"
req POST /tenant/admins "${owner_b_token}" "{\"email\":\"admin-b@rcptest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin B\",\"departmentId\":\"${tenant_b_default_dept}\"}"
expect_status "owner B creates Admin B (own tenant, own department)" 201 "${status}"
req POST /auth/login "" '{"email":"admin-b@rcptest.local","password":"AdminPass123!"}'
admin_b_token="$(jqf .token)"
req GET "/vouchers/${v_full}/receipt-match" "${admin_b_token}" ""
expect_status "Admin B (Tenant B) cannot read Tenant A's receipt-match (cross-tenant denied)" 404 "${status}"

echo ""
echo "== §5 ADMIN reject, then re-propose recovers (regression: upsert guard must not throw once rejected) =="
req POST "/vouchers/${v_unknown}/reject-receipt-match" "${admin_accounting_token}" ""
expect_status "ADMIN rejects V_UNKNOWN's proposed match" 200 "${status}"
[[ "$(jqf .reviewStatus)" == "rejected" ]] && ok "V_UNKNOWN review_status=rejected" || bad "reject result: ${body}"
req GET "/vouchers/${v_unknown}/receipt-match" "${admin_accounting_token}" ""
[[ "$(jqf .reviewStatus)" == "rejected" ]] && ok "GET confirms rejected status persisted" || bad "GET after reject: ${body}"
req POST "/vouchers/${v_unknown}/reject-receipt-match" "${admin_accounting_token}" ""
expect_status "re-reject on an already-rejected row is a no-op conflict, not a crash" 409 "${status}"
req POST "/vouchers/${v_unknown}/receipt-match/propose" "${assigned_user_token}" ""
expect_status "re-propose after reject succeeds (was: 500 before the upsert-guard fix)" 200 "${status}"
[[ "$(jqf .reviewStatus)" == "proposed" ]] && ok "re-propose resets review_status back to 'proposed'" || bad "re-propose review_status: ${body}"
[[ "$(jqf .outcome)" == "UNKNOWN_PAYER" ]] && ok "re-propose recomputes the outcome" || bad "re-propose outcome: ${body}"

echo ""
echo "== §6 ADMIN confirm via apply-receipt — settles sim_ar_ledger AND the linked contract installment =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-APPLY-FULL","buyerMst":"0100000001","totalAmount":2000000,"currency":"VND"}'
expect_status "create V_APPLY_FULL" 201 "${status}"
v_apply_full="$(jqf .voucher.id)"
apply_full_payload=$(cat <<EOF
{"voucherType":"receipt","documentNumber":"PT-APPLY-FULL","buyerMst":"0100000001","totalAmount":2000000,"currency":"VND","extractedFields":{"ar_context":{"customer":{"Id":"${c1_id}"}},"receipt_matching":{"receipt_amount":2000000,"applications":[{"document_number":"INV-C1-001","applied":2000000}]}}}
EOF
)
req PATCH "/vouchers/${v_apply_full}/apply-extraction" "${assigned_user_token}" "${apply_full_payload}"
expect_status "Assigned User stages extracted_fields for V_APPLY_FULL" 200 "${status}"

req POST "/vouchers/${v_apply_full}/apply-receipt" "${admin_accounting_token}" ""
expect_status "ADMIN applies V_APPLY_FULL" 200 "${status}"
[[ "$(jqf .status)" == "applied" ]] && ok "apply-receipt posts the ledger rows" || bad "apply result: ${body}"
[[ "$(jqf .receipt_match.reviewStatus)" == "confirmed" ]] && ok "receipt_match.reviewStatus=confirmed after apply" || bad "receipt_match: ${body}"
[[ "$(jqf .receipt_match.outcome)" == "FULL_MATCH" ]] && ok "receipt_match.outcome=FULL_MATCH after apply" || bad "receipt_match outcome: ${body}"
installment_c1_status="$(psqlc "SELECT status FROM contract_payment_schedules WHERE id='${installment_c1}'")"
[[ "${installment_c1_status}" == "paid" ]] && ok "FULL_MATCH confirm settles the linked installment to 'paid'" || bad "installment_c1 status: ${installment_c1_status}"

echo "== C1/C2 — contract progress % recomputed + next milestone activated =="
contract_c1_progress="$(psqlc "SELECT progress_percent FROM contracts WHERE id='${contract_c1}'")"
[[ "${contract_c1_progress}" == "100.00" ]] && ok "C1 progress_percent recomputed to 100 (đợt 1 = 100% of schedule, now paid)" || bad "contract_c1 progress_percent: ${contract_c1_progress}"
milestone_c1_m2_status="$(psqlc "SELECT status FROM contract_milestones WHERE id='${milestone_c1_m2}'")"
[[ "${milestone_c1_m2_status}" == "active" ]] && ok "C2 next milestone (ordinal 2, trigger-based) activated by đợt 1 settling" || bad "milestone_c1_m2 status: ${milestone_c1_m2_status}"
milestone_c1_m2_expected="$(psqlc "SELECT expected_date FROM contract_milestones WHERE id='${milestone_c1_m2}'")"
[[ -n "${milestone_c1_m2_expected}" ]] && ok "C2 activated milestone got a computed expected_date (duration_months applied)" || bad "milestone_c1_m2 expected_date: ${milestone_c1_m2_expected}"

req POST "/vouchers/${v_apply_full}/apply-receipt" "${admin_accounting_token}" ""
expect_status "re-apply on an already-verified voucher is idempotent (200, not re-posted)" 200 "${status}"
[[ "$(jqf .status)" == "already_applied" ]] && ok "re-apply short-circuits as already_applied" || bad "re-apply result: ${body}"
installment_c1_status_after_retry="$(psqlc "SELECT status FROM contract_payment_schedules WHERE id='${installment_c1}'")"
[[ "${installment_c1_status_after_retry}" == "paid" ]] && ok "retry does not disturb the settled installment status" || bad "installment_c1 status after retry: ${installment_c1_status_after_retry}"
contract_c1_progress_after_retry="$(psqlc "SELECT progress_percent FROM contracts WHERE id='${contract_c1}'")"
[[ "${contract_c1_progress_after_retry}" == "100.00" ]] && ok "C1/C2 retry is idempotent — progress_percent unchanged, not double-counted" || bad "contract_c1 progress_percent after retry: ${contract_c1_progress_after_retry}"

echo ""
echo "== §7 ADMIN confirm PARTIAL_PAYMENT — installment settles to 'partial', not 'paid' =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-APPLY-PARTIAL","buyerMst":"0100000004","totalAmount":1000000,"currency":"VND"}'
expect_status "create V_APPLY_PARTIAL" 201 "${status}"
v_apply_partial="$(jqf .voucher.id)"
apply_partial_payload=$(cat <<EOF
{"voucherType":"receipt","documentNumber":"PT-APPLY-PARTIAL","buyerMst":"0100000004","totalAmount":1000000,"currency":"VND","extractedFields":{"ar_context":{"customer":{"Id":"${c4_id}"}},"receipt_matching":{"receipt_amount":1000000,"applications":[{"document_number":"INV-C4-001","applied":1000000}]}}}
EOF
)
req PATCH "/vouchers/${v_apply_partial}/apply-extraction" "${assigned_user_token}" "${apply_partial_payload}"
expect_status "Assigned User stages extracted_fields for V_APPLY_PARTIAL" 200 "${status}"

req POST "/vouchers/${v_apply_partial}/apply-receipt" "${admin_accounting_token}" ""
expect_status "ADMIN applies V_APPLY_PARTIAL" 200 "${status}"
[[ "$(jqf .receipt_match.outcome)" == "PARTIAL_PAYMENT" ]] && ok "receipt_match.outcome=PARTIAL_PAYMENT after apply" || bad "receipt_match outcome: ${body}"
installment_c4_status="$(psqlc "SELECT status FROM contract_payment_schedules WHERE id='${installment_c4}'")"
[[ "${installment_c4_status}" == "partial" ]] && ok "PARTIAL_PAYMENT confirm settles the linked installment to 'partial'" || bad "installment_c4 status: ${installment_c4_status}"

echo ""
echo "== §8 R16 — separation of duties: ADMIN cannot confirm/reject a receipt they themselves created =="
req POST /vouchers "${admin_accounting_token}" '{"voucherType":"receipt","documentNumber":"PT-SELF-01","buyerMst":"0100000001","totalAmount":100000,"currency":"VND"}'
expect_status "ADMIN creates their own receipt voucher" 201 "${status}"
v_self="$(jqf .voucher.id)"
self_payload=$(cat <<EOF
{"voucherType":"receipt","documentNumber":"PT-SELF-01","buyerMst":"0100000001","totalAmount":100000,"currency":"VND","extractedFields":{"ar_context":{"customer":{"Id":"${c1_id}"}},"receipt_matching":{"receipt_amount":100000,"applications":[{"document_number":"INV-C1-001","applied":100000}]}}}
EOF
)
req PATCH "/vouchers/${v_self}/apply-extraction" "${admin_accounting_token}" "${self_payload}"
expect_status "ADMIN stages extracted_fields for their own voucher" 200 "${status}"
req POST "/vouchers/${v_self}/apply-receipt" "${admin_accounting_token}" ""
expect_status "ADMIN cannot apply-receipt on their own voucher (self-approval blocked)" 403 "${status}"
req POST "/vouchers/${v_self}/receipt-match/propose" "${admin_accounting_token}" ""
req POST "/vouchers/${v_self}/reject-receipt-match" "${admin_accounting_token}" ""
expect_status "ADMIN cannot reject their own voucher's match either" 403 "${status}"
req POST "/vouchers/${v_self}/apply-receipt" "${admin_sales_token}" ""
expect_status "A DIFFERENT admin (Sales dept — different Department, expected 404 not 200) still cannot apply" 404 "${status}"
req GET "/vouchers/${v_self}/receipt-match" "${admin_accounting_token}" ""
match_status_after="$(jqf .reviewStatus)"
[[ "${match_status_after}" == "proposed" ]] && ok "voucher remains unsettled after both self-approval attempts were blocked" || bad "unexpected reviewStatus: ${match_status_after}"

echo ""
echo "== §9 R05 — tax-verification/confirm persists a real (never fabricated) result =="
req GET "/vouchers/${v_full}" "${assigned_user_token}" ""
[[ "$(jqf .voucher.tctVerified)" == "false" ]] && ok "V_FULL starts tct_verified=false (never defaults to true)" || bad "unexpected initial tctVerified: ${body}"
req POST "/vouchers/${v_full}/tax-verification/confirm" "${unassigned_user_token}" ""
expect_status "Unassigned User (no capability) cannot confirm tax verification" 403 "${status}"
req POST "/vouchers/${v_full}/tax-verification/confirm" "${assigned_user_token}" ""
expect_status "Assigned User (Department member) confirms tax verification" 200 "${status}"
req GET "/vouchers/${v_full}" "${assigned_user_token}" ""
[[ "$(jqf .voucher.tctVerified)" == "true" ]] && ok "V_FULL now tct_verified=true, persisted" || bad "tctVerified not persisted: ${body}"

echo ""
echo "== §10 R06 — explicit Contract/Invoice link (persistent, not just the tax-code heuristic) =="
req GET "/vouchers/${v_full}" "${assigned_user_token}" ""
[[ "$(jqf .voucher.contractId)" == "null" ]] && ok "V_FULL starts with no contract link (never auto-inferred)" || bad "unexpected initial contractId: ${body}"
req PATCH "/vouchers/${v_full}/link-contract" "${assigned_user_token}" "{\"contractId\":\"${contract_c1}\",\"installmentId\":\"${installment_c1}\"}"
expect_status "link V_FULL to contract_c1/installment_c1" 200 "${status}"
req GET "/vouchers/${v_full}" "${assigned_user_token}" ""
[[ "$(jqf .voucher.contractId)" == "${contract_c1}" ]] && ok "contractId persisted on the voucher row" || bad "contractId not persisted: ${body}"
[[ "$(jqf .voucher.contractInstallmentId)" == "${installment_c1}" ]] && ok "contractInstallmentId persisted" || bad "contractInstallmentId not persisted: ${body}"

echo "== A1 — audit log captures what was actually changed, not just who/when =="
audit_body="$(psqlc "SELECT payload->'requestBody'->>'contractId' FROM audit_log WHERE action='PATCH /vouchers/${v_full}/link-contract' ORDER BY created_at DESC LIMIT 1")"
[[ "${audit_body}" == "${contract_c1}" ]] && ok "A1 audit_log.payload.requestBody records the actual contractId sent" || bad "A1 audit requestBody: ${audit_body}"
audit_pw="$(psqlc "SELECT payload->'requestBody'->>'password' FROM audit_log WHERE action='POST /tenant/admins' ORDER BY created_at ASC LIMIT 1")"
[[ "${audit_pw}" == "***redacted***" ]] && ok "A1 audit log redacts password fields, never stores the raw secret" || bad "A1 audit password redaction: ${audit_pw}"

req PATCH "/vouchers/${v_full}/link-contract" "${assigned_user_token}" "{\"contractId\":\"${contract_c2}\"}"
expect_status "link-contract omitting installmentId (contract-only link) works" 200 "${status}"

req PATCH "/vouchers/${v_full}/link-contract" "${admin_b_token}" "{\"contractId\":\"${contract_c1}\"}"
expect_status "Admin B (Tenant B) cannot link a Tenant A voucher (cross-tenant denied)" 404 "${status}"
req PATCH "/vouchers/${v_full}/link-contract" "${assigned_user_token}" "{\"contractId\":\"$(uuidgen)\"}"
expect_status "linking to a nonexistent/foreign contract id is rejected, not silently accepted" 404 "${status}"

echo ""
echo "== §11 J1 — deterministic journal-entry routing from the classified outcome (never an LLM, never invented) =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-J1-FULL","buyerMst":"0100000001","totalAmount":2000000,"currency":"VND","paymentMethod":"CK"}'
v_j1_full="$(jqf .voucher.id)"
req POST "/vouchers/${v_j1_full}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "FULL_MATCH" ]] && ok "J1 V_FULL -> FULL_MATCH" || bad "J1 V_FULL outcome: ${body}"
req GET "/vouchers/${v_j1_full}" "${assigned_user_token}" ""
[[ "$(jqf '.journalSuggestions[0].debitAccount')" == "1121" && "$(jqf '.journalSuggestions[0].creditAccount')" == "131" ]] && ok "J1 normal receipt (CK) -> Nợ 1121 / Có 131" || bad "J1 normal receipt entry: ${body}"
[[ "$(jqf '.journalSuggestions | length')" == "1" ]] && ok "J1 normal receipt proposes exactly 1 entry" || bad "J1 normal receipt entry count: ${body}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-J1-PARTIAL","buyerMst":"0100000001","totalAmount":800000,"currency":"VND","paymentMethod":"TM"}'
v_j1_partial="$(jqf .voucher.id)"
req POST "/vouchers/${v_j1_partial}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "PARTIAL_PAYMENT" ]] && ok "J1 V_PARTIAL -> PARTIAL_PAYMENT" || bad "J1 V_PARTIAL outcome: ${body}"
req GET "/vouchers/${v_j1_partial}" "${assigned_user_token}" ""
[[ "$(jqf '.journalSuggestions[0].debitAccount')" == "1111" ]] && ok "J1 partial receipt (TM) -> Nợ 1111 (cash, not bank)" || bad "J1 partial receipt entry: ${body}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-J1-OVER","buyerMst":"0100000001","totalAmount":2500000,"currency":"VND","paymentMethod":"CK"}'
v_j1_over="$(jqf .voucher.id)"
req POST "/vouchers/${v_j1_over}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "OVERPAYMENT" ]] && ok "J1 V_OVER -> OVERPAYMENT" || bad "J1 V_OVER outcome: ${body}"
req GET "/vouchers/${v_j1_over}" "${assigned_user_token}" ""
[[ "$(jqf '.journalSuggestions | length')" == "2" ]] && ok "J1 overpayment proposes 2 entries (matched + excess)" || bad "J1 overpayment entry count: ${body}"
[[ "$(jqf '.journalSuggestions[1].creditAccount')" == "3388" ]] && ok "J1 overpayment excess -> Có 3388 (separate from the matched 131 leg)" || bad "J1 overpayment excess entry: ${body}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-J1-ADVANCE","buyerMst":"0100000002","totalAmount":1000000,"currency":"VND","paymentMethod":"CK"}'
v_j1_advance="$(jqf .voucher.id)"
req POST "/vouchers/${v_j1_advance}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "ADVANCE_PAYMENT" ]] && ok "J1 V_ADVANCE -> ADVANCE_PAYMENT" || bad "J1 V_ADVANCE outcome: ${body}"
req GET "/vouchers/${v_j1_advance}" "${assigned_user_token}" ""
[[ "$(jqf '.journalSuggestions[0].creditAccount')" == "131" ]] && ok "J1 advance -> Có 131 (trả trước) — never auto-selects 3387" || bad "J1 advance entry: ${body}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-J1-AMBIG","buyerMst":"0199999999","totalAmount":300000,"currency":"VND","paymentMethod":"CK"}'
v_j1_ambig="$(jqf .voucher.id)"
req POST "/vouchers/${v_j1_ambig}/receipt-match/propose" "${assigned_user_token}" ""
[[ "$(jqf .outcome)" == "UNKNOWN_PAYER" ]] && ok "J1 V_AMBIG -> UNKNOWN_PAYER" || bad "J1 V_AMBIG outcome: ${body}"
req GET "/vouchers/${v_j1_ambig}" "${assigned_user_token}" ""
[[ "$(jqf '.journalSuggestions | length')" == "0" ]] && ok "J1 ambiguous outcome (UNKNOWN_PAYER) proposes NO entry — review only, nothing invented" || bad "J1 ambiguous entry count: ${body}"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-J1-NOMETHOD","buyerMst":"0100000001","totalAmount":2000000,"currency":"VND"}'
v_j1_nomethod="$(jqf .voucher.id)"
req POST "/vouchers/${v_j1_nomethod}/receipt-match/propose" "${assigned_user_token}" ""
req GET "/vouchers/${v_j1_nomethod}" "${assigned_user_token}" ""
[[ "$(jqf '.journalSuggestions | length')" == "0" ]] && ok "J1 unknown payment method -> no entry proposed (never guesses cash vs bank)" || bad "J1 no-method entry count: ${body}"

req POST "/vouchers/${v_j1_full}/journal-suggestions" "${assigned_user_token}" '{"entries":[{"entryNo":1,"debitAccount":"9999","creditAccount":"131","amount":100}],"replaceExisting":true}'
expect_status "J1 manual journal proposal with an out-of-allow-list account is rejected" 400 "${status}"

echo ""
echo "== N1 — Ban giám đốc weekly summary report (tenant-wide: dòng tiền, tuổi nợ, chi phí theo bộ phận) =="
req GET /tenant/reports/weekly-summary "${owner_a_token}" ""
expect_status "owner A (tenant_owner) can read the weekly summary" 200 "${status}"
[[ "$(jqf .cashCollectedThisWeek)" != "null" ]] && ok "N1 report includes cashCollectedThisWeek" || bad "N1 missing cashCollectedThisWeek: ${body}"
[[ "$(jqf .totalOpenReceivables)" != "null" ]] && ok "N1 report includes totalOpenReceivables (tuổi nợ)" || bad "N1 missing totalOpenReceivables: ${body}"
[[ "$(jqf '.expenseByDepartment | length')" -ge "1" ]] && ok "N1 report includes chi phí theo bộ phận, at least 1 department row" || bad "N1 expenseByDepartment: ${body}"
req GET /tenant/reports/weekly-summary "${admin_accounting_token}" ""
expect_status "plain department admin (not tenant_owner) cannot read the tenant-wide report" 403 "${status}"
req GET /tenant/reports/weekly-summary "${owner_b_token}" ""
expect_status "owner B (Tenant B) can read their own tenant's report" 200 "${status}"
[[ "$(jqf '.expenseByDepartment | map(.totalApproved) | add')" == "0" ]] && ok "N1 Tenant B's report shows zero expense activity — no leakage of Tenant A's data" || bad "N1 cross-tenant leakage: ${body}"

echo ""
echo "================================================================"
echo "RECEIPT/AR E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
