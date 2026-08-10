#!/usr/bin/env bash
# migrate_vouchers_to_s3.sh — one-shot migration of pre-E-4 voucher files.
#
# Reads every voucher row whose source_file_path still points at the
# legacy /var/aiq/uploads/ Docker volume, uploads that file to the
# MinIO voucher bucket under vouchers/legacy/{basename}, and rewrites
# source_file_path to the S3 key so the gateway's preview endpoint
# (E-5) can serve it via S3 like new uploads.
#
# Idempotent: rows already under vouchers/ prefix are skipped. Files
# missing from disk are logged and skipped (row keeps old path, will
# 404 on preview — user can re-upload).
#
# Run on the cosmos VPS:
#   ./scripts/migrate_vouchers_to_s3.sh
#
# No files are deleted from local storage — cleanup is a separate
# manual step once we're confident the S3 copies serve correctly.

set -euo pipefail

BUCKET="voucher-uploads"
GATEWAY_VOLUME="/var/lib/docker/volumes/aiq-agentic_gateway_uploads/_data"
PG_CONTAINER="aiq-postgres"
MINIO_CONTAINER="aiq-minio"
S3_PREFIX="vouchers/legacy"

echo "[1/4] Fetch legacy voucher rows from DB..."
mapfile -t rows < <(sudo docker exec $PG_CONTAINER psql -U aiq -d aiq -tAc \
    "SELECT id || '|' || source_file_path
     FROM vouchers
     WHERE source_file_path IS NOT NULL
       AND source_file_path NOT LIKE 'vouchers/%'")

if [ ${#rows[@]} -eq 0 ]; then
    echo "No legacy rows to migrate. Everything already on S3."
    exit 0
fi
echo "Found ${#rows[@]} legacy voucher rows."

echo "[2/4] Wire mc alias (needs MinIO password from .env)..."
MINIO_PASSWORD=$(grep '^MINIO_ROOT_PASSWORD=' /opt/apps/aiq/.env | cut -d= -f2)
sudo docker exec $MINIO_CONTAINER mc alias set aiq http://localhost:9000 aiq "$MINIO_PASSWORD" >/dev/null

echo "[3/4] Copy files + update DB..."
migrated=0
skipped_missing=0
for row in "${rows[@]}"; do
    voucher_id="${row%%|*}"
    local_path="${row#*|}"
    basename=$(basename "$local_path")
    s3_key="$S3_PREFIX/$basename"

    if [ ! -f "$GATEWAY_VOLUME/$basename" ]; then
        echo "  [$voucher_id] MISSING on disk: $local_path"
        skipped_missing=$((skipped_missing + 1))
        continue
    fi

    # Stream the file into MinIO via mc — no temp copy needed.
    sudo docker cp "$GATEWAY_VOLUME/$basename" $MINIO_CONTAINER:/tmp/migrate.tmp
    sudo docker exec $MINIO_CONTAINER mc cp /tmp/migrate.tmp "aiq/$BUCKET/$s3_key" >/dev/null
    sudo docker exec $MINIO_CONTAINER rm -f /tmp/migrate.tmp

    # Update DB row to point at the new S3 key.
    sudo docker exec $PG_CONTAINER psql -U aiq -d aiq -c \
        "UPDATE vouchers SET source_file_path = '$s3_key' WHERE id = '$voucher_id';" >/dev/null

    echo "  [$voucher_id] OK: $basename -> $s3_key"
    migrated=$((migrated + 1))
done

echo "[4/4] Verify..."
remaining=$(sudo docker exec $PG_CONTAINER psql -U aiq -d aiq -tAc \
    "SELECT count(*) FROM vouchers WHERE source_file_path NOT LIKE 'vouchers/%' AND source_file_path IS NOT NULL")

echo ""
echo "Done."
echo "  Migrated to S3: $migrated"
echo "  Skipped (missing on disk): $skipped_missing"
echo "  Remaining legacy rows in DB: $remaining"
echo ""
echo "Local files preserved at $GATEWAY_VOLUME. Delete manually after verification:"
echo "  sudo rm $GATEWAY_VOLUME/voucher-*.jpg $GATEWAY_VOLUME/voucher-*.pdf"
