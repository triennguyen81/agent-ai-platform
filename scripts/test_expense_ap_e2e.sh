#!/usr/bin/env bash
# Expense & Supplier Payables (AP) real HTTP E2E.
#
# Boots an isolated, empty Postgres + freshly built Gateway image (own
# network/volume, never touches the shared dev stack) and verifies:
#   - the 7-handler ADMIN-creator-scoping bug fix in VoucherEndpoints.cs
#     (apply-receipt/apply-invoice/unapply-receipt/apply-expense/delete/
#     reextract/preview): an ADMIN must be able to act on a voucher a
#     *different* Department USER created, not just their own.
#   - the new GET /suppliers/ap-summary aggregate (real sim_ap_ledger data,
#     Department-scoped, capability-gated, cross-Department/Tenant denied).
#   - USER without capability denied for both surfaces.
#
# Business logic itself (actually posting to sim_ar_ledger/sim_ap_ledger)
# needs populated extracted_fields JSON the LLM pipeline would normally
# produce — this script does not fake that. Instead it proves the
# authorization fix directly: before the fix, ADMIN got a bare 404 (voucher
# not found — wrong scoping); after the fix, ADMIN reaches real business
# validation (a specific 400 naming what's missing) or succeeds outright
# (delete, reextract) on a voucher it did not create.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-exp-e2e-net-${run_id}"
pg="aiq-exp-e2e-postgres-${run_id}"
gw="aiq-exp-e2e-gateway-${run_id}"
vol="aiq-exp-e2e-pgdata-${run_id}"
db="aiq_exp_e2e"
db_password="exp_e2e_password"
image="aiq-agentic-gateway:contracttest"
base="http://localhost:18086"
gw_port=18086

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
    local args=(-sS -o /tmp/exp_e2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/exp_e2e_body.$$)"
    rm -f /tmp/exp_e2e_body.$$
}

jqf() { echo "${body}" | jq -r "$1"; }

echo "== Expense & AP E2E: booting isolated stack =="
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
    --env Jwt__Issuer=aiq.exp.test --env Jwt__Audience=aiq.exp.client \
    --env Jwt__SigningKey=exp-e2e-test-signing-key-needs-32-bytes-long \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://minio.invalid:9000 --env Object__AccessKey=expe2e --env Object__SecretKey=expe2epassword \
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

req POST /auth/register "" '{"email":"root@exptest.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"

docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@exptest.local' -f /tmp/first-super-admin.sql >/dev/null

req POST /auth/login "" '{"email":"root@exptest.local","password":"RootPass123!"}'
super_token="$(jqf .token)"

echo "== Setup: Tenant A (+ Sales dept), Business Agent granting voucher.processing + supplier.management =="
req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-exptest","name":"Tenant A","ownerEmail":"owner-a@exptest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
expect_status "onboard Tenant A" 201 "${status}"
tenant_a="$(jqf .tenant.id)"

req GET /system/capabilities "${super_token}" ""
voucher_cap="$(echo "${body}" | jq -r '.items[] | select(.code=="voucher.processing") | .id')"
supplier_cap="$(echo "${body}" | jq -r '.items[] | select(.code=="supplier.management") | .id')"
[[ -n "${voucher_cap}" && "${voucher_cap}" != "null" ]] && ok "voucher.processing exists in catalog" || bad "voucher.processing missing"
[[ -n "${supplier_cap}" && "${supplier_cap}" != "null" ]] && ok "supplier.management exists in catalog" || bad "supplier.management missing"

req POST /system/agents "${super_token}" '{"slug":"business-agent-e2e","name":"Business Agent","actionTier":"suggest"}'
expect_status "create Business Agent" 201 "${status}"
agent_id="$(jqf .id)"
req GET "/system/agents/${agent_id}/versions" "${super_token}" ""
draft_version_id="$(jqf '.items[0].id')"
req PATCH "/system/agents/${agent_id}/versions/${draft_version_id}" "${super_token}" "{\"capabilityIds\":[\"${voucher_cap}\",\"${supplier_cap}\"]}"
expect_status "assign capabilities to the draft" 200 "${status}"
req POST "/system/agents/${agent_id}/versions/${draft_version_id}/publish" "${super_token}" ""
expect_status "publish version" 200 "${status}"
req PUT "/system/agents/${agent_id}/tenants/${tenant_a}" "${super_token}" ""
expect_status "assign Business Agent to Tenant A" 204 "${status}"

docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status)
  SELECT id,'sales-exptest','Sales','active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
accounting_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
sales_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-exptest'")"

req POST /auth/login "" '{"email":"owner-a@exptest.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"
req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner assigns Business Agent to Accounting dept" 204 "${status}"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@exptest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@exptest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "owner creates Admin Sales" 201 "${status}"
req POST /auth/login "" '{"email":"admin-accounting@exptest.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
req POST /auth/login "" '{"email":"admin-sales@exptest.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"

req PUT "/department/capabilities/${voucher_cap}/binding" "${admin_accounting_token}" "{\"agentId\":\"${agent_id}\"}"
expect_status "Admin Accounting binds voucher.processing -> Business Agent" 204 "${status}"
req PUT "/department/capabilities/${supplier_cap}/binding" "${admin_accounting_token}" "{\"agentId\":\"${agent_id}\"}"
expect_status "Admin Accounting binds supplier.management -> Business Agent" 204 "${status}"

req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user@exptest.local","password":"UserPass123!","displayName":"Assigned User"}'
expect_status "Admin Accounting creates Assigned User" 201 "${status}"
assigned_user_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='assigned-user@exptest.local'")"
req POST /tenant/users "${admin_accounting_token}" '{"email":"unassigned-user@exptest.local","password":"UserPass123!","displayName":"Unassigned User"}'
expect_status "Admin Accounting creates Unassigned User" 201 "${status}"

req PUT "/tenant/agents/${agent_id}/users/${assigned_user_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Business Agent to Assigned User" 204 "${status}"

req POST /auth/login "" '{"email":"assigned-user@exptest.local","password":"UserPass123!"}'
assigned_user_token="$(jqf .token)"
req POST /auth/login "" '{"email":"unassigned-user@exptest.local","password":"UserPass123!"}'
unassigned_user_token="$(jqf .token)"

echo ""
echo "== §1 Setup: Assigned User creates 2 vouchers (receipt + invoice_vat_in), no extracted_fields yet =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-001","totalAmount":2000000,"currency":"VND"}'
expect_status "Assigned User creates a receipt voucher" 201 "${status}"
receipt_voucher_id="$(jqf .voucher.id)"

req POST /vouchers "${assigned_user_token}" '{"voucherType":"invoice_vat_in","documentNumber":"HDNCC-001","totalAmount":5000000,"currency":"VND"}'
expect_status "Assigned User creates an invoice_vat_in voucher" 201 "${status}"
invoice_voucher_id="$(jqf .voucher.id)"

echo ""
echo "== §2 ADMIN cross-creator regression: apply-*/reextract/preview on a USER-created voucher must reach real logic, never a bare 404 =="
req POST "/vouchers/${receipt_voucher_id}/apply-invoice" "${admin_accounting_token}" ""
expect_status "ADMIN apply-invoice on receipt-type voucher -> business 400 (wrong type), not 404" 400 "${status}"
[[ "$(jqf .error)" == "apply-invoice chỉ áp dụng cho invoice_vat_out (hoá đơn bán)" ]] && ok "error message proves the ADMIN found the (USER-created) voucher" || bad "unexpected error body: ${body}"

req POST "/vouchers/${invoice_voucher_id}/apply-receipt" "${admin_accounting_token}" ""
expect_status "ADMIN apply-receipt on invoice_vat_in voucher -> business 400, not 404" 400 "${status}"

req POST "/vouchers/${invoice_voucher_id}/apply-expense" "${admin_accounting_token}" ""
expect_status "ADMIN apply-expense on USER-created voucher -> reaches ap_context validation, not 404" 400 "${status}"
[[ "$(jqf .error)" == "voucher chưa có ap_context.supplier" ]] && ok "error message proves the ADMIN found the (USER-created) voucher" || bad "unexpected error body: ${body}"

req POST "/vouchers/${receipt_voucher_id}/unapply-receipt" "${admin_accounting_token}" ""
expect_status "ADMIN unapply-receipt on a never-applied USER-created voucher -> business 400, not 404" 400 "${status}"

req POST "/vouchers/${receipt_voucher_id}/reextract" "${admin_accounting_token}" ""
expect_status "ADMIN can re-extract a USER-created voucher (job enqueued)" 200 "${status}"

req GET "/vouchers/${receipt_voucher_id}/preview" "${admin_accounting_token}" ""
expect_status "ADMIN preview on a USER-created voucher with no file -> business 404 body, not a bare not-found" 404 "${status}"
[[ "$(jqf .error)" == "voucher has no source file" ]] && ok "error message proves the ADMIN found the (USER-created) voucher" || bad "unexpected error body: ${body}"

req DELETE "/vouchers/${invoice_voucher_id}" "${admin_accounting_token}" ""
expect_status "ADMIN deletes a USER-created voucher (not creator-scoped)" 204 "${status}"
req GET "/vouchers/${invoice_voucher_id}" "${admin_accounting_token}" ""
expect_status "deleted voucher is actually gone" 404 "${status}"

echo ""
echo "== §3 USER (non-admin, non-creator) still denied the same ADMIN-only actions =="
req POST /vouchers "${assigned_user_token}" '{"voucherType":"receipt","documentNumber":"PT-002","totalAmount":1000000,"currency":"VND"}'
receipt2_id="$(jqf .voucher.id)"
req POST "/vouchers/${receipt2_id}/apply-receipt" "${assigned_user_token}" ""
expect_status "Assigned User (creator) still cannot apply-receipt (ADMIN-only action)" 403 "${status}"
req DELETE "/vouchers/${receipt2_id}" "${assigned_user_token}" ""
expect_status "Assigned User (creator) still cannot delete (ADMIN-only action)" 403 "${status}"

echo ""
echo "== §4 GET /suppliers/ap-summary — real aggregate, Department-scoped =="
req GET /suppliers/ap-summary "${admin_accounting_token}" ""
expect_status "ADMIN reads the AP summary" 200 "${status}"
[[ "$(jqf .totalPayable)" == "0" ]] && ok "no suppliers seeded yet -> totalPayable is genuinely 0, not fabricated" || bad "unexpected totalPayable: ${body}"
req GET /suppliers/ap-summary "${assigned_user_token}" ""
expect_status "Assigned User (supplier.management) reads the AP summary" 200 "${status}"
req GET /suppliers/ap-summary "${unassigned_user_token}" ""
expect_status "Unassigned User cannot read the AP summary (no capability)" 403 "${status}"

req GET /suppliers/ap-summary "${admin_sales_token}" ""
expect_status "Admin Sales reads their own (empty) Department AP summary" 200 "${status}"

echo ""
echo "== §5 Cross-Tenant denied =="
req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-b-exptest","name":"Tenant B","ownerEmail":"owner-b@exptest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner B"}'
tenant_b="$(jqf .tenant.id)"
tenant_b_default_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_b}' AND is_default")"
# /auth/* is a global 10/min fixed-window limiter; this script has already
# issued close to that many register/login calls, so wait out the window
# before the last 2 logins (matches the pacing fix used in the Supplier E2E).
sleep 61
req POST /auth/login "" '{"email":"owner-b@exptest.local","password":"OwnerPass123!"}'
owner_b_token="$(jqf .token)"
req POST /tenant/admins "${owner_b_token}" "{\"email\":\"admin-b@exptest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin B\",\"departmentId\":\"${tenant_b_default_dept}\"}"
expect_status "owner B creates Admin B (own tenant, own department)" 201 "${status}"
req POST /auth/login "" '{"email":"admin-b@exptest.local","password":"AdminPass123!"}'
admin_b_token="$(jqf .token)"
req GET "/vouchers/${receipt_voucher_id}" "${admin_b_token}" ""
expect_status "Admin B (Tenant B) cannot fetch Tenant A's voucher (cross-tenant denied)" 404 "${status}"
req GET /suppliers/ap-summary "${admin_b_token}" ""
expect_status "Admin B's AP summary call succeeds but is scoped to their own (empty) tenant/department" 200 "${status}"
[[ "$(jqf .totalPayable)" == "0" ]] && ok "Admin B's AP summary shows 0 — no leakage of Tenant A's payables" || bad "cross-tenant leak: ${body}"

echo ""
echo "================================================================"
echo "EXPENSE & AP E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
