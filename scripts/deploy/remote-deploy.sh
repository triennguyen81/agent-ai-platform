#!/usr/bin/env bash
# Runs ON THE PRODUCTION SERVER (copied there by .github/workflows/docker.yml).
#
#   AIQ_IMAGE_TAG=sha-abc1234 ./remote-deploy.sh
#
# 1. backs up the database (the Gateway applies pending migrations on start,
#    and an image rollback does NOT undo a migration — the dump does);
# 2. pulls the gateway/agent/web images for AIQ_IMAGE_TAG from GHCR;
# 3. recreates only those three services (infrastructure keeps running);
# 4. waits for the health checks; if they fail, redeploys the previous tag
#    and exits non-zero so the workflow run turns red.
#
# Expects the current directory to hold docker-compose.yml,
# docker-compose.prod.yml and the production .env (see docs/operations/deploy.md).
set -euo pipefail

: "${AIQ_IMAGE_TAG:?AIQ_IMAGE_TAG is required (e.g. sha-abc1234)}"
export AIQ_IMAGE_TAG
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-aiq-agentic}"
SERVICES=(gateway agent web)
STATE_DIR=.deploy
BACKUP_DIR="${DEPLOY_BACKUP_DIR:-backups/pre-deploy}"
KEEP_BACKUPS="${DEPLOY_KEEP_BACKUPS:-10}"
HEALTH_TIMEOUT="${DEPLOY_HEALTH_TIMEOUT:-180}"

# Ports published by docker-compose.yml; read the production .env so overrides are honoured.
env_value() { grep -E "^$1=" .env 2>/dev/null | tail -n1 | cut -d= -f2- | tr -d '"' || true; }
GATEWAY_PORT="$(env_value GATEWAY_PORT)"; GATEWAY_PORT="${GATEWAY_PORT:-8080}"
WEB_PORT="$(env_value WEB_PORT)"; WEB_PORT="${WEB_PORT:-3030}"

compose() { docker compose -f docker-compose.yml -f docker-compose.prod.yml "$@"; }
log() { printf '[deploy %s] %s\n' "$(date -u +%H:%M:%SZ)" "$*"; }

wait_healthy() {
  local deadline=$((SECONDS + HEALTH_TIMEOUT))
  until curl -fsS "http://127.0.0.1:${GATEWAY_PORT}/healthz/ready" >/dev/null 2>&1 \
        && curl -fsS -o /dev/null "http://127.0.0.1:${WEB_PORT}/login" \
        && [ "$(docker inspect -f '{{.State.Running}}' "$(compose ps -q agent)" 2>/dev/null)" = "true" ]; do
    if [ "$SECONDS" -ge "$deadline" ]; then return 1; fi
    sleep 5
  done
}

roll_out() {
  AIQ_IMAGE_TAG="$1"
  log "pulling ${SERVICES[*]} @ ${AIQ_IMAGE_TAG}"
  compose pull --quiet "${SERVICES[@]}"
  log "recreating ${SERVICES[*]}"
  compose up -d --no-build --no-deps "${SERVICES[@]}"
}

mkdir -p "$STATE_DIR" "$BACKUP_DIR"
target="$AIQ_IMAGE_TAG"
previous="$(cat "$STATE_DIR/current_tag" 2>/dev/null || true)"
log "target=${target} previous=${previous:-none} project=${COMPOSE_PROJECT_NAME}"

if [ "$(compose ps -q postgres)" ]; then
  dump="$BACKUP_DIR/$(date -u +%Y%m%dT%H%M%SZ)_before_${target}.dump"
  log "backing up database -> ${dump}"
  # shellcheck disable=SC2016  # expanded inside the postgres container, from its own env
  compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" -Fc "$POSTGRES_DB"' > "$dump"
  test -s "$dump"
  # shellcheck disable=SC2012  # file names are our own timestamps
  ls -1t "$BACKUP_DIR"/*.dump | tail -n +"$((KEEP_BACKUPS + 1))" | xargs -r rm -f
else
  log "postgres is not running — skipping backup (first deploy?)"
fi

roll_out "$target"
if wait_healthy; then
  echo "$target" > "$STATE_DIR/current_tag"
  echo "$(date -u +%FT%TZ) ${target}" >> "$STATE_DIR/history"
  docker image prune -f >/dev/null
  log "OK — ${target} is live"
  exit 0
fi

log "health check FAILED for ${target}"
compose logs --tail 80 "${SERVICES[@]}" || true
if [ -n "$previous" ] && [ "$previous" != "$target" ]; then
  log "rolling back to ${previous} (database migrations are NOT reverted; backup: ${dump:-none})"
  roll_out "$previous"
  if wait_healthy; then log "rollback to ${previous} is healthy"; else log "rollback to ${previous} is ALSO unhealthy — manual action needed"; fi
fi
exit 1
