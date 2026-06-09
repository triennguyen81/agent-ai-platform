#!/usr/bin/env bash
# Postgres logical backup with retention + integrity check.
#
# Designed to run from cron or a systemd timer on the database host (or any
# machine that can reach Postgres). Writes a custom-format dump that
# pg_restore can read selectively, then optionally rotates older copies.
#
# Environment variables:
#   POSTGRES_HOST       (default: postgres)
#   POSTGRES_PORT       (default: 5432)
#   POSTGRES_USER       (required)
#   POSTGRES_DB         (required)
#   PGPASSWORD          (required — feed from secret manager, not env file)
#   BACKUP_DIR          (default: /var/backups/aiq)
#   RETENTION_DAYS      (default: 14)
#   BACKUP_PREFIX       (default: aiq)
#
# Exit codes:
#   0 — backup written + verified
#   1 — config missing
#   2 — pg_dump failed
#   3 — verification failed

set -Eeuo pipefail

POSTGRES_HOST="${POSTGRES_HOST:-postgres}"
POSTGRES_PORT="${POSTGRES_PORT:-5432}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/aiq}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"
BACKUP_PREFIX="${BACKUP_PREFIX:-aiq}"

: "${POSTGRES_USER:?POSTGRES_USER is required}"
: "${POSTGRES_DB:?POSTGRES_DB is required}"
: "${PGPASSWORD:?PGPASSWORD is required — feed it from a secret manager, never commit it}"

mkdir -p "$BACKUP_DIR"

timestamp=$(date -u +%Y%m%dT%H%M%SZ)
dump_path="${BACKUP_DIR}/${BACKUP_PREFIX}-${timestamp}.dump"

echo "[$(date -u +%FT%TZ)] starting backup → ${dump_path}"

# Custom format (-Fc) is compressed by default and readable by pg_restore for
# selective restores. --no-owner keeps the dump portable across roles.
PGPASSWORD="$PGPASSWORD" pg_dump \
    --host="$POSTGRES_HOST" \
    --port="$POSTGRES_PORT" \
    --username="$POSTGRES_USER" \
    --dbname="$POSTGRES_DB" \
    --format=custom \
    --compress=9 \
    --no-owner \
    --no-privileges \
    --file="$dump_path" \
  || { echo "pg_dump failed" >&2; exit 2; }

# Verify the dump is loadable (catalog + TOC parse) without actually restoring.
PGPASSWORD="$PGPASSWORD" pg_restore --list "$dump_path" > /dev/null \
  || { echo "pg_restore --list failed — dump corrupt" >&2; exit 3; }

# Sidecar SHA-256 so backup integrity can be verified later.
sha256sum "$dump_path" > "${dump_path}.sha256"

size=$(du -h "$dump_path" | awk '{print $1}')
echo "[$(date -u +%FT%TZ)] backup ok — ${size}"

# Retention: delete dumps older than RETENTION_DAYS (and their .sha256 siblings).
find "$BACKUP_DIR" -maxdepth 1 -type f \
    \( -name "${BACKUP_PREFIX}-*.dump" -o -name "${BACKUP_PREFIX}-*.dump.sha256" \) \
    -mtime "+$RETENTION_DAYS" -print -delete

echo "[$(date -u +%FT%TZ)] retention pass complete (>${RETENTION_DAYS}d removed)"
