#!/usr/bin/env bash
# ACL V2-D real HTTP E2E: proves the actual route/policy wiring for
# Department Business Capability ownership — the new xUnit integration
# tests (DepartmentCapabilityOwnershipIntegrationTests.cs) call
# CapabilityRepository/DynamicAgentRepository methods directly and prove the
# SQL/authorization logic is correct, but never exercise the actual mapped
# HTTP routes, policies, or JSON contracts. This script does not re-derive
# D4's full capability-security matrix (scripts/test_d4_business_capability_e2e.sh
# already covers that and was re-verified this phase) — it proves the NEW
# V2-D-specific pieces: /department/capabilities CRUD, the moved binding
# endpoint, catalog_reader's widened GET routes, and that a V2-C Department
# Agent's capability is now visible end-to-end over real HTTP.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-v2d-e2e-net-${run_id}"
pg="aiq-v2d-e2e-postgres-${run_id}"
gw="aiq-v2d-e2e-gateway-${run_id}"
db="aiq_v2d_e2e"
db_password="v2d_e2e_password"
image="aiq-agentic-gateway:latest"
base="http://localhost:18086"
gw_port=18086

pass=0
fail=0
status=""
body=""

cleanup() {
    docker rm -f "${gw}" >/dev/null 2>&1 || true
    docker rm -f "${pg}" >/dev/null 2>&1 || true
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
    local args=(-sS -o /tmp/v2de2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/v2de2e_body.$$)"
    rm -f /tmp/v2de2e_body.$$
}
jqf() { echo "${body}" | jq -r "$1"; }

echo "== V2-D E2E: booting isolated stack =="
docker network create "${net}" >/dev/null
docker run --detach --name "${pg}" --network "${net}" --network-alias postgres \
    --env POSTGRES_USER=postgres --env POSTGRES_PASSWORD="${db_password}" --env POSTGRES_DB="${db}" \
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
    --env Jwt__Issuer=aiq.v2d.test --env Jwt__Audience=aiq.v2d.client \
    --env Jwt__SigningKey=v2d-e2e-test-signing-key-needs-32-bytes-min \
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
req POST /auth/register "" '{"email":"root@v2dtest.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"
docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@v2dtest.local' -f /tmp/first-super-admin.sql >/dev/null
req POST /auth/login "" '{"email":"root@v2dtest.local","password":"RootPass123!"}'
super_token="$(jqf .token)"

req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-v2de2e","name":"Tenant A","ownerEmail":"owner-a@v2dtest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
tenant_a="$(jqf .tenant.id)"
req POST /auth/login "" '{"email":"owner-a@v2dtest.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"
accounting_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status) SELECT id,'sales-v2de2e','Sales','active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
sales_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-v2de2e'")"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@v2dtest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@v2dtest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "owner creates Admin Sales" 201 "${status}"
req POST /auth/login "" '{"email":"admin-accounting@v2dtest.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
req POST /auth/login "" '{"email":"admin-sales@v2dtest.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"

echo ""
echo "== §catalog_reader: ADMIN can now browse (never mutate) platform catalogs =="
req GET /system/capabilities "${admin_accounting_token}" ""
expect_status "ADMIN reads platform capability catalog" 200 "${status}"
req GET /tools "${admin_accounting_token}" ""
expect_status "ADMIN reads platform tool registry" 200 "${status}"
req GET /workflows "${admin_accounting_token}" ""
expect_status "ADMIN reads platform workflow catalog" 200 "${status}"
req POST /system/capabilities "${admin_accounting_token}" '{"code":"hack.attempt","name":"x"}'
expect_status "ADMIN cannot mutate the SUPER_ADMIN-owned platform catalog" 403 "${status}"

echo ""
echo "== §Department capability CRUD: ADMIN Accounting owns its own capability =="
req POST /department/capabilities "${admin_accounting_token}" '{"code":"crm.lead_management","name":"Quan ly khach hang tiem nang","category":"crm"}'
expect_status "ADMIN Accounting creates a Department capability" 201 "${status}"
own_capability_id="$(jqf .id)"
expect_true "created capability is scope=department" "$([[ "$(jqf .scope)" == "department" ]] && echo true || echo false)"

req GET /department/capabilities "${admin_accounting_token}" ""
expect_status "ADMIN Accounting lists its own capabilities" 200 "${status}"
expect_true "own capability appears in the list" "$(echo "${body}" | jq -r --arg id "${own_capability_id}" '[.items[]|select(.id==$id)]|length==1')"

echo "== cross-department denial: ADMIN Sales cannot read/edit/enable Accounting's own capability =="
req GET "/department/capabilities/${own_capability_id}" "${admin_sales_token}" ""
expect_status "ADMIN Sales cannot read Accounting's capability" 404 "${status}"
req PATCH "/department/capabilities/${own_capability_id}" "${admin_sales_token}" '{"name":"hacked"}'
expect_status "ADMIN Sales cannot edit Accounting's capability" 404 "${status}"
req POST "/department/capabilities/${own_capability_id}/disable" "${admin_sales_token}" ""
expect_status "ADMIN Sales cannot disable Accounting's capability" 404 "${status}"

echo "== duplicate code (even cross-department) rejected — global uniqueness preserved =="
req POST /department/capabilities "${admin_sales_token}" '{"code":"crm.lead_management","name":"Duplicate"}'
expect_status "ADMIN Sales cannot create a capability with Accounting's exact code" 409 "${status}"

echo ""
echo "== §TENANT_OWNER: read-only oversight only, binding mutation denied =="
req GET "/tenant/departments/${accounting_dept}/capabilities" "${owner_a_token}" ""
expect_status "TENANT_OWNER GET department capability oversight" 200 "${status}"
req PUT "/tenant/departments/${accounting_dept}/capabilities/${own_capability_id}/binding" "${owner_a_token}" '{"agentId":"00000000-0000-0000-0000-000000000000"}'
expect_status "TENANT_OWNER's old binding-mutation route no longer exists" 404 "${status}"
req PUT "/department/capabilities/${own_capability_id}/binding" "${owner_a_token}" '{"agentId":"00000000-0000-0000-0000-000000000000"}'
expect_status "TENANT_OWNER denied on ADMIN's own binding route too (department_admin policy, literal role)" 403 "${status}"
req POST /department/capabilities "${owner_a_token}" '{"code":"owner.attempt","name":"x"}'
expect_status "TENANT_OWNER cannot create a Department capability at all" 403 "${status}"

echo ""
echo "== §V2-C integration: a Department Agent's declared capability is visible end-to-end over real HTTP =="
req POST /department/agents "${admin_accounting_token}" '{"slug":"v2d-acct-bot","name":"V2D Accounting Bot"}'
expect_status "ADMIN Accounting creates a Department Agent" 201 "${status}"
dept_agent_id="$(jqf .id)"
# trg_create_initial_agent_version fires AFTER INSERT and commits
# current_draft_version_id after the CREATE response's RETURNING is already
# computed, so re-fetch to see the trigger-populated draft version id.
req GET "/department/agents/${dept_agent_id}" "${admin_accounting_token}" ""
dept_version_id="$(jqf .currentDraftVersionId)"
expect_true "agent creation auto-provisioned an initial draft version" "$([[ "${dept_version_id}" != "null" ]] && echo true || echo false)"
req PATCH "/department/agents/${dept_agent_id}/versions/${dept_version_id}" "${admin_accounting_token}" "{\"provider\":\"claude_sdk\",\"model\":\"sonnet\",\"systemPrompt\":\"You help with accounting.\",\"temperature\":0.2,\"maxTurns\":4,\"timeoutSeconds\":120,\"maxTokens\":2048,\"capabilityIds\":[\"${own_capability_id}\"]}"
expect_status "ADMIN Accounting attaches its own capability to its own draft version" 200 "${status}"
req POST "/department/agents/${dept_agent_id}/versions/${dept_version_id}/publish" "${admin_accounting_token}" ""
expect_status "ADMIN Accounting publishes the version" 200 "${status}"

req GET /me/capabilities "${admin_accounting_token}" ""
expect_true "ADMIN's Nghiệp vụ menu now contains the Department Agent's own capability" \
  "$(echo "${body}" | jq -r --arg c "crm.lead_management" '[.items[]|select(.code==$c)]|length==1')"

req GET "/me/capabilities/crm.lead_management/agent" "${admin_accounting_token}" ""
expect_status "ADMIN resolves its own Department Agent for its own capability" 200 "${status}"
expect_true "resolves to the exact Department Agent" "$([[ "$(jqf .agentId)" == "${dept_agent_id}" ]] && echo true || echo false)"

req GET /me/capabilities "${admin_sales_token}" ""
expect_true "ADMIN Sales' menu does NOT contain Accounting's Department capability" \
  "$(echo "${body}" | jq -r '[.items[]|select(.code=="crm.lead_management")]|length==0')"

echo "== attaching another department's capability to own draft is denied =="
req POST /department/capabilities "${admin_sales_token}" '{"code":"sales.own_capability","name":"Sales Only"}'
expect_status "ADMIN Sales creates its own capability" 201 "${status}"
sales_capability_id="$(jqf .id)"
req POST /department/agents "${admin_accounting_token}" '{"slug":"v2d-acct-bot-2","name":"V2D Accounting Bot 2"}'
second_agent_id="$(jqf .id)"
req GET "/department/agents/${second_agent_id}" "${admin_accounting_token}" ""
second_version_id="$(jqf .currentDraftVersionId)"
req PATCH "/department/agents/${second_agent_id}/versions/${second_version_id}" "${admin_accounting_token}" "{\"provider\":\"claude_sdk\",\"model\":\"sonnet\",\"systemPrompt\":\"x\",\"temperature\":0.2,\"maxTurns\":4,\"timeoutSeconds\":120,\"maxTokens\":2048,\"capabilityIds\":[\"${sales_capability_id}\"]}"
expect_status "ADMIN Accounting cannot attach Sales's own capability to its own draft" 400 "${status}"

echo ""
echo "================================================================"
echo "V2-D E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
