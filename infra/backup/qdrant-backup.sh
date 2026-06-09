#!/usr/bin/env bash
# Qdrant snapshot backup via the snapshot API.
#
# Iterates over every collection, requests a snapshot, downloads it, computes
# SHA-256, and prunes snapshots older than RETENTION_DAYS.
#
# Environment variables:
#   QDRANT_URL          (default: http://qdrant:6333)
#   QDRANT_API_KEY      (optional — sent as api-key header when set)
#   BACKUP_DIR          (default: /var/backups/aiq/qdrant)
#   RETENTION_DAYS      (default: 14)
#
# Exit codes:
#   0 — all collections snapshotted
#   1 — config / dependency missing
#   2 — API call failed
#   3 — download failed

set -Eeuo pipefail

QDRANT_URL="${QDRANT_URL:-http://qdrant:6333}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/aiq/qdrant}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"

command -v curl >/dev/null 2>&1 || { echo "curl is required" >&2; exit 1; }
command -v jq   >/dev/null 2>&1 || { echo "jq is required"   >&2; exit 1; }

mkdir -p "$BACKUP_DIR"

auth_args=()
if [ -n "${QDRANT_API_KEY:-}" ]; then
    auth_args=(-H "api-key: ${QDRANT_API_KEY}")
fi

timestamp=$(date -u +%Y%m%dT%H%M%SZ)

echo "[$(date -u +%FT%TZ)] listing collections at ${QDRANT_URL}"
collections=$(curl -fsS "${auth_args[@]}" "${QDRANT_URL}/collections" | jq -r '.result.collections[].name') \
  || { echo "Failed to list collections" >&2; exit 2; }

if [ -z "$collections" ]; then
    echo "No collections to back up."
    exit 0
fi

while IFS= read -r collection; do
    [ -z "$collection" ] && continue
    echo "[$(date -u +%FT%TZ)] snapshot → ${collection}"

    snapshot_name=$(curl -fsS -X POST "${auth_args[@]}" \
        "${QDRANT_URL}/collections/${collection}/snapshots" \
        | jq -r '.result.name') \
      || { echo "Snapshot create failed for ${collection}" >&2; exit 2; }

    download_path="${BACKUP_DIR}/${collection}-${timestamp}.snapshot"
    curl -fsS "${auth_args[@]}" \
        -o "$download_path" \
        "${QDRANT_URL}/collections/${collection}/snapshots/${snapshot_name}" \
      || { echo "Download failed for ${collection}/${snapshot_name}" >&2; exit 3; }

    sha256sum "$download_path" > "${download_path}.sha256"
    size=$(du -h "$download_path" | awk '{print $1}')
    echo "[$(date -u +%FT%TZ)]   ${collection}: ${size}"

    # Best-effort cleanup of the snapshot inside Qdrant itself.
    curl -fsS -X DELETE "${auth_args[@]}" \
        "${QDRANT_URL}/collections/${collection}/snapshots/${snapshot_name}" \
        > /dev/null || true
done <<< "$collections"

# Retention pass.
find "$BACKUP_DIR" -maxdepth 1 -type f \
    \( -name "*.snapshot" -o -name "*.snapshot.sha256" \) \
    -mtime "+$RETENTION_DAYS" -print -delete

echo "[$(date -u +%FT%TZ)] qdrant backup complete (>${RETENTION_DAYS}d removed)"
