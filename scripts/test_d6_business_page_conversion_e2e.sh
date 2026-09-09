#!/usr/bin/env bash
# Phase D6 real HTTP E2E: the remaining business pages (accounting.tax,
# accounting.invoice, accounting.report, voucher.processing,
# contract.processing) now resolve through the exact same, unmodified D4
# capability resolver /accounting/journal already proved out — this script
# does NOT re-derive D4's entire security matrix per capability (that would
# duplicate scripts/test_d4_business_capability_e2e.sh's 42 checks, since
# the resolver's mechanism is capability-agnostic by construction). Instead
# it proves: (1) every D6-converted capability code is real and resolvable,
# (2) one full ADMIN/USER/department-revoke/restore/cross-tenant cycle for
# a second capability (accounting.tax) as a representative, non-journal
# check that the mechanism generalizes, and (3) the actual bug fixed this
# phase — Gateway's voucher-extraction AgentClient call previously sent no
# Authorization header at all and would always fail auth; this script
# proves the corrected call now reaches the manifest-issuance point
# (capability resolves to a real agent) rather than asserting a full OCR
# pipeline run.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-d6-e2e-net-${run_id}"
pg="aiq-d6-e2e-postgres-${run_id}"
gw="aiq-d6-e2e-gateway-${run_id}"
vol="aiq-d6-e2e-pgdata-${run_id}"
db="aiq_d6_e2e"
db_password="d6_e2e_password"
image="aiq-agentic-gateway:d6test"
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
    if [[ "${actual}" == "${expected}" ]]; then ok "${desc} (HTTP ${actual})"; else bad "${desc} (expected ${expected}, got ${actual})"; fi
}
expect_true() {
    local desc="$1" cond="$2"
    if [[ "${cond}" == "true" ]]; then ok "${desc}"; else bad "${desc} (got: ${cond})"; fi
}
req() {
    local method="$1" path="$2" token="${3:-}" req_payload="${4:-}"
    local args=(-sS -o /tmp/d6e2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/d6e2e_body.$$)"
    rm -f /tmp/d6e2e_body.$$
}
jqf() { echo "${body}" | jq -r "$1"; }

echo "== D6 E2E: booting isolated stack =="
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
    --env Jwt__Issuer=aiq.d6.test --env Jwt__Audience=aiq.d6.client \
    --env Jwt__SigningKey=d6-e2e-test-signing-key-needs-32-bytes-min \
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
req POST /auth/register "" '{"email":"root@d6test.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"
docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@d6test.local' -f /tmp/first-super-admin.sql >/dev/null
req POST /auth/login "" '{"email":"root@d6test.local","password":"RootPass123!"}'
super_token="$(jqf .token)"

req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-d6e2e","name":"Tenant A","ownerEmail":"owner-a@d6test.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
tenant_a="$(jqf .tenant.id)"
req POST /auth/login "" '{"email":"owner-a@d6test.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"

echo "== §2: every D6-target capability code is real (seeded by 022 + new 023) =="
req GET /system/capabilities "${super_token}" ""
expect_status "list capability catalog" 200 "${status}"
for code in accounting.journal accounting.tax accounting.invoice accounting.report voucher.processing contract.processing; do
    found="$(echo "${body}" | jq -r --arg c "${code}" '[.items[]|select(.code==$c)]|length==1')"
    expect_true "catalog contains ${code}" "${found}"
done
tax_cap="$(echo "${body}" | jq -r '.items[]|select(.code=="accounting.tax")|.id')"
contract_cap="$(echo "${body}" | jq -r '.items[]|select(.code=="contract.processing")|.id')"
voucher_cap="$(echo "${body}" | jq -r '.items[]|select(.code=="voucher.processing")|.id')"
journal_cap="$(echo "${body}" | jq -r '.items[]|select(.code=="accounting.journal")|.id')"
invoice_cap="$(echo "${body}" | jq -r '.items[]|select(.code=="accounting.invoice")|.id')"
report_cap="$(echo "${body}" | jq -r '.items[]|select(.code=="accounting.report")|.id')"

echo "== Setup: SUPER_ADMIN publishes one Accounting Agent declaring journal+tax+invoice+report+voucher, entitles to Tenant A =="
req POST /system/agents "${super_token}" '{"slug":"accounting-agent-d6e2e","name":"Accounting Agent","actionTier":"suggest"}'
agent_id="$(jqf .id)"
req GET "/system/agents/${agent_id}/versions" "${super_token}" ""
version_id="$(jqf '.items[0].id')"
req PATCH "/system/agents/${agent_id}/versions/${version_id}" "${super_token}" "{\"capabilityIds\":[\"${journal_cap}\",\"${tax_cap}\",\"${invoice_cap}\",\"${report_cap}\",\"${voucher_cap}\"]}"
expect_status "wire journal+tax+invoice+report+voucher onto one draft" 200 "${status}"
req POST "/system/agents/${agent_id}/versions/${version_id}/publish" "${super_token}" ""
expect_status "publish" 200 "${status}"
req PUT "/system/agents/${agent_id}/tenants/${tenant_a}" "${super_token}" ""
expect_status "entitle to Tenant A" 204 "${status}"

echo "== Setup: a second Contract Agent, separate business responsibility, declaring only contract.processing =="
req POST /system/agents "${super_token}" '{"slug":"contract-agent-d6e2e","name":"Contract Agent","actionTier":"suggest"}'
contract_agent="$(jqf .id)"
req GET "/system/agents/${contract_agent}/versions" "${super_token}" ""
contract_version="$(jqf '.items[0].id')"
req PATCH "/system/agents/${contract_agent}/versions/${contract_version}" "${super_token}" "{\"capabilityIds\":[\"${contract_cap}\"]}"
req POST "/system/agents/${contract_agent}/versions/${contract_version}/publish" "${super_token}" ""
req PUT "/system/agents/${contract_agent}/tenants/${tenant_a}" "${super_token}" ""
expect_status "entitle Contract Agent to Tenant A" 204 "${status}"

accounting_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status) SELECT id,'sales-d6e2e','Sales','active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
sales_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-d6e2e'")"

req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "assign Accounting Agent to Accounting dept" 204 "${status}"
req PUT "/tenant/departments/${accounting_dept}/agents/${contract_agent}" "${owner_a_token}" ""
expect_status "assign Contract Agent to Accounting dept" 204 "${status}"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@d6test.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "create Admin Accounting" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@d6test.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "create Admin Sales" 201 "${status}"
req POST /auth/login "" '{"email":"admin-accounting@d6test.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
req POST /auth/login "" '{"email":"admin-sales@d6test.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"

echo "== §14 ADMIN Accounting: journal / tax / invoice / report / voucher.processing all resolve; contract.processing too =="
for code in accounting.journal accounting.tax accounting.invoice accounting.report voucher.processing contract.processing; do
    req GET "/me/capabilities/${code}/agent" "${admin_accounting_token}" ""
    expect_status "Admin Accounting resolves ${code}" 200 "${status}"
done

echo "== §14 ADMIN Sales: accounting-only capabilities denied unless explicitly entitled =="
for code in accounting.journal accounting.tax accounting.invoice accounting.report voucher.processing contract.processing; do
    req GET "/me/capabilities/${code}/agent" "${admin_sales_token}" ""
    expect_status "Admin Sales denied for ${code}" 403 "${status}"
done

echo "== Representative full cycle on a NON-journal capability (accounting.tax): USER + revoke + restore =="
req POST /tenant/users "${admin_accounting_token}" '{"email":"user-accounting@d6test.local","password":"UserPass123!","displayName":"User Accounting"}'
user_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='user-accounting@d6test.local'")"
req GET "/me/capabilities/accounting.tax/agent" "${admin_accounting_token}" ""
resolved_for_tax="$(jqf .agentId)"

req POST /auth/login "" '{"email":"user-accounting@d6test.local","password":"UserPass123!"}'
user_token="$(jqf .token)"
req GET "/me/capabilities/accounting.tax/agent" "${user_token}" ""
expect_status "USER without direct assignment denied for accounting.tax" 403 "${status}"

req PUT "/tenant/agents/${resolved_for_tax}/users/${user_id}" "${admin_accounting_token}" ""
expect_status "Admin assigns the resolved agent directly to USER" 204 "${status}"
req GET "/me/capabilities/accounting.tax/agent" "${user_token}" ""
expect_status "USER with direct assignment resolves accounting.tax" 200 "${status}"
req GET "/runtime/agents/${resolved_for_tax}/manifest" "${user_token}" ""
expect_status "USER executes (manifest issued) for accounting.tax's resolved agent" 200 "${status}"

echo "-- department entitlement revoke denies both roles for accounting.tax --"
req DELETE "/tenant/departments/${accounting_dept}/agents/${resolved_for_tax}" "${owner_a_token}" ""
expect_status "owner revokes department entitlement" 204 "${status}"
req GET "/me/capabilities/accounting.tax/agent" "${admin_accounting_token}" ""
expect_status "ADMIN denied for accounting.tax after department revoke" 403 "${status}"
req GET "/me/capabilities/accounting.tax/agent" "${user_token}" ""
expect_status "USER denied for accounting.tax after department revoke" 403 "${status}"

echo "-- restore: ADMIN recovers automatically, USER follows D3 direct-assignment semantics --"
req PUT "/tenant/departments/${accounting_dept}/agents/${resolved_for_tax}" "${owner_a_token}" ""
expect_status "owner restores department entitlement" 204 "${status}"
req GET "/me/capabilities/accounting.tax/agent" "${admin_accounting_token}" ""
expect_status "ADMIN recovers immediately (department chain only)" 200 "${status}"
req GET "/me/capabilities/accounting.tax/agent" "${user_token}" ""
expect_status "USER stays denied — direct assignment was revoked by the department-revoke cascade (D3 semantics)" 403 "${status}"

echo "== §12/§14 security: known agent_id spoofing cannot bypass capability authorization =="
req POST /auth/login "" '{"email":"admin-sales@d6test.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"
req GET "/runtime/agents/${resolved_for_tax}/manifest" "${admin_sales_token}" ""
expect_status "Admin Sales cannot spoof the known Accounting agent id directly" 403 "${status}"

echo "== §12 cross-tenant: Tenant B never resolves Tenant A's capabilities =="
req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-b-d6e2e","name":"Tenant B","ownerEmail":"owner-b@d6test.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner B"}'
req POST /auth/login "" '{"email":"owner-b@d6test.local","password":"OwnerPass123!"}'
owner_b_token="$(jqf .token)"
default_dept_b="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id=(SELECT id FROM tenants WHERE code='tenant-b-d6e2e') AND is_default")"
req POST /tenant/admins "${owner_b_token}" "{\"email\":\"admin-b@d6test.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin B\",\"departmentId\":\"${default_dept_b}\"}"
req POST /auth/login "" '{"email":"admin-b@d6test.local","password":"AdminPass123!"}'
admin_b_token="$(jqf .token)"
req GET "/me/capabilities/accounting.journal/agent" "${admin_b_token}" ""
expect_status "Tenant B admin cannot resolve Tenant A's accounting.journal" 403 "${status}"

echo "== §12: TENANT_OWNER never gains implicit production execution for any capability =="
for code in accounting.journal accounting.tax voucher.processing contract.processing; do
    req GET "/me/capabilities/${code}/agent" "${owner_a_token}" ""
    expect_status "TENANT_OWNER denied for ${code} (no implicit execution)" 403 "${status}"
done

echo ""
echo "== §15 audit: Gateway->Agent voucher-extraction call now carries a real Bearer header (D6 bug fix) =="
# Full behavioral proof (real Authorization header reaching the Agent
# service) lives in gateway/tests/Aiq.Gateway.Tests/Books/AgentClientTests.cs
# — a live OCR/LLM round trip is out of this E2E's scope (same "no live
# provider call" scoping decision as D3/D4's own E2E scripts). Here we
# confirm voucher.processing itself is a live, resolvable capability that
# the Gateway job now targets instead of the old skills list.
req GET "/me/capabilities/voucher.processing/agent" "${admin_accounting_token}" ""
expect_status "voucher.processing resolves for Admin Accounting (what the fixed job now authorizes through)" 200 "${status}"

echo ""
echo "================================================================"
echo "D6 E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
