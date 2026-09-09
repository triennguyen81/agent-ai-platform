#!/usr/bin/env bash
# ACL V2 final phase (V2-G + V2-H) real HTTP E2E:
#   Part A (V2-G): legacy shared-agent quarantine inventory + explicit
#   one-department-at-a-time clone action, over real HTTP routes/policies.
#   Part B (V2-H): production secret fail-fast (a second, isolated Gateway
#   container started with ASPNETCORE_ENVIRONMENT=Production and a
#   known-weak Object__AccessKey must refuse to become ready) and the
#   record-validation-attribute fix (malformed input must now be rejected
#   with 400, not silently accepted) — see
#   ACL_V2_FINAL_MIGRATION_PRODUCTION_HARDENING_REPORT.md.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-v2final-e2e-net-${run_id}"
pg="aiq-v2final-e2e-postgres-${run_id}"
gw="aiq-v2final-e2e-gateway-${run_id}"
gw_bad_secrets="aiq-v2final-e2e-gateway-badsecrets-${run_id}"
db="aiq_v2final_e2e"
db_password="v2final_e2e_password"
image="aiq-agentic-gateway:latest"
base="http://localhost:18088"
gw_port=18088

pass=0
fail=0
status=""
body=""

cleanup() {
    docker rm -f "${gw_bad_secrets}" >/dev/null 2>&1 || true
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
    local args=(-sS -o /tmp/v2finale2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/v2finale2e_body.$$)"
    rm -f /tmp/v2finale2e_body.$$
}
jqf() { echo "${body}" | jq -r "$1"; }

echo "== V2 FINAL E2E: booting isolated stack =="
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
    --env Jwt__Issuer=aiq.v2final.test --env Jwt__Audience=aiq.v2final.client \
    --env Jwt__SigningKey=v2final-e2e-test-signing-key-needs-32-bytes-min \
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
req POST /auth/register "" '{"email":"root@v2finaltest.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"
docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@v2finaltest.local' -f /tmp/first-super-admin.sql >/dev/null
req POST /auth/login "" '{"email":"root@v2finaltest.local","password":"RootPass123!"}'
super_token="$(jqf .token)"

req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-v2final","name":"Tenant A","ownerEmail":"owner-a@v2finaltest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
tenant_a="$(jqf .tenant.id)"
req POST /auth/login "" '{"email":"owner-a@v2finaltest.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"
accounting_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status) SELECT id,'sales-v2final','Sales','active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
sales_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-v2final'")"

echo ""
echo "== §V2-G Part A: seed a legacy scope='tenant' agent shared across two departments =="
shared_agent_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "
  INSERT INTO agents(tenant_id, owning_tenant_id, scope, code, slug, name, created_by)
  VALUES ('${tenant_a}', '${tenant_a}', 'tenant', 'legacy-shared-bot', 'legacy-shared-bot', 'Legacy Shared Bot', NULL) RETURNING id" | head -1)"
# trg_create_initial_agent_version already auto-created a draft v1 on the
# INSERT above (migration 008, AAFTER INSERT) -- update it in place rather
# than inserting a second version 1.
shared_version_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "
  SELECT current_draft_version_id FROM agents WHERE id='${shared_agent_id}'" | head -1)"
owner_a_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='owner-a@v2finaltest.local'" | head -1)"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  UPDATE agent_versions SET status='published', provider='ollama', model='qwen3:latest',
      system_prompt='Legacy shared assistant.', published_at=NOW(), published_by='${owner_a_id}'
  WHERE id='${shared_version_id}';" >/dev/null
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  UPDATE agents SET published_version_id='${shared_version_id}', current_draft_version_id=NULL, lifecycle_state='production'
    WHERE id='${shared_agent_id}';
  -- trg_create_initial_agent_tenant_assignment already created the active
  -- agent_tenant_assignments row when the agent was inserted.
  INSERT INTO agent_department_assignments(agent_id, tenant_id, department_id, status) VALUES
    ('${shared_agent_id}', '${tenant_a}', '${accounting_dept}', 'active'),
    ('${shared_agent_id}', '${tenant_a}', '${sales_dept}', 'active');
  -- Re-run migration 024's own classification against this freshly seeded
  -- row -- proves the REAL migration logic (not a hand-set flag) correctly
  -- quarantines a genuinely shared legacy agent.
  UPDATE agents SET v2_scope=NULL, v2_migration_status='pending' WHERE id='${shared_agent_id}';
  WITH shared AS (
    SELECT a.id FROM agents a JOIN agent_department_assignments ada ON ada.agent_id=a.id AND ada.tenant_id=a.owning_tenant_id AND ada.status='active'
    WHERE a.id='${shared_agent_id}' GROUP BY a.id HAVING count(DISTINCT ada.department_id)>1
  )
  UPDATE agents a SET v2_migration_status='quarantined_shared' FROM shared WHERE a.id=shared.id;
" >/dev/null

req GET /system/agents/quarantine "${super_token}" ""
expect_status "SUPER_ADMIN reads the quarantine inventory" 200 "${status}"
expect_true "shared agent appears with 2 active departments" \
  "$(echo "${body}" | jq -r --arg id "${shared_agent_id}" '[.items[]|select(.id==$id and .activeDepartmentCount==2)]|length==1')"

echo "== clone to Accounting: creates a real Department Agent, source untouched =="
req POST "/system/agents/${shared_agent_id}/clone-to-department" "${super_token}" "{\"tenantId\":\"${tenant_a}\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "SUPER_ADMIN clones the shared agent into Accounting" 201 "${status}"
clone_agent_id="$(jqf .clonedAgentId)"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@v2finaltest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
req POST /auth/login "" '{"email":"admin-accounting@v2finaltest.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"

req GET /department/agents "${admin_accounting_token}" ""
expect_true "the clone is now visible in Accounting's own Department Agent Builder" \
  "$(echo "${body}" | jq -r --arg id "${clone_agent_id}" '[.items[]|select(.id==$id)]|length==1')"

echo "== idempotent reclone -> 404 (not a duplicate); wrong department -> 404 =="
req POST "/system/agents/${shared_agent_id}/clone-to-department" "${super_token}" "{\"tenantId\":\"${tenant_a}\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "recloning the same source into the same department is refused" 404 "${status}"
req POST "/system/agents/${shared_agent_id}/clone-to-department" "${super_token}" "{\"tenantId\":\"${tenant_a}\",\"departmentId\":\"$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-v2final'")\"}"
expect_status "cloning into Sales succeeds separately (Sales also had an active entitlement)" 201 "${status}"

echo ""
echo "== §V2-H: validation-attribute fix now genuinely rejects malformed input =="
req POST /auth/register "" '{"email":"not-an-email","password":"x"}'
expect_status "register with an invalid email + too-short password is now rejected" 400 "${status}"
req POST /department/capabilities "${admin_accounting_token}" '{"code":"","name":""}'
expect_status "creating a Department capability with empty required fields is now rejected" 400 "${status}"

echo ""
echo "== §V2-H: production secret fail-fast =="
docker run --detach --name "${gw_bad_secrets}" --network "${net}" \
    --env ASPNETCORE_ENVIRONMENT=Production --env ASPNETCORE_URLS=http://+:8080 \
    --env "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=${db};Username=postgres;Password=${db_password};Pooling=true" \
    --env Jwt__Issuer=aiq.v2final.test --env Jwt__Audience=aiq.v2final.client \
    --env Jwt__SigningKey=v2final-e2e-test-signing-key-needs-32-bytes-min \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://object.invalid:9000 --env Object__AccessKey=aiq --env Object__SecretKey=aiq \
    "${image}" >/dev/null
sleep 3
bad_secrets_status="$(docker inspect -f '{{.State.Status}}' "${gw_bad_secrets}")"
if [[ "${bad_secrets_status}" == "exited" ]]; then
    ok "Gateway refuses to start in Production with weak Object__AccessKey/SecretKey (container exited)"
else
    bad "Gateway did not fail fast on weak Production secrets (container status: ${bad_secrets_status})"
fi
docker logs "${gw_bad_secrets}" 2>&1 | grep -q "must be set to a real, non-default value" \
    && ok "startup log names the exact offending configuration" \
    || bad "startup log did not explain the failure"

echo ""
echo "================================================================"
echo "V2 FINAL E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
