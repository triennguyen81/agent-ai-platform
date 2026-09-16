#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="${$}"
postgres_name="aiq-fresh-postgres-${run_id}"
gateway_name="aiq-fresh-gateway-${run_id}"
network_name="aiq-fresh-network-${run_id}"
volume_name="aiq-fresh-postgres-${run_id}"
database_name="aiq_fresh"
database_password="fresh_test_password"
gateway_image="aiq-agentic-gateway:latest"
expected_migrations="$(find "${repo_root}/gateway/src/Aiq.Gateway/Data/Migrations" -maxdepth 1 -type f -name '*.sql' | wc -l)"

cleanup() {
    docker rm -f "${gateway_name}" >/dev/null 2>&1 || true
    docker rm -f "${postgres_name}" >/dev/null 2>&1 || true
    docker volume rm "${volume_name}" >/dev/null 2>&1 || true
    docker network rm "${network_name}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker network create "${network_name}" >/dev/null
docker volume create "${volume_name}" >/dev/null

docker run --detach \
    --name "${postgres_name}" \
    --network "${network_name}" \
    --network-alias postgres \
    --env POSTGRES_USER=postgres \
    --env POSTGRES_PASSWORD="${database_password}" \
    --env POSTGRES_DB="${database_name}" \
    --volume "${volume_name}:/var/lib/postgresql/data" \
    --volume "${repo_root}/infra/postgres/init:/docker-entrypoint-initdb.d:ro" \
    pgvector/pgvector:pg16 >/dev/null

for _ in $(seq 1 90); do
    if docker logs "${postgres_name}" 2>&1 | grep -q 'PostgreSQL init process complete'; then
        break
    fi
    sleep 1
done
docker logs "${postgres_name}" 2>&1 | grep -q 'PostgreSQL init process complete'
docker exec "${postgres_name}" pg_isready -U postgres -d "${database_name}" >/dev/null

docker run --detach \
    --name "${gateway_name}" \
    --network "${network_name}" \
    --network-alias gateway \
    --env ASPNETCORE_ENVIRONMENT=Production \
    --env ASPNETCORE_URLS=http://+:8080 \
    --env "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=${database_name};Username=postgres;Password=${database_password};Pooling=true" \
    --env Jwt__Issuer=aiq.bootstrap.test \
    --env Jwt__Audience=aiq.bootstrap.client \
    --env Jwt__SigningKey=fresh-bootstrap-test-signing-key-32-bytes \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://object.invalid:9000 \
    --env Object__AccessKey=test \
    --env Object__SecretKey=test-password \
    "${gateway_image}" >/dev/null

check_gateway() {
    docker run --rm --network "${network_name}" curlimages/curl:8.12.1 \
        --connect-timeout 1 --max-time 2 --fail --silent \
        http://gateway:8080/healthz/ready >/dev/null
}

for _ in $(seq 1 60); do
    if check_gateway; then
        break
    fi
    sleep 1
done
check_gateway

docker exec "${postgres_name}" psql -v ON_ERROR_STOP=1 -U postgres -d "${database_name}" -c "
DO \$\$
DECLARE
    expected_migrations integer;
    applied_migrations integer;
    missing_tables integer;
BEGIN
    SELECT count(*) INTO applied_migrations FROM schema_migrations;
    expected_migrations := ${expected_migrations};
    IF applied_migrations <> expected_migrations THEN
        RAISE EXCEPTION 'expected % migrations, found %', expected_migrations, applied_migrations;
    END IF;

    SELECT count(*) INTO missing_tables
    FROM unnest(ARRAY[
        'departments', 'sim_customers', 'sim_suppliers', 'sim_employees',
        'expense_claim_results', 'sim_ar_ledger', 'sim_sales_orders',
        'sim_delivery_notes', 'sim_contracts', 'sim_contract_milestones',
        'sim_purchase_orders', 'sim_ap_ledger', 'sim_goods_received_notes'
    ]) table_name
    WHERE to_regclass(table_name) IS NULL;
    IF missing_tables <> 0 THEN
        RAISE EXCEPTION '% required Gateway business tables are missing', missing_tables;
    END IF;

    IF EXISTS (
        SELECT 1 FROM tenants t
        LEFT JOIN departments d ON d.tenant_id=t.id AND d.is_default
        GROUP BY t.id HAVING count(d.id) <> 1
    ) THEN
        RAISE EXCEPTION 'invalid default department count';
    END IF;

    IF EXISTS (
        SELECT 1 FROM tenants t
        LEFT JOIN users u ON u.tenant_id=t.id AND u.is_active
        LEFT JOIN user_roles ur ON ur.user_id=u.id
        LEFT JOIN roles r ON r.id=ur.role_id AND r.name='tenant_owner'
        WHERE t.status='active'
        GROUP BY t.id HAVING count(r.id) <> 1
    ) THEN
        RAISE EXCEPTION 'invalid active tenant owner count';
    END IF;
END \$\$;"

before_restart="$(docker exec "${postgres_name}" psql -At -U postgres -d "${database_name}" -c 'SELECT count(*) FROM schema_migrations')"
docker restart "${gateway_name}" >/dev/null
for _ in $(seq 1 60); do
    if check_gateway; then
        break
    fi
    sleep 1
done
check_gateway
after_restart="$(docker exec "${postgres_name}" psql -At -U postgres -d "${database_name}" -c 'SELECT count(*) FROM schema_migrations')"

if [[ "${before_restart}" != "${after_restart}" ]]; then
    echo "migration count changed across restart: ${before_restart} -> ${after_restart}" >&2
    exit 1
fi

echo "Fresh bootstrap passed: ${after_restart} migrations, Gateway ready before and after restart."
