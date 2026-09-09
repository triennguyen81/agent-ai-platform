#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="${$}"
postgres_name="aiq-v2a-upgrade-postgres-${run_id}"
gateway_name="aiq-v2a-upgrade-gateway-${run_id}"
network_name="aiq-v2a-upgrade-network-${run_id}"
volume_name="aiq-v2a-upgrade-postgres-${run_id}"
database_name="aiq_v2a_upgrade"
database_password="v2a_upgrade_test_password"
gateway_image="aiq-agentic-gateway:latest"
migration_dir="${repo_root}/gateway/src/Aiq.Gateway/Data/Migrations"

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
    --volume "${migration_dir}:/migrations:ro" \
    pgvector/pgvector:pg16 >/dev/null

for _ in $(seq 1 90); do
    if docker logs "${postgres_name}" 2>&1 | grep -q 'PostgreSQL init process complete'; then
        break
    fi
    sleep 1
done
docker logs "${postgres_name}" 2>&1 | grep -q 'PostgreSQL init process complete'
docker exec "${postgres_name}" pg_isready -U postgres -d "${database_name}" >/dev/null

# Reproduce an already-deployed D8 database: apply and checksum 001-023, but
# deliberately leave 024 unapplied before inserting representative live data.
docker exec "${postgres_name}" psql -v ON_ERROR_STOP=1 -U postgres -d "${database_name}" -c '
CREATE TABLE IF NOT EXISTS schema_migrations (
    version TEXT PRIMARY KEY,
    checksum TEXT,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);' >/dev/null

for migration_path in "${migration_dir}"/{001..023}_*.sql; do
    migration_name="$(basename "${migration_path}")"
    migration_checksum="$(sha256sum "${migration_path}" | cut -d' ' -f1)"
    docker exec "${postgres_name}" psql -v ON_ERROR_STOP=1 -U postgres -d "${database_name}" \
        -f "/migrations/${migration_name}" >/dev/null
    docker exec "${postgres_name}" psql -v ON_ERROR_STOP=1 -U postgres -d "${database_name}" -c \
        "INSERT INTO schema_migrations(version,checksum) VALUES ('${migration_name}','${migration_checksum}') ON CONFLICT(version) DO NOTHING" >/dev/null
done

docker exec "${postgres_name}" psql -v ON_ERROR_STOP=1 -U postgres -d "${database_name}" <<'SQL' >/dev/null
DO $$
DECLARE
    tenant_id UUID := '10000000-0000-0000-0000-000000000001';
    owner_id UUID := '20000000-0000-0000-0000-000000000001';
    admin_id UUID := '20000000-0000-0000-0000-000000000002';
    department_a UUID := '30000000-0000-0000-0000-000000000001';
    department_b UUID := '30000000-0000-0000-0000-000000000002';
    single_agent UUID := '40000000-0000-0000-0000-000000000001';
    shared_agent UUID := '40000000-0000-0000-0000-000000000002';
    orphan_agent UUID := '40000000-0000-0000-0000-000000000003';
    note_id UUID := '50000000-0000-0000-0000-000000000001';
BEGIN
    INSERT INTO tenants(id,code,name,status)
    VALUES (tenant_id,'v2a-populated','V2-A Populated Upgrade','suspended');
    INSERT INTO departments(id,tenant_id,code,name,status,is_default)
    VALUES
      (department_a,tenant_id,'accounting','Accounting','active',true),
      (department_b,tenant_id,'sales','Sales','active',false);
    INSERT INTO users(id,email,password_hash,tenant_id,department_id,is_active)
    VALUES
      (owner_id,'owner-v2a@test','x',tenant_id,NULL,true),
      (admin_id,'admin-v2a@test','x',tenant_id,department_a,true);
    INSERT INTO user_roles(user_id,role_id)
      SELECT owner_id,id FROM roles WHERE name='tenant_owner';
    INSERT INTO user_roles(user_id,role_id)
      SELECT admin_id,id FROM roles WHERE name='admin';
    UPDATE tenants SET status='active' WHERE id=tenant_id;

    INSERT INTO agents(
      id,tenant_id,code,name,owner_user_id,slug,scope,owning_tenant_id,created_by,updated_by)
    VALUES
      (single_agent,tenant_id,'single-agent','Single Agent',admin_id,'single-agent','tenant',tenant_id,admin_id,admin_id),
      (shared_agent,tenant_id,'shared-agent','Shared Agent',admin_id,'shared-agent','tenant',tenant_id,admin_id,admin_id),
      (orphan_agent,tenant_id,'orphan-agent','Orphan Agent',admin_id,'orphan-agent','tenant',tenant_id,admin_id,admin_id);
    INSERT INTO agent_department_assignments(tenant_id,department_id,agent_id,status,assigned_by)
    VALUES
      (tenant_id,department_a,single_agent,'active',owner_id),
      (tenant_id,department_a,shared_agent,'active',owner_id),
      (tenant_id,department_b,shared_agent,'active',owner_id);
    INSERT INTO notes(id,user_id,tenant_id,title,content)
    VALUES (note_id,admin_id,tenant_id,'Existing populated knowledge','must survive');
    INSERT INTO agent_knowledge(agent_id,note_id,attached_by)
    VALUES (single_agent,note_id,admin_id);
END $$;
SQL

docker run --detach \
    --name "${gateway_name}" \
    --network "${network_name}" \
    --network-alias gateway \
    --env ASPNETCORE_ENVIRONMENT=Production \
    --env ASPNETCORE_URLS=http://+:8080 \
    --env "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=${database_name};Username=postgres;Password=${database_password};Pooling=true" \
    --env Jwt__Issuer=aiq.v2a.upgrade.test \
    --env Jwt__Audience=aiq.v2a.upgrade.client \
    --env Jwt__SigningKey=v2a-upgrade-test-signing-key-32-bytes \
    --env Agent__BaseUrl=http://agent.invalid:8000 \
    --env Object__ServiceUrl=http://object.invalid:9000 \
    --env Object__AccessKey=test \
    --env Object__SecretKey=test-password \
    "${gateway_image}" >/dev/null

for _ in $(seq 1 60); do
    if docker run --rm --network "${network_name}" curlimages/curl:8.12.1 \
        --connect-timeout 1 --max-time 2 --fail --silent http://gateway:8080/healthz/ready >/dev/null 2>&1; then
        break
    fi
    sleep 1
done

docker exec "${postgres_name}" psql -v ON_ERROR_STOP=1 -U postgres -d "${database_name}" <<'SQL'
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM schema_migrations WHERE version='024_acl_v2_scope_foundations.sql') THEN
        RAISE EXCEPTION 'migration 024 was not recorded';
    END IF;
    IF (SELECT count(*) FROM schema_migrations) <> 24 THEN
        RAISE EXCEPTION 'expected 24 applied migrations';
    END IF;
    IF (SELECT v2_scope FROM agents WHERE code='single-agent') <> 'department' THEN
        RAISE EXCEPTION 'single-department agent was not resolved';
    END IF;
    IF (SELECT department_id FROM agents WHERE code='single-agent') IS DISTINCT FROM
       '30000000-0000-0000-0000-000000000001'::UUID THEN
        RAISE EXCEPTION 'single-department agent resolved to wrong department';
    END IF;
    IF (SELECT v2_migration_status FROM agents WHERE code='shared-agent') <> 'quarantined_shared'
       OR (SELECT department_id FROM agents WHERE code='shared-agent') IS NOT NULL THEN
        RAISE EXCEPTION 'shared agent was guessed instead of quarantined';
    END IF;
    IF (SELECT v2_migration_status FROM agents WHERE code='orphan-agent') <> 'quarantined_no_department' THEN
        RAISE EXCEPTION 'orphan agent was not reported';
    END IF;
    IF (SELECT content FROM notes WHERE id='50000000-0000-0000-0000-000000000001') <> 'must survive' THEN
        RAISE EXCEPTION 'existing knowledge changed';
    END IF;
    IF (SELECT scope FROM business_capabilities LIMIT 1) <> 'platform' THEN
        RAISE EXCEPTION 'existing global capabilities were not preserved as platform scope';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM acl_v2_agent_scope_issues
        WHERE resource_id='40000000-0000-0000-0000-000000000002'::UUID
          AND issue_code='agent_multiple_departments') THEN
        RAISE EXCEPTION 'shared-agent validation report is missing';
    END IF;
END $$;
SQL

before_restart="$(docker exec "${postgres_name}" psql -At -U postgres -d "${database_name}" -c 'SELECT count(*) FROM schema_migrations')"
docker restart "${gateway_name}" >/dev/null
for _ in $(seq 1 60); do
    if docker logs "${gateway_name}" 2>&1 | grep -q 'Now listening on'; then break; fi
    sleep 1
done
after_restart="$(docker exec "${postgres_name}" psql -At -U postgres -d "${database_name}" -c 'SELECT count(*) FROM schema_migrations')"
[[ "${before_restart}" = "${after_restart}" ]]

echo "ACL V2-A populated upgrade passed: existing rows preserved, deterministic ownership resolved, shared Agent quarantined, ${after_restart} checksummed migrations stable after restart."
