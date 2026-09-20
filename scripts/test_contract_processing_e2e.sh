#!/usr/bin/env bash
# Contract Processing (Luong 01) real HTTP E2E.
#
# Boots an isolated, empty Postgres + freshly built Gateway image (own
# network/volume, never touches the shared dev stack) and drives one
# realistic sample contract through the full lifecycle over real HTTP with
# real JWTs:
#   ADMIN (own department): create -> process (classify/extract/validate/
#     schedule/milestones/alerts) -> HITL action recorded -> review/approve
#     -> handoff/export.
#   USER (same department, assigned Agent + contract capability): can use
#     the permitted Contract business pages but cannot approve/handoff.
#   USER (same department, no assignment): cannot access contract processing
#     at all.
#   ADMIN from another department: cannot see or reach the contract.
#   TENANT_OWNER / SUPER_ADMIN: cannot perform Department Contract
#     operations at all (not Department principals).
#   Confirms handoff never writes a journal/invoice/tax row anywhere in the
#   schema (only a JSON EXPORT_ONLY package).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$"
net="aiq-contract-e2e-net-${run_id}"
pg="aiq-contract-e2e-postgres-${run_id}"
gw="aiq-contract-e2e-gateway-${run_id}"
minio="aiq-contract-e2e-minio-${run_id}"
vol="aiq-contract-e2e-pgdata-${run_id}"
db="aiq_contract_e2e"
db_password="contract_e2e_password"
image="aiq-agentic-gateway:contracttest"
base="http://localhost:18082"
gw_port=18082

pass=0
fail=0
status=""
body=""

cleanup() {
    docker rm -f "${gw}" >/dev/null 2>&1 || true
    docker rm -f "${minio}" >/dev/null 2>&1 || true
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

expect_true() {
    local desc="$1" cond="$2"
    if [[ "${cond}" == "true" ]]; then ok "${desc}"; else bad "${desc} (got: ${cond})"; fi
}

req() {
    local method="$1" path="$2" token="${3:-}" req_payload="${4:-}"
    local args=(-sS -o /tmp/contract_e2e_body.$$ -w '%{http_code}' -X "${method}" "${base}${path}" -H 'Content-Type: application/json')
    [[ -n "${token}" ]] && args+=(-H "Authorization: Bearer ${token}")
    [[ -n "${req_payload}" ]] && args+=(-d "${req_payload}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/contract_e2e_body.$$)"
    rm -f /tmp/contract_e2e_body.$$
}

upload_doc() {
    local path="$1" token="$2" file_path="$3" document_type="$4" referenced_code="${5:-}"
    local args=(-sS -o /tmp/contract_e2e_body.$$ -w '%{http_code}' -X POST "${base}${path}"
        -H "Authorization: Bearer ${token}" -F "file=@${file_path};type=application/pdf" -F "documentType=${document_type}")
    [[ -n "${referenced_code}" ]] && args+=(-F "referencedCode=${referenced_code}")
    status="$(curl "${args[@]}")"
    body="$(cat /tmp/contract_e2e_body.$$)"
    rm -f /tmp/contract_e2e_body.$$
}

jqf() { echo "${body}" | jq -r "$1"; }

echo "== Contract Processing E2E: booting isolated stack =="
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

docker run --detach --name "${minio}" --network "${net}" --network-alias minio \
    --env MINIO_ROOT_USER=contracte2e --env MINIO_ROOT_PASSWORD=contracte2epassword \
    minio/minio server /data >/dev/null
for _ in $(seq 1 60); do
    docker run --rm --network "${net}" --entrypoint /bin/sh minio/mc -c \
        'mc alias set local http://minio:9000 contracte2e contracte2epassword >/dev/null && mc mb --ignore-existing local/contract-uploads >/dev/null' \
        >/dev/null 2>&1 && break
    sleep 1
done
docker run --rm --network "${net}" --entrypoint /bin/sh minio/mc -c \
    'mc alias set local http://minio:9000 contracte2e contracte2epassword >/dev/null && mc stat local/contract-uploads >/dev/null'

docker run --detach --name "${gw}" --network "${net}" --network-alias gateway -p "${gw_port}:8080" \
    --env ASPNETCORE_ENVIRONMENT=Production --env ASPNETCORE_URLS=http://+:8080 \
    --env "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=${db};Username=postgres;Password=${db_password};Pooling=true" \
    --env Jwt__Issuer=aiq.contract.test --env Jwt__Audience=aiq.contract.client \
    --env Jwt__SigningKey=contract-e2e-test-signing-key-needs-32-bytes \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://minio:9000 --env Object__AccessKey=contracte2e --env Object__SecretKey=contracte2epassword \
    --env Object__ForcePathStyle=true --env Object__ContractBucket=contract-uploads \
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

req POST /auth/register "" '{"email":"root@contracttest.local","password":"RootPass123!","displayName":"Root"}'
expect_status "register bootstrap user" 201 "${status}"

docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@contracttest.local' -f /tmp/first-super-admin.sql >/dev/null

req POST /auth/login "" '{"email":"root@contracttest.local","password":"RootPass123!"}'
expect_status "login as super_admin" 200 "${status}"
super_token="$(jqf .token)"

echo "== Setup: SUPER_ADMIN publishes a Contract Agent declaring contract.processing, assigns to Tenant A =="
req POST /system/tenants/onboard "${super_token}" '{"code":"tenant-a-contracttest","name":"Tenant A","ownerEmail":"owner-a@contracttest.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner A"}'
expect_status "onboard Tenant A" 201 "${status}"
tenant_a="$(jqf .tenant.id)"

req GET /system/capabilities "${super_token}" ""
expect_status "super_admin lists capability catalog" 200 "${status}"
contract_processing_id="$(echo "${body}" | jq -r '.items[] | select(.code=="contract.processing") | .id')"
[[ -n "${contract_processing_id}" && "${contract_processing_id}" != "null" ]] && ok "contract.processing exists in the seeded catalog" || bad "contract.processing missing from catalog"

req POST /system/agents "${super_token}" '{"slug":"contract-agent-e2e","name":"Contract Agent","actionTier":"suggest"}'
expect_status "create Contract Agent" 201 "${status}"
agent_id="$(jqf .id)"

req GET "/system/agents/${agent_id}/versions" "${super_token}" ""
draft_version_id="$(jqf '.items[0].id')"

req PATCH "/system/agents/${agent_id}/versions/${draft_version_id}" "${super_token}" "{\"capabilityIds\":[\"${contract_processing_id}\"]}"
expect_status "assign contract.processing to the draft" 200 "${status}"

req POST "/system/agents/${agent_id}/versions/${draft_version_id}/publish" "${super_token}" ""
expect_status "publish version" 200 "${status}"

req PUT "/system/agents/${agent_id}/tenants/${tenant_a}" "${super_token}" ""
expect_status "assign Contract Agent to Tenant A" 204 "${status}"

echo "== Setup: seed Accounting + Sales departments =="
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO departments(tenant_id,code,name,status)
  SELECT id,'sales-contracttest','Sales','active' FROM tenants WHERE id='${tenant_a}';
" >/dev/null
accounting_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND is_default")"
sales_dept="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_a}' AND code='sales-contracttest'")"

req POST /auth/login "" '{"email":"owner-a@contracttest.local","password":"OwnerPass123!"}'
owner_a_token="$(jqf .token)"

req PUT "/tenant/departments/${accounting_dept}/agents/${agent_id}" "${owner_a_token}" ""
expect_status "owner assigns Contract Agent to Accounting dept" 204 "${status}"

req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-accounting@contracttest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Accounting\",\"departmentId\":\"${accounting_dept}\"}"
expect_status "owner creates Admin Accounting" 201 "${status}"
req POST /tenant/admins "${owner_a_token}" "{\"email\":\"admin-sales@contracttest.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin Sales\",\"departmentId\":\"${sales_dept}\"}"
expect_status "owner creates Admin Sales" 201 "${status}"

req POST /auth/login "" '{"email":"admin-accounting@contracttest.local","password":"AdminPass123!"}'
admin_accounting_token="$(jqf .token)"
req POST /auth/login "" '{"email":"admin-sales@contracttest.local","password":"AdminPass123!"}'
admin_sales_token="$(jqf .token)"

req PUT "/department/capabilities/${contract_processing_id}/binding" "${admin_accounting_token}" "{\"agentId\":\"${agent_id}\"}"
expect_status "Admin Accounting binds contract.processing -> its own Contract Agent" 204 "${status}"

echo "== Setup: HITL gate on the Contract Agent for contract.handoff (so /process's review gate has something to evaluate) =="
req POST "/agents/${agent_id}/gates" "${admin_accounting_token}" '{"name":"contract-handoff-review","triggerRule":{"tool":"contract.handoff"},"approverRole":"tenant_admin"}'
expect_status "Admin Accounting creates a HITL gate for contract.handoff" 201 "${status}"

echo "== Setup: USER with assignment (assigned-user), USER without assignment (unassigned-user) =="
req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user@contracttest.local","password":"UserPass123!","displayName":"Assigned User"}'
expect_status "Admin Accounting creates Assigned User" 201 "${status}"
assigned_user_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='assigned-user@contracttest.local'")"

req POST /tenant/users "${admin_accounting_token}" '{"email":"unassigned-user@contracttest.local","password":"UserPass123!","displayName":"Unassigned User"}'
expect_status "Admin Accounting creates Unassigned User" 201 "${status}"

req POST /tenant/users "${admin_accounting_token}" '{"email":"assigned-user2@contracttest.local","password":"UserPass123!","displayName":"Assigned User 2"}'
expect_status "Admin Accounting creates a second Assigned User (same department)" 201 "${status}"
assigned_user2_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM users WHERE email='assigned-user2@contracttest.local'")"

req PUT "/tenant/agents/${agent_id}/users/${assigned_user_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Contract Agent to Assigned User" 204 "${status}"
req PUT "/tenant/agents/${agent_id}/users/${assigned_user2_id}" "${admin_accounting_token}" ""
expect_status "Admin Accounting assigns Contract Agent to Assigned User 2" 204 "${status}"

req POST /auth/login "" '{"email":"assigned-user@contracttest.local","password":"UserPass123!"}'
assigned_user_token="$(jqf .token)"
req POST /auth/login "" '{"email":"assigned-user2@contracttest.local","password":"UserPass123!"}'
assigned_user2_token="$(jqf .token)"
req POST /auth/login "" '{"email":"unassigned-user@contracttest.local","password":"UserPass123!"}'
unassigned_user_token="$(jqf .token)"

echo "== §6 baseline: no journal/invoice/tax rows exist before any contract action =="
voucher_count_before="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT count(*) FROM vouchers")"
journal_count_before="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT count(*) FROM voucher_journal_suggestions")"
expect_true "no vouchers exist before contract processing" "$([[ "${voucher_count_before}" == "0" ]] && echo true || echo false)"
expect_true "no journal suggestions exist before contract processing" "$([[ "${journal_count_before}" == "0" ]] && echo true || echo false)"

echo ""
echo "== §1 ADMIN same department: create -> process -> HITL -> review/approve -> handoff/export =="
req POST /contracts "${admin_accounting_token}" '{
  "title":"Hop dong cung cap dich vu IT - Cong ty ABC",
  "partyA":"Cong ty TNHH ABC",
  "partyB":"Cong ty TNHH Doi tac XYZ",
  "signedDate":"2026-01-10",
  "effectiveDate":"2026-01-15",
  "endDate":"2027-01-14",
  "totalValue":110000000,
  "currency":"VND",
  "rawText":"Hop dong cung cap dich vu phat trien phan mem giua Cong ty TNHH ABC va Cong ty TNHH Doi tac XYZ.",
  "tags":["it-services","2026"]
}'
expect_status "ADMIN Accounting creates the sample contract" 201 "${status}"
contract_id="$(jqf .contract.id)"

req POST "/contracts/${contract_id}/process" "${admin_accounting_token}" '{
  "documentType":"service_contract",
  "contractNumber":"HD-2026-001",
  "subtotal":100000000,
  "vatRate":10,
  "totalValue":110000000,
  "retentionPercent":5,
  "penaltyPercent":5,
  "parties":[
    {"partyRole":"buyer","name":"Cong ty TNHH ABC","taxCode":"0101234567"},
    {"partyRole":"seller","name":"Cong ty TNHH Doi tac XYZ","taxCode":"0107654321"}
  ],
  "paymentTerms":[
    {"ordinal":1,"description":"Dot 1 - tam ung","percentage":30,"dueDate":"2026-01-20"},
    {"ordinal":2,"description":"Dot 2 - nghiem thu","percentage":70,"dueDate":"2026-06-30"}
  ],
  "milestones":[
    {"ordinal":1,"name":"Ban giao giai doan 1","dueDate":"2026-04-01","reminderDays":7},
    {"ordinal":2,"name":"Nghiem thu cuoi cung","dueDate":"2026-06-15","reminderDays":7}
  ]
}'
expect_status "ADMIN processes: classify/extract/validate/schedule/milestones/alerts" 200 "${status}"
expect_true "workflow reaches WAITING_REVIEW (only info-level alerts)" "$([[ "$(jqf .workflowState)" == "WAITING_REVIEW" ]] && echo true || echo false)"
# CT.NEW: both counterparties are brand-new tax codes -> 2 informational
# NEW_COUNTERPARTY alerts. Informational never blocks WAITING_REVIEW.
expect_true "2 informational CT.NEW alerts (both counterparties are new)" "$([[ "$(jqf .alertCount)" == "2" ]] && echo true || echo false)"
expect_true "payment schedule generated (2 lines)" "$([[ "$(jqf .paymentCount)" == "2" ]] && echo true || echo false)"
expect_true "milestones generated (2)" "$([[ "$(jqf .milestoneCount)" == "2" ]] && echo true || echo false)"
expect_true "HITL action recorded for review gate" "$([[ "$(jqf .hitlActionId)" != "null" ]] && echo true || echo false)"

req GET "/contracts/${contract_id}" "${admin_accounting_token}" ""
expect_status "ADMIN reads full aggregate" 200 "${status}"
expect_true "aggregate carries 2 parties" "$([[ "$(jqf '.parties | length')" == "2" ]] && echo true || echo false)"
expect_true "aggregate carries 2 payment schedule lines" "$([[ "$(jqf '.payments | length')" == "2" ]] && echo true || echo false)"
expect_true "aggregate carries 2 milestones" "$([[ "$(jqf '.milestones | length')" == "2" ]] && echo true || echo false)"
expect_true "aggregate carries 2 info-severity alerts, none blocking" \
  "$(echo "${body}" | jq -r '(.alerts | length == 2) and ([.alerts[].severity] | all(. == "info"))')"
expect_true "CT.NEW proposed opening a counterparty code for both new parties" "$([[ "$(jqf '.counterpartyProposals | length')" == "2" ]] && echo true || echo false)"
expect_true "workflow history recorded the transitions" "$([[ "$(jqf '.history | length')" -gt "0" ]] && echo true || echo false)"

echo ""
echo "== §CT.NEW is not re-triggered for an already-known counterparty =="
req POST /contracts "${admin_accounting_token}" '{"title":"Hop dong dich vu lan 2 - Cong ty ABC","partyA":"Cong ty TNHH ABC","partyB":"Cong ty TNHH Doi tac XYZ","totalValue":50000000,"currency":"VND"}'
expect_status "ADMIN creates a second contract with the same counterparties" 201 "${status}"
second_contract_id="$(jqf .contract.id)"
req POST "/contracts/${second_contract_id}/process" "${admin_accounting_token}" '{
  "subtotal":50000000,"vatRate":10,"totalValue":55000000,
  "parties":[
    {"partyRole":"buyer","name":"Cong ty TNHH ABC","taxCode":"0101234567"},
    {"partyRole":"seller","name":"Cong ty TNHH Doi tac XYZ","taxCode":"0107654321"}
  ],
  "paymentTerms":[{"ordinal":1,"description":"Mot lan","percentage":100,"dueDate":"2026-02-01"}]
}'
expect_status "process the second contract" 200 "${status}"
expect_true "no CT.NEW alerts for already-known counterparties" "$([[ "$(jqf .alertCount)" == "0" ]] && echo true || echo false)"

echo ""
echo "== §CT.REF: contract references an appendix that was never received =="
req POST /contracts "${admin_accounting_token}" '{
  "title":"Hop dong mua ban co phu luc",
  "partyA":"Cong ty TNHH ABC","partyB":"Cong ty TNHH Doi tac XYZ",
  "totalValue":110000000,"currency":"VND",
  "clauses":[{"ordinal":1,"heading":"Dieu 3 - Gia tri hop dong","body":"Gia theo phụ lục bảng giá đính kèm, chưa nhận được phụ lục này.","hasDeadline":false,"hasPenalty":false}]
}'
expect_status "ADMIN creates a contract whose clause references an appendix" 201 "${status}"
appendix_contract_id="$(jqf .contract.id)"
req POST "/contracts/${appendix_contract_id}/process" "${admin_accounting_token}" '{
  "subtotal":100000000,"vatRate":10,"totalValue":110000000,
  "parties":[{"partyRole":"buyer","name":"Cong ty TNHH ABC","taxCode":"0101234567"},{"partyRole":"seller","name":"Cong ty TNHH Doi tac XYZ","taxCode":"0107654321"}],
  "paymentTerms":[{"ordinal":1,"description":"Mot lan","percentage":100,"dueDate":"2026-02-01"}]
}'
expect_status "process the appendix-referencing contract" 200 "${status}"
req GET "/contracts/${appendix_contract_id}" "${admin_accounting_token}" ""
expect_true "MISSING_APPENDIX warning recorded (clause references an unreceived appendix)" \
  "$(echo "${body}" | jq -r '[.alerts[] | select(.code=="MISSING_APPENDIX")] | length == 1')"
expect_true "MISSING_APPENDIX is a warning, not a block — still reaches WAITING_REVIEW" \
  "$([[ "$(jqf .contract.workflowState)" == "WAITING_REVIEW" ]] && echo true || echo false)"

echo ""
echo "== §Conditional date semantics: trigger_event/offset_days/duration_months/expected_date =="
req POST /contracts "${admin_accounting_token}" '{"title":"Hop dong co dieu kien ngay","partyA":"Cong ty TNHH ABC","partyB":"Cong ty TNHH Doi tac XYZ","totalValue":100000000,"currency":"VND"}'
expect_status "ADMIN creates a contract for conditional-date testing" 201 "${status}"
conditional_contract_id="$(jqf .contract.id)"
req POST "/contracts/${conditional_contract_id}/process" "${admin_accounting_token}" '{
  "subtotal":100000000,"vatRate":10,"totalValue":110000000,
  "parties":[{"partyRole":"buyer","name":"Cong ty TNHH ABC","taxCode":"0101234567"},{"partyRole":"seller","name":"Cong ty TNHH Doi tac XYZ","taxCode":"0107654321"}],
  "paymentTerms":[
    {"ordinal":1,"description":"Dot 1 - ky hop dong","percentage":30,"dueDate":"2026-03-01"},
    {"ordinal":2,"description":"Dot 2 - giao hang","percentage":70,"dueDate":"2026-09-01","triggerEvent":"Ky Bien ban giao nhan + hoa don GTGT hop le","offsetDays":10,"expectedDate":"2026-10-05"}
  ],
  "milestones":[
    {"ordinal":1,"name":"Ket thuc bao hanh","dueDate":"2027-01-01","triggerEvent":"Ky Bien ban giao nhan","durationMonths":24,"expectedDate":"2028-09-25"}
  ]
}'
expect_status "process the conditional-date contract" 200 "${status}"
req GET "/contracts/${conditional_contract_id}" "${admin_accounting_token}" ""
expect_true "Payment 1 (no trigger) keeps its confirmed due_date" \
  "$(echo "${body}" | jq -r '[.payments[]|select(.ordinal==1)][0] | .dueDate=="2026-03-01T00:00:00" and .triggerEvent==null')"
expect_true "Payment 2's due_date is forced NULL server-side even though the caller sent one alongside triggerEvent" \
  "$(echo "${body}" | jq -r '[.payments[]|select(.ordinal==2)][0] | .dueDate==null and .expectedDate=="2026-10-05T00:00:00" and .offsetDays==10')"
expect_true "Milestone's due_date is forced NULL server-side too, expected_date/duration_months preserved" \
  "$(echo "${body}" | jq -r '.milestones[0] | .dueDate==null and .durationMonths==24 and .expectedDate=="2028-09-25T00:00:00"')"
expect_true "PAYMENT_AWAITING_TRIGGER alert recorded" \
  "$(echo "${body}" | jq -r '[.alerts[]|select(.code=="PAYMENT_AWAITING_TRIGGER")]|length==1')"
expect_true "MILESTONE_AWAITING_TRIGGER alert recorded" \
  "$(echo "${body}" | jq -r '[.alerts[]|select(.code=="MILESTONE_AWAITING_TRIGGER")]|length==1')"
expect_true "conditional-date alerts are informational, never block WAITING_REVIEW" \
  "$([[ "$(jqf .contract.workflowState)" == "WAITING_REVIEW" ]] && echo true || echo false)"

req POST "/contracts/${contract_id}/review" "${admin_accounting_token}" '{"approve":true,"comment":"Da kiem tra dieu khoan va lich thanh toan, phe duyet."}'
expect_status "ADMIN reviews/approves (human-in-the-loop approval)" 200 "${status}"
expect_true "workflow state is APPROVED" "$([[ "$(jqf .workflowState)" == "APPROVED" ]] && echo true || echo false)"

req POST "/contracts/${contract_id}/handoff" "${admin_accounting_token}" ""
expect_status "ADMIN generates handoff/export package" 200 "${status}"
expect_true "handoff payload is explicitly export-only" "$(echo "${body}" | jq -r '(.Payload // .payload) | fromjson | .accountingBoundary=="EXPORT_ONLY"')"
expect_true "handoff contains contract code, schedule rows and counterparty proposals" \
  "$(echo "${body}" | jq -r '(.Payload // .payload) | fromjson | (.contractCode|length)>0 and (.paymentSchedules|length)==2 and (.counterpartyProposals|length)==2')"

req GET "/contracts/${contract_id}" "${admin_accounting_token}" ""
expect_true "contract now HANDED_OFF" "$([[ "$(jqf .contract.workflowState)" == "HANDED_OFF" ]] && echo true || echo false)"
expect_true "handoff package recorded in aggregate" "$([[ "$(jqf '.handoffs | length')" == "1" ]] && echo true || echo false)"

echo ""
echo "== §2 USER same department, assigned Agent + contract capability =="
req GET /contracts "${assigned_user_token}" ""
expect_status "Assigned User can list Contract business pages" 200 "${status}"
expect_true "Assigned User's list contains the sample contract" "$(echo "${body}" | jq -r --arg id "${contract_id}" '[.items[]|select(.id==$id)]|length==1')"

req GET "/contracts/${contract_id}" "${assigned_user_token}" ""
expect_status "Assigned User can read the contract" 200 "${status}"

req POST "/contracts/${contract_id}/review" "${assigned_user_token}" '{"approve":true,"comment":"attempt"}'
expect_status "Assigned User cannot review/approve (ADMIN-only)" 403 "${status}"

req POST "/contracts/${contract_id}/handoff" "${assigned_user_token}" ""
expect_status "Assigned User cannot generate handoff (ADMIN-only)" 403 "${status}"

req PATCH "/contracts/${contract_id}" "${assigned_user_token}" '{"title":"hacked"}'
expect_status "Assigned User cannot update an ADMIN-created contract (not the creator)" 403 "${status}"

req DELETE "/contracts/${contract_id}" "${assigned_user_token}" ""
expect_status "Assigned User cannot delete the contract (ADMIN-only)" 403 "${status}"

echo ""
echo "== §2b USER create/edit-own authorization =="
req POST /contracts "${assigned_user_token}" '{"title":"Hop dong do User tu tao","partyA":"Cong ty TNHH ABC","partyB":"Cong ty TNHH Doi tac XYZ","totalValue":20000000,"currency":"VND"}'
expect_status "Assigned User can create (intake) their own contract" 201 "${status}"
user_contract_id="$(jqf .contract.id)"
expect_true "created_by is the authenticated USER, never client-supplied" "$([[ "$(jqf .contract.userId)" == "${assigned_user_id}" ]] && echo true || echo false)"

req PATCH "/contracts/${user_contract_id}" "${assigned_user_token}" '{"title":"Hop dong do User tu tao (da sua)"}'
expect_status "USER can edit their own contract while still in an editable workflow state" 200 "${status}"
expect_true "edit was actually applied" "$([[ "$(jqf .title)" == "Hop dong do User tu tao (da sua)" ]] && echo true || echo false)"

req PATCH "/contracts/${user_contract_id}" "${assigned_user2_token}" '{"title":"hacked-by-another-user"}'
expect_status "A different USER (same department) cannot edit User's contract (not the creator)" 403 "${status}"

req PATCH "/contracts/${user_contract_id}" "${unassigned_user_token}" '{"title":"hacked-no-capability"}'
expect_status "USER without an active capability grant cannot edit any contract" 403 "${status}"

req POST /contracts "${unassigned_user_token}" '{"title":"Should not be created","partyA":"A","partyB":"B","totalValue":1,"currency":"VND"}'
expect_status "USER without an active capability grant cannot create a contract" 403 "${status}"

contract_pdf="/tmp/contract-e2e-${run_id}.pdf"
printf '%s\n' '%PDF-1.4' '1 0 obj << /Type /Catalog >> endobj' '%%EOF' > "${contract_pdf}"
upload_doc "/contracts/${user_contract_id}/documents" "${assigned_user_token}" "${contract_pdf}" contract
expect_status "USER can upload a supporting Contract document for their own editable record" 201 "${status}"
uploaded_document_id="$(jqf .id)"
upload_doc "/contracts/${user_contract_id}/documents" "${assigned_user_token}" "${contract_pdf}" contract
expect_status "retrying the same upload is idempotent" 201 "${status}"
expect_true "duplicate upload resolves to the existing document" "$([[ "$(jqf .id)" == "${uploaded_document_id}" ]] && echo true || echo false)"
req GET "/contracts/${user_contract_id}/documents" "${assigned_user_token}" ""
expect_status "USER can list supporting documents in the permitted Department" 200 "${status}"
expect_true "idempotent upload leaves one document" "$([[ "$(jqf '.items | length')" == "1" ]] && echo true || echo false)"
rm -f "${contract_pdf}"

req POST "/contracts/${user_contract_id}/process" "${assigned_user_token}" '{
  "subtotal":20000000,"vatRate":10,"totalValue":22000000,
  "parties":[{"partyRole":"buyer","name":"Cong ty TNHH ABC","taxCode":"0101234567"},{"partyRole":"seller","name":"Cong ty TNHH Doi tac XYZ","taxCode":"0107654321"}],
  "paymentTerms":[{"ordinal":1,"description":"Mot lan","percentage":100,"dueDate":"2026-03-01"}]
}'
expect_status "USER processes their own contract to WAITING_REVIEW" 200 "${status}"
expect_true "own contract reaches WAITING_REVIEW" "$([[ "$(jqf .workflowState)" == "WAITING_REVIEW" ]] && echo true || echo false)"

req POST "/contracts/${user_contract_id}/process" "${assigned_user_token}" '{"subtotal":20000000,"vatRate":10,"totalValue":22000000,"parties":[{"partyRole":"buyer","name":"Cong ty TNHH ABC","taxCode":"0101234567"},{"partyRole":"seller","name":"Cong ty TNHH Doi tac XYZ","taxCode":"0107654321"}],"paymentTerms":[{"ordinal":1,"description":"Mot lan","percentage":100,"dueDate":"2026-03-01"}]}'
expect_status "processing retry is idempotent" 200 "${status}"
req GET "/contracts/${user_contract_id}" "${assigned_user_token}" ""
expect_true "retry does not duplicate schedule rows" "$(echo "${body}" | jq -r '(.payments|length)==1')"
req GET "/contracts/${user_contract_id}/checklist" "${assigned_user_token}" ""
expect_true "retry does not duplicate generated checklist rows" "$(echo "${body}" | jq -r '(.items|map(.description)|unique|length)==(.items|length)')"

req POST "/contracts/${user_contract_id}/review" "${admin_accounting_token}" '{"approve":true,"comment":"reviewed"}'
expect_status "ADMIN approves the USER-created contract" 200 "${status}"

req POST "/contracts/${user_contract_id}/process" "${assigned_user_token}" '{"subtotal":20000000,"vatRate":10,"totalValue":22000000,"paymentTerms":[{"ordinal":1,"description":"Mot lan","percentage":100,"dueDate":"2026-03-01"}]}'
expect_status "finalized contract cannot be reprocessed" 409 "${status}"

req PATCH "/contracts/${user_contract_id}" "${assigned_user_token}" '{"title":"attempt-after-approval"}'
expect_status "USER can no longer edit their own contract once ADMIN has approved it" 403 "${status}"

echo ""
echo "== §3 USER same department, no assignment =="
req GET /contracts "${unassigned_user_token}" ""
expect_status "Unassigned User cannot access contract processing at all" 403 "${status}"
req GET "/contracts/${contract_id}" "${unassigned_user_token}" ""
expect_status "Unassigned User cannot read the contract either" 403 "${status}"

echo ""
echo "== §4 ADMIN from another department cannot see or access the contract =="
req GET "/contracts/${contract_id}" "${admin_sales_token}" ""
expect_status "Admin Sales cannot fetch Accounting's contract" 404 "${status}"
req GET /contracts "${admin_sales_token}" ""
expect_status "Admin Sales's own contract list still works (own department)" 200 "${status}"
expect_true "Admin Sales's list does NOT contain Accounting's contract" \
  "$(echo "${body}" | jq -r --arg id "${contract_id}" '[.items[]|select(.id==$id)]|length==0')"

echo ""
echo "== §5 TENANT_OWNER and SUPER_ADMIN cannot perform Department Contract operations =="
req GET /contracts "${owner_a_token}" ""
expect_status "TENANT_OWNER cannot list Department Contracts (not a Department principal)" 403 "${status}"
req GET "/contracts/${contract_id}" "${owner_a_token}" ""
expect_status "TENANT_OWNER cannot read the contract" 403 "${status}"
req GET /contracts "${super_token}" ""
expect_status "SUPER_ADMIN cannot list Department Contracts (not a Department principal)" 403 "${status}"
req GET "/contracts/${contract_id}" "${super_token}" ""
expect_status "SUPER_ADMIN cannot read the contract" 403 "${status}"

echo ""
echo "== §6 confirm no journal/invoice/tax side effects anywhere in the schema =="
voucher_count_after="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT count(*) FROM vouchers")"
journal_count_after="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT count(*) FROM voucher_journal_suggestions")"
expect_true "no vouchers were created by the full contract lifecycle" "$([[ "${voucher_count_after}" == "0" ]] && echo true || echo false)"
expect_true "no journal suggestions were created by the full contract lifecycle" "$([[ "${journal_count_after}" == "0" ]] && echo true || echo false)"
invoice_table_count="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT count(*) FROM information_schema.tables WHERE table_name ILIKE '%invoice%' OR table_name ILIKE '%tax_declaration%'")"
expect_true "no invoice/tax_declaration table exists for a contract flow to have written to" "$([[ "${invoice_table_count}" == "0" ]] && echo true || echo false)"

echo ""
echo "================================================================"
echo "CONTRACT PROCESSING E2E RESULT: ${pass} passed, ${fail} failed"
echo "================================================================"
[[ "${fail}" -eq 0 ]]
