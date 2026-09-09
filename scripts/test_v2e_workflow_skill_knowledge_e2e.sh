#!/usr/bin/env bash
# ACL V2-E real HTTP E2E: proves route/policy wiring for Department Workflow
# (immutable draft/publish versioning), Skill (declarative, content-locked
# once published), and Knowledge (department notes + Department Agent
# attachment) ownership. The xUnit tests
# (DepartmentWorkflowSkillKnowledgeIntegrationTests.cs) call repository
# methods directly and prove the SQL/ownership logic; this script exercises
# the actual mapped HTTP routes, policies, and JSON contracts, matching the
# V2-D precedent (test_v2d_department_capabilities_e2e.sh).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-v2e-e2e-net-${run_id}"
pg="aiq-v2e-e2e-postgres-${run_id}"
gw="aiq-v2e-e2e-gateway-${run_id}"
db="aiq_v2e_e2e"
db_password="v2e_e2e_password"
image="aiq-agentic-gateway:latest"
base="http://localhost:18087"
gw_port=18087

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
    local args=(-sS -o /tmp/v2ee2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/v2ee2e_body.$$)"
    rm -f /tmp/v2ee2e_body.$$
}
jqf() { echo "${body}" | jq -r "$1"; }

echo "== V2-E E2E: booting isolated stack =="
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
    --env Jwt__Issuer=aiq.v2e.test --env Jwt__Audience=aiq.v2e.client \
    --env Jwt__SigningKey=v2e-e2e-test-signing-key-needs-32-bytes-min \
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
req POST /auth/register "" '{"email":"root@v2etest.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"
docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@v2etest.local' -f /tmp/first-super-admin.sql >/dev/null
req POST /auth/login "" '{"email":"root@v2etest.local","password":"RootPass123!"}'
super_token="$(jqf .token)"

req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-v2ee2e","name":"Tenant A","ownerEmail":"owner-a@v2etest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
tenant_a="$(jqf .tenant.id)"
req POST /auth/login "" '{"email":"owner-a@v2etest.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"
accounting_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status) SELECT id,'sales-v2ee2e','Sales','active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
sales_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-v2ee2e'")"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@v2etest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@v2etest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "owner creates Admin Sales" 201 "${status}"
req POST /auth/login "" '{"email":"admin-accounting@v2etest.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
req POST /auth/login "" '{"email":"admin-sales@v2etest.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"

echo ""
echo "== §Workflow: create -> edit draft -> publish -> immutable -> new draft =="
req POST /department/workflows "${admin_accounting_token}" '{"name":"Invoice Review","description":"desc"}'
expect_status "ADMIN Accounting creates a Department Workflow" 201 "${status}"
workflow_id="$(jqf .id)"
draft_version_id="$(jqf .currentDraftVersionId)"
expect_true "workflow creation auto-provisioned draft v1" "$([[ "${draft_version_id}" != "null" ]] && echo true || echo false)"

req PATCH "/department/workflows/${workflow_id}/versions/${draft_version_id}" "${admin_accounting_token}" '{"definition":{"steps":[{"type":"prompt","prompt":"step one"}],"requiresApproval":false}}'
expect_status "ADMIN Accounting edits the draft definition" 200 "${status}"

req POST "/department/workflows/${workflow_id}/versions/${draft_version_id}/publish" "${admin_accounting_token}" ""
expect_status "ADMIN Accounting publishes v1" 200 "${status}"

req PATCH "/department/workflows/${workflow_id}/versions/${draft_version_id}" "${admin_accounting_token}" '{"definition":{"steps":[],"requiresApproval":false}}'
expect_status "editing the now-published version is rejected (not draft)" 404 "${status}"

req POST "/department/workflows/${workflow_id}/versions" "${admin_accounting_token}" '{}'
expect_status "ADMIN Accounting creates draft v2 after publish" 201 "${status}"
draft2_id="$(jqf .id)"
expect_true "draft v2 is version 2" "$([[ "$(jqf .version)" == "2" ]] && echo true || echo false)"

req POST "/department/workflows/${workflow_id}/versions/${draft2_id}/publish" "${admin_accounting_token}" ""
expect_status "publishing draft v2 with an empty step list is denied" 400 "${status}"

echo "== cross-department / cross-tenant denial =="
req GET "/department/workflows/${workflow_id}" "${admin_sales_token}" ""
expect_status "ADMIN Sales cannot read Accounting's workflow" 404 "${status}"
req POST "/department/workflows/${workflow_id}/versions/${draft2_id}/publish" "${admin_sales_token}" ""
expect_status "ADMIN Sales cannot publish Accounting's draft" 404 "${status}"

echo ""
echo "== §Skill: create -> attach to draft -> publish locks content =="
req POST /department/skills "${admin_accounting_token}" '{"code":"journal-helper","name":"Journal Helper","declarativeContent":"Review journal entries."}'
expect_status "ADMIN Accounting creates a Department Skill" 201 "${status}"
skill_id="$(jqf .id)"

req GET /department/agents "${admin_accounting_token}" ""
req POST /department/agents "${admin_accounting_token}" '{"slug":"v2e-acct-bot","name":"V2E Accounting Bot"}'
expect_status "ADMIN Accounting creates a Department Agent" 201 "${status}"
dept_agent_id="$(jqf .id)"
req GET "/department/agents/${dept_agent_id}" "${admin_accounting_token}" ""
dept_version_id="$(jqf .currentDraftVersionId)"

req PATCH "/department/agents/${dept_agent_id}/versions/${dept_version_id}" "${admin_accounting_token}" "{\"provider\":\"claude_sdk\",\"model\":\"sonnet\",\"systemPrompt\":\"You help.\",\"temperature\":0.2,\"maxTurns\":4,\"timeoutSeconds\":120,\"maxTokens\":2048,\"skills\":[\"journal-helper\"],\"workflowId\":\"${workflow_id}\"}"
expect_status "ADMIN Accounting attaches its own Skill + published Workflow to its draft" 200 "${status}"

req PATCH "/department/skills/${skill_id}/content" "${admin_accounting_token}" '{"declarativeContent":"Edited while still draft."}'
expect_status "Skill content still editable while only a DRAFT agent version references it" 200 "${status}"

req POST "/department/agents/${dept_agent_id}/versions/${dept_version_id}/publish" "${admin_accounting_token}" ""
expect_status "ADMIN Accounting publishes the agent version" 200 "${status}"

req PATCH "/department/skills/${skill_id}/content" "${admin_accounting_token}" '{"declarativeContent":"Attempted post-publish edit."}'
expect_status "Skill content locked once referenced by a PUBLISHED agent version" 409 "${status}"

req PATCH "/department/skills/${skill_id}" "${admin_accounting_token}" '{"name":"Renamed"}'
expect_status "Skill metadata (name) still editable after publish-lock" 200 "${status}"

echo "== cross-department skill/workflow attach denial =="
req POST /department/skills "${admin_sales_token}" '{"code":"sales-pitch","name":"Sales Pitch","declarativeContent":"Sales only."}'
expect_status "ADMIN Sales creates its own skill" 201 "${status}"
req POST /department/agents "${admin_accounting_token}" '{"slug":"v2e-acct-bot-2","name":"V2E Accounting Bot 2"}'
second_agent_id="$(jqf .id)"
req GET "/department/agents/${second_agent_id}" "${admin_accounting_token}" ""
second_version_id="$(jqf .currentDraftVersionId)"
req PATCH "/department/agents/${second_agent_id}/versions/${second_version_id}" "${admin_accounting_token}" '{"provider":"claude_sdk","model":"sonnet","systemPrompt":"x","temperature":0.2,"maxTurns":4,"timeoutSeconds":120,"maxTokens":2048,"skills":["sales-pitch"]}'
expect_status "ADMIN Accounting cannot attach Sales's own skill code" 400 "${status}"

echo ""
echo "== §Knowledge: create -> list -> attach to Department Agent -> cross-department denial =="
req POST /department/knowledge "${admin_accounting_token}" '{"title":"Chart of Accounts","content":"1000 Cash","tags":["coa"]}'
expect_status "ADMIN Accounting creates a Department Knowledge item" 201 "${status}"
note_id="$(jqf .id)"

req GET /department/knowledge "${admin_accounting_token}" ""
expect_true "own Knowledge list contains it" "$(echo "${body}" | jq -r --arg id "${note_id}" '[.items[]|select(.id==$id)]|length==1')"

req GET "/department/agents/${dept_agent_id}/knowledge/attachable" "${admin_accounting_token}" ""
expect_true "Knowledge item appears in this agent's attachable list" "$(echo "${body}" | jq -r --arg id "${note_id}" '[.items[]|select(.id==$id)]|length==1')"

req PUT "/department/agents/${dept_agent_id}/knowledge/${note_id}" "${admin_accounting_token}" ""
expect_status "ADMIN Accounting attaches its Knowledge to its own agent" 204 "${status}"

req GET "/department/agents/${dept_agent_id}/knowledge" "${admin_accounting_token}" ""
expect_true "attached list now contains it" "$(echo "${body}" | jq -r --arg id "${note_id}" '[.items[]|select(.id==$id)]|length==1')"

req DELETE "/department/knowledge/${note_id}" "${admin_accounting_token}" ""
expect_status "deleting a Knowledge item while attached to an agent is refused" 409 "${status}"

req GET "/department/knowledge/${note_id}" "${admin_sales_token}" ""
expect_status "ADMIN Sales cannot read Accounting's Knowledge item" 404 "${status}"
req PUT "/department/agents/${second_agent_id}/knowledge/${note_id}" "${admin_sales_token}" ""
expect_status "ADMIN Sales cannot attach Accounting's Knowledge to any agent (agent ownership check fails first)" 404 "${status}"

req DELETE "/department/agents/${dept_agent_id}/knowledge/${note_id}" "${admin_accounting_token}" ""
expect_status "ADMIN Accounting detaches it" 204 "${status}"
req DELETE "/department/knowledge/${note_id}" "${admin_accounting_token}" ""
expect_status "delete now succeeds once detached" 204 "${status}"

echo ""
echo "================================================================"
echo "V2-E E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
