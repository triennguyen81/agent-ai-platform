#!/usr/bin/env bash
# AccAgent business sample data — curl walkthrough.
#
# Boots an isolated, empty Postgres + freshly built Gateway image (own
# network/volume/ports — never touches a shared/dev/production stack),
# onboards ONE fictional demo tenant ("AccAgent Demo Co."), loads seed.sql,
# then walks through one representative case per Luồng so you can see the
# real request/response shapes. All data is fictional; no production IDs
# are used or created. Run the whole thing top to bottom, or copy individual
# curl blocks into your own shell once GW/token variables are set.
#
# Usage: bash curl_examples.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
fixtures_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
run_id="$$"
net="aiq-sample-net-${run_id}"
pg="aiq-sample-pg-${run_id}"
gw="aiq-sample-gw-${run_id}"
db="aiq_sample"
db_pw="samplepass123"
gw_port=18080
GW="http://localhost:${gw_port}"

cleanup() {
  echo ""
  echo "Cleaning up isolated stack (${pg}, ${gw}, ${net})..."
  docker rm -f "${gw}" "${pg}" >/dev/null 2>&1 || true
  docker network rm "${net}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

req() {
  local method="$1" path="$2" token="$3" body="${4:-}"
  if [[ -n "${body}" ]]; then
    response=$(curl -s -w "\n%{http_code}" -X "${method}" "${GW}${path}" \
      -H "Content-Type: application/json" ${token:+-H "Authorization: Bearer ${token}"} -d "${body}")
  else
    response=$(curl -s -w "\n%{http_code}" -X "${method}" "${GW}${path}" \
      ${token:+-H "Authorization: Bearer ${token}"})
  fi
  status="$(echo "${response}" | tail -1)"
  body_out="$(echo "${response}" | sed '$d')"
}
jqf() { echo "${body_out}" | jq -r "$1"; }

echo "== 1. Boot an isolated Postgres + Gateway (fictional demo data only) =="
docker build -t aiq-agentic-gateway:sample-pack -f "${repo_root}/gateway/src/Aiq.Gateway/Dockerfile" "${repo_root}/gateway" >/dev/null
docker network create "${net}" >/dev/null
docker run --detach --name "${pg}" --network "${net}" --network-alias postgres \
  -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD="${db_pw}" -e POSTGRES_DB="${db}" \
  --volume "${repo_root}/infra/postgres/init:/docker-entrypoint-initdb.d:ro" \
  pgvector/pgvector:pg16 -c max_connections=200 >/dev/null
for _ in $(seq 1 90); do docker logs "${pg}" 2>&1 | grep -q "PostgreSQL init process complete" && break; sleep 1; done
docker exec "${pg}" pg_isready -U postgres -d "${db}" >/dev/null

docker run --detach --name "${gw}" --network "${net}" --network-alias gateway -p "${gw_port}:8080" \
  -e ASPNETCORE_ENVIRONMENT=Production -e ASPNETCORE_URLS=http://+:8080 \
  -e "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=${db};Username=postgres;Password=${db_pw};Pooling=true" \
  -e Jwt__Issuer=aiq.sample.local -e Jwt__Audience=aiq.sample.client -e Jwt__SigningKey=sample-pack-signing-key-32-bytes-long \
  -e Agent__BaseUrl=http://agent.invalid:8000 -e Object__ServiceUrl=http://object.invalid:9000 -e Object__AccessKey=test -e Object__SecretKey=test-password \
  aiq-agentic-gateway:sample-pack >/dev/null
for _ in $(seq 1 60); do curl -sf --max-time 2 "${GW}/healthz/ready" >/dev/null 2>&1 && break; sleep 1; done
curl -sf "${GW}/healthz/ready" >/dev/null
echo "Gateway ready at ${GW}"

echo ""
echo "== 2. Bootstrap: first super_admin, then onboard the fictional demo tenant =="
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -c "
  INSERT INTO tenants(code,name,status) VALUES ('default','Default','active');
  INSERT INTO departments(tenant_id,code,name,status,is_default)
  SELECT id,'default','Default','active',true FROM tenants WHERE code='default';
" >/dev/null
req POST /auth/register "" '{"email":"root@sample.local","password":"RootPass123!","displayName":"Root"}'
docker cp "${repo_root}/infra/postgres/bootstrap/first-super-admin.sql" "${pg}:/tmp/first-super-admin.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" -v email='root@sample.local' -f /tmp/first-super-admin.sql >/dev/null
req POST /auth/login "" '{"email":"root@sample.local","password":"RootPass123!"}'
super_token="$(jqf .token)"

req POST /system/tenants/onboard "${super_token}" '{"code":"accagent-demo","name":"AccAgent Demo Co.","ownerEmail":"owner@sample.local","ownerPassword":"OwnerPass123!","ownerDisplayName":"Owner Demo"}'
tenant_id="$(jqf .tenant.id)"
owner_user_id="$(jqf .ownerUserId)"
department_id="$(docker exec "${pg}" psql -At -U postgres -d "${db}" -c "SELECT id FROM departments WHERE tenant_id='${tenant_id}' AND is_default")"
echo "tenant_id=${tenant_id}"
echo "department_id=${department_id}"

echo ""
echo "== 3. Wire up a Business Agent with voucher.processing + contract.processing (needed for /vouchers and /contracts/{id}/process) =="
req GET /system/capabilities "${super_token}" ""
voucher_cap="$(echo "${body_out}" | jq -r '.items[] | select(.code=="voucher.processing") | .id')"
contract_cap="$(echo "${body_out}" | jq -r '.items[] | select(.code=="contract.processing") | .id')"
req POST /system/agents "${super_token}" '{"slug":"sample-pack-agent","name":"Sample Pack Agent","actionTier":"suggest"}'
agent_id="$(jqf .id)"
req GET "/system/agents/${agent_id}/versions" "${super_token}" ""
draft_version_id="$(jqf '.items[0].id')"
req PATCH "/system/agents/${agent_id}/versions/${draft_version_id}" "${super_token}" "{\"capabilityIds\":[\"${voucher_cap}\",\"${contract_cap}\"]}"
req POST "/system/agents/${agent_id}/versions/${draft_version_id}/publish" "${super_token}" ""
req PUT "/system/agents/${agent_id}/tenants/${tenant_id}" "${super_token}" ""

req POST /auth/login "" '{"email":"owner@sample.local","password":"OwnerPass123!"}'
owner_token="$(jqf .token)"
req PUT "/tenant/departments/${department_id}/agents/${agent_id}" "${owner_token}" ""

echo ""
echo "== 4. Create 4 admin accounts (Luồng 03's 3-stage approval needs 3 distinct non-creator actors) =="
for n in 1 2 3 4; do
  req POST /tenant/admins "${owner_token}" "{\"email\":\"admin${n}@sample.local\",\"password\":\"AdminPass123!\",\"displayName\":\"Admin ${n}\",\"departmentId\":\"${department_id}\"}"
  req POST /auth/login "" "{\"email\":\"admin${n}@sample.local\",\"password\":\"AdminPass123!\"}"
  eval "admin${n}_token=\"$(jqf .token)\""
done
req PUT "/department/capabilities/${voucher_cap}/binding" "${admin1_token}" "{\"agentId\":\"${agent_id}\"}"
req PUT "/department/capabilities/${contract_cap}/binding" "${admin1_token}" "{\"agentId\":\"${agent_id}\"}"

echo ""
echo "== 5. Load master data + supporting AR fixtures (seed.sql) =="
docker cp "${fixtures_dir}/seed.sql" "${pg}:/tmp/seed.sql"
docker exec "${pg}" psql -v ON_ERROR_STOP=1 -U postgres -d "${db}" \
  -v tenant_id="'${tenant_id}'" -v department_id="'${department_id}'" -v user_id="'${owner_user_id}'" \
  -f /tmp/seed.sql

echo ""
echo "== 6. Luồng 01 — process a contract (HD-002, missing appendix) =="
echo "   (deliberately NOT HD-001's own customer here — HD-001 is pre-seeded by seed.sql with its OWN"
echo "    contract+installment for CUS-001's tax code already; creating a SECOND contract for the SAME"
echo "    tax code would make step 7's installment-matching heuristic ambiguous between the two contracts)"
req POST /contracts "${admin1_token}" '{"title":"Hợp đồng mua bán vật tư 210/2026/HĐMB","partyA":"AccAgent Demo Co.","partyB":"Công ty TNHH Thương mại Hải Đăng","signedDate":"2026-08-20","effectiveDate":"2026-08-20","totalValue":450000000,"currency":"VND","tags":["luong01","sample","missing_appendix"],"clauses":[{"ordinal":1,"heading":"Điều 1. Đối tượng hợp đồng","body":"Bên B cung cấp vật tư theo bảng giá quy định tại phụ lục đính kèm hợp đồng này."},{"ordinal":2,"heading":"Điều 2. Thanh toán","body":"Thanh toán trong vòng 15 ngày kể từ ngày nhận hàng và hoá đơn hợp lệ."}]}'
hd002_id="$(jqf .contract.id)"
req POST "/contracts/${hd002_id}/process" "${admin1_token}" '{
  "documentType":"purchase_contract","contractNumber":"210/2026/HĐMB-DEMO","subtotal":409090909,"vatRate":10,"totalValue":450000000,
  "parties":[{"partyRole":"seller","name":"AccAgent Demo Co.","taxCode":"0100000999"},{"partyRole":"buyer","name":"Công ty TNHH Thương mại Hải Đăng","taxCode":"0302345678"}],
  "paymentTerms":[{"ordinal":1,"description":"Thanh toán sau nhận hàng","percentage":100,"dueDate":"2026-09-05"}]
}'
echo "HD-002: HTTP ${status}, workflowState=$(jqf .workflowState) alertCount=$(jqf .alertCount) (expect alertCount=1, MISSING_APPENDIX)"
if [[ "${status}" != "200" ]]; then echo "  (process response: ${body_out})"; fi

echo ""
echo "== 7. Luồng 02 — AR-001 full payment (uses seed.sql's pre-loaded HD-001, not the one just created above) =="
req POST /vouchers "${admin2_token}" '{"voucherType":"receipt","documentNumber":"PT-2026-0901","buyerMst":"0301234567","totalAmount":594000000,"currency":"VND","paymentMethod":"CK"}'
ar001_id="$(jqf .voucher.id)"
req POST "/vouchers/${ar001_id}/receipt-match/propose" "${admin2_token}" ""
echo "propose outcome: $(jqf .outcome) matched=$(jqf .matchedAmount)"
req PATCH "/vouchers/${ar001_id}/apply-extraction" "${admin2_token}" '{"voucherType":"receipt","documentNumber":"PT-2026-0901","buyerMst":"0301234567","totalAmount":594000000,"currency":"VND","paymentMethod":"CK","extractedFields":{"ar_context":{"customer":{"Id":"30000000-0000-4000-8000-000000000001"}},"receipt_matching":{"receipt_amount":594000000,"applications":[{"document_number":"INV-BM-001","applied":594000000}]}}}'
req POST "/vouchers/${ar001_id}/apply-receipt" "${admin1_token}" ""
echo "apply-receipt status: $(jqf .status), ledger rows inserted: $(jqf .ledger_rows_inserted)"
req GET "/contracts/60000000-0000-4000-8000-000000000001" "${admin1_token}" ""
echo "HD-001 progressPercent after settlement: $(jqf .contract.progressPercent)% (expect 30)"

echo ""
echo "== 8. Luồng 03 — EXP-001 normal claim with advance =="
req POST /vouchers "${admin2_token}" '{"voucherType":"expense_claim","documentNumber":"EC-2026-1001"}'
exp001_id="$(jqf .voucher.id)"
req PATCH "/vouchers/${exp001_id}/apply-extraction" "${admin2_token}" '{"voucherType":"expense_claim","documentNumber":"EC-2026-1001","extractedFields":{"employee_code":"NV-001","total_claim":3500000,"advance_taken":3000000,"currency":"VND","items":[{"category":"airfare","description":"Vé máy bay SGN-HAN — PRJ-014","amount":3500000,"receipt_attached":true,"vendor_mst":"0100107638","invoice_no":"HD0012345","vat_amount":318182}]}}'
req POST "/vouchers/${exp001_id}/expense-claim-result/propose" "${admin2_token}" ""
echo "propose outcome: $(jqf .outcome) approvedAmount=$(jqf .approvedAmount) payableAmount=$(jqf .payableAmount)"
req POST "/vouchers/${exp001_id}/expense-claim/department-review" "${admin1_token}" ""
req POST "/vouchers/${exp001_id}/expense-claim/accounting-review" "${admin3_token}" ""
req POST "/vouchers/${exp001_id}/apply-expense" "${admin2_token}" ""
echo "apply-expense as the CREATOR (admin2) -> expect 403 self-approval block: HTTP ${status}"
req POST "/vouchers/${exp001_id}/tax-verification/confirm" "${admin1_token}" ""
req POST "/vouchers/${exp001_id}/apply-expense" "${admin4_token}" ""
echo "apply-expense as a genuinely distinct 4th admin -> HTTP ${status}, final reviewStatus=$(jqf '.expense_claim_result.reviewStatus')"
req GET "/vouchers/${exp001_id}" "${admin1_token}" ""
echo "Journal proposal: $(echo "${body_out}" | jq -c '.journalSuggestions | map({debitAccount,creditAccount,amount})')"

echo ""
echo "== Done. See master_data.json / luong0N_*.json / expected_results.json for the full case list. =="
echo "Isolated stack will now be torn down (trap)."
