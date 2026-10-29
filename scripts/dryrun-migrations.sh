#!/usr/bin/env bash
# Dry-run Gateway migrations against a COPY of a database, never the database itself.
#
#   scripts/dryrun-migrations.sh <dump-file> [--until 070] [--db aiq] [--user aiq]
#
# <dump-file>: pg_dump output — custom format (-Fc), or plain SQL (optionally .gz).
# The dump is restored into a throwaway Postgres container (no published port,
# its own network, no volume), the migrations in gateway/src/Aiq.Gateway/Data/Migrations
# are applied up to and including --until exactly as DatabaseMigrator does
# (ordinal order, one transaction per file, schema_migrations + SHA-256 checksum,
# refuse a changed applied file), then the chứng từ flow v2 PR3 statistics are
# printed. The container is removed on exit. Takes no connection string on purpose.
set -euo pipefail

usage() { sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-2}"; }

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
migrations_dir="${repo_root}/gateway/src/Aiq.Gateway/Data/Migrations"
until_prefix="070"
db_name="aiq"
db_user="aiq"
dump=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --until) until_prefix="$2"; shift 2 ;;
        --db) db_name="$2"; shift 2 ;;
        --user) db_user="$2"; shift 2 ;;
        -h|--help) usage 0 ;;
        -*) echo "unknown option: $1" >&2; usage ;;
        *) [[ -z "${dump}" ]] || usage; dump="$1"; shift ;;
    esac
done
[[ -n "${dump}" ]] || usage
[[ -f "${dump}" && -r "${dump}" ]] || { echo "dump file not found or unreadable: ${dump}" >&2; exit 2; }
[[ "${until_prefix}" =~ ^[0-9]{3}$ ]] || { echo "--until takes a 3-digit migration number" >&2; exit 2; }
dump="$(cd "$(dirname "${dump}")" && pwd)/$(basename "${dump}")"

run_id="$$-$(date +%s)"
container="aiq-dryrun-pg-${run_id}"
network="aiq-dryrun-net-${run_id}"
cleanup() {
    docker rm -f "${container}" >/dev/null 2>&1 || true
    docker network rm "${network}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

log() { printf '\n== %s\n' "$*"; }
psql_c() { docker exec -i "${container}" psql -X -q -v ON_ERROR_STOP=1 -U "${db_user}" -d "${db_name}" "$@"; }
scalar() { psql_c -At -c "$1"; }

log "starting throwaway Postgres ${container} (no published port)"
docker network create --internal "${network}" >/dev/null
docker run --detach --name "${container}" --network "${network}" \
    --env POSTGRES_USER="${db_user}" --env POSTGRES_PASSWORD="dryrun-$(od -An -N8 -tx1 /dev/urandom | tr -d ' \n')" \
    --env POSTGRES_DB="${db_name}" \
    --volume "${dump}:/dump/input:ro" \
    pgvector/pgvector:pg16 >/dev/null
for _ in $(seq 1 90); do
    docker logs "${container}" 2>&1 | grep -q 'PostgreSQL init process complete' && break
    sleep 1
done
docker logs "${container}" 2>&1 | grep -q 'PostgreSQL init process complete' || { echo "postgres did not start" >&2; exit 1; }
until docker exec "${container}" pg_isready -U "${db_user}" -d "${db_name}" >/dev/null 2>&1; do sleep 1; done

log "restoring $(basename "${dump}")"
if [[ "$(head -c5 "${dump}")" == "PGDMP" ]]; then
    docker exec "${container}" pg_restore --no-owner --no-privileges --exit-on-error -U "${db_user}" -d "${db_name}" /dump/input
elif [[ "$(head -c2 "${dump}" | od -An -tx1 | tr -d ' ')" == "1f8b" ]]; then
    docker exec "${container}" sh -c "gunzip -c /dump/input | psql -X -q -v ON_ERROR_STOP=1 -U '${db_user}' -d '${db_name}'" >/dev/null
else
    docker exec "${container}" psql -X -q -v ON_ERROR_STOP=1 -U "${db_user}" -d "${db_name}" -f /dump/input >/dev/null
fi
[[ "$(scalar "SELECT to_regclass('public.vouchers') IS NOT NULL")" == "t" ]] || { echo "the dump has no public.vouchers table" >&2; exit 1; }

# Old status of every voucher, before any migration runs (own schema, dropped with the container).
psql_c -c "CREATE SCHEMA aiq_dryrun;
           CREATE TABLE aiq_dryrun.before AS SELECT id, status FROM vouchers;"
journalized_before="$(scalar "SELECT COUNT(*) FROM vouchers WHERE status = 'journalized'")"

log "applying migrations up to ${until_prefix}"
psql_c -c "CREATE TABLE IF NOT EXISTS schema_migrations (
               version TEXT PRIMARY KEY, checksum TEXT, applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW());
           ALTER TABLE schema_migrations ADD COLUMN IF NOT EXISTS checksum TEXT;"
applied_now=0
while IFS= read -r -u 3 path; do
    version="$(basename "${path}")"
    [[ "${version:0:3}" > "${until_prefix}" ]] && break
    checksum="$(sha256sum "${path}" | cut -d' ' -f1)"
    recorded="$(scalar "SELECT COALESCE(checksum, '<null>') FROM schema_migrations WHERE version = '${version}'")"
    if [[ -n "${recorded}" ]]; then
        if [[ "${recorded}" == "<null>" ]]; then
            psql_c -c "UPDATE schema_migrations SET checksum = '${checksum}' WHERE version = '${version}' AND checksum IS NULL"
            echo "   baseline  ${version}"
        elif [[ "${recorded}" != "${checksum}" ]]; then
            echo "Applied migration checksum mismatch: ${version} — DatabaseMigrator would refuse to start." >&2
            exit 1
        fi
        continue
    fi
    started=$(date +%s)
    { cat "${path}"; printf "\nINSERT INTO schema_migrations(version, checksum) VALUES ('%s', '%s');\n" "${version}" "${checksum}"; } \
        | psql_c --single-transaction >/dev/null
    echo "   applied   ${version} ($(( $(date +%s) - started ))s)"
    applied_now=$((applied_now + 1))
done 3< <(find "${migrations_dir}" -maxdepth 1 -type f -name '*.sql' | LC_ALL=C sort)
echo "   ${applied_now} migration(s) applied in this run"

if [[ "$(scalar "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'vouchers' AND column_name = 'lifecycle_state')")" != "t" ]]; then
    echo "vouchers.lifecycle_state does not exist after migrating to ${until_prefix} — nothing to report." >&2
    exit 1
fi

log "vouchers by (old status, new status, lifecycle_state)"
psql_c -c "SELECT b.status AS old_status, v.status AS new_status, v.lifecycle_state, COUNT(*) AS vouchers
             FROM aiq_dryrun.before b JOIN vouchers v ON v.id = b.id
            GROUP BY 1, 2, 3 ORDER BY 1, 2, 3;"

log "journalized"
journalized_after="$(scalar "SELECT COUNT(*) FROM vouchers WHERE status = 'journalized'")"
echo "   before: ${journalized_before}   after: ${journalized_after}"
psql_c -c "SELECT v.status, v.lifecycle_state, COUNT(*) AS vouchers
             FROM aiq_dryrun.before b JOIN vouchers v ON v.id = b.id
            WHERE b.status = 'journalized' GROUP BY 1, 2 ORDER BY 1, 2;"

log "receipts / payments cancelled through the legacy path (reversed cash, nothing posted now)"
if [[ "$(scalar "SELECT to_regclass('public.cash_transactions') IS NOT NULL")" == "t" ]]; then
    psql_c -c "WITH r AS (
                   SELECT v.id, v.status, v.lifecycle_state, v.posted_at
                     FROM vouchers v
                    WHERE EXISTS (SELECT 1 FROM cash_transactions c WHERE c.voucher_id = v.id AND c.status = 'reversed')
                      AND NOT EXISTS (SELECT 1 FROM cash_transactions c WHERE c.voucher_id = v.id AND c.status = 'posted'))
               SELECT COUNT(*) AS vouchers,
                      COUNT(*) FILTER (WHERE posted_at IS NOT NULL) AS with_posted_at,
                      COUNT(*) FILTER (WHERE EXISTS (SELECT 1 FROM voucher_events e WHERE e.voucher_id = r.id AND e.event_type = 'legacy_backfill')) AS with_backfill_event,
                      COUNT(*) FILTER (WHERE status <> 'extracted') AS not_extracted
                 FROM r;"
else
    echo "   (no cash_transactions table in this dump)"
fi

log "checks"
failed=0
null_lifecycle="$(scalar "SELECT COUNT(*) FROM vouchers WHERE lifecycle_state IS NULL")"
echo "   lifecycle_state IS NULL: ${null_lifecycle}"
[[ "${null_lifecycle}" == "0" ]] || failed=1
[[ "${journalized_after}" == "0" ]] || { echo "   journalized rows remain"; failed=1; }
mismatch="$(scalar "SELECT COUNT(*) FROM vouchers
                     WHERE voucher_status_from_lifecycle(lifecycle_state) IS NOT NULL
                       AND voucher_status_from_lifecycle(lifecycle_state) <> status")"
echo "   status not matching lifecycle_state: ${mismatch}"
[[ "${mismatch}" == "0" ]] || failed=1
echo "   vouchers before: $(scalar "SELECT COUNT(*) FROM aiq_dryrun.before")   after: $(scalar "SELECT COUNT(*) FROM vouchers")"
[[ "$(scalar "SELECT COUNT(*) FROM aiq_dryrun.before")" == "$(scalar "SELECT COUNT(*) FROM vouchers")" ]] || { echo "   voucher count changed"; failed=1; }

if [[ "${failed}" == "0" ]]; then log "OK"; else log "FAILED"; fi
exit "${failed}"
