# Disaster Recovery & Backup

> **Status:** Phase 4 baseline. Backups run from `infra/backup/*.sh`. Restore
> drills (RTO/RPO measurement) must be performed quarterly before this
> document can be considered field-tested.

---

## Objectives

| Metric | Target | Tier |
|--------|--------|------|
| **RPO** — recovery point objective | ≤ 15 min for Postgres, ≤ 24h for Qdrant | Phase 4 baseline |
| **RTO** — recovery time objective | ≤ 1 hour for read traffic, ≤ 4h for full write traffic | Phase 4 baseline |
| **Backup verification** | Every backup auto-verified (`pg_restore --list`, snapshot SHA-256) | Active |
| **Restore drill cadence** | Quarterly; results recorded against this doc's "Drill log" section | Required |

Update these when the deployment topology changes — outdated targets are
worse than no targets.

---

## What gets backed up

| Data | Tool | Cadence | Storage location | Retention |
|------|------|---------|------------------|-----------|
| PostgreSQL (`aiq` DB) | `infra/backup/postgres-backup.sh` (pg_dump custom format) | Hourly | `/var/backups/aiq` → Synology NAS via `rsync` | 14 days local + 90 days NAS |
| PostgreSQL WAL | `archive_command` → object storage | Continuous | S3/MinIO bucket `aiq-wal` | 7 days |
| Qdrant collections | `infra/backup/qdrant-backup.sh` (Snapshot API) | Daily | `/var/backups/aiq/qdrant` → NAS | 14 days local + 90 days NAS |
| Redis (conversation memory) | **Not backed up** (volatile by design) | — | — | — |
| Uploaded PDFs (`gateway_uploads`) | Btrfs snapshot of host volume | Daily | Btrfs subvolume + offsite NAS | 30 days |
| Application secrets | Vault audit-engine snapshots | Daily | Vault primary + DR replica | 30 days |
| Audit log table | Covered by Postgres backup | — | — | — |

Redis is intentionally excluded — chat memory is recoverable from Postgres
audit data and the user can replay the conversation. Keeping Redis backups
would only add attack surface for stale credentials.

---

## How to run the scripts

### Postgres

```bash
# Schedule with systemd timer or cron:
#   0 * * * *  /opt/aiq/infra/backup/postgres-backup.sh
POSTGRES_HOST=postgres \
POSTGRES_USER=aiq \
POSTGRES_DB=aiq \
PGPASSWORD="$(vault kv get -field=password aiq/postgres)" \
BACKUP_DIR=/var/backups/aiq \
RETENTION_DAYS=14 \
  ./infra/backup/postgres-backup.sh
```

The script writes `aiq-YYYYMMDDTHHMMSSZ.dump` + `.sha256` sidecar and prunes
anything older than `RETENTION_DAYS`.

### Qdrant

```bash
QDRANT_URL=http://qdrant:6333 \
QDRANT_API_KEY="$(vault kv get -field=api_key aiq/qdrant)" \
BACKUP_DIR=/var/backups/aiq/qdrant \
RETENTION_DAYS=14 \
  ./infra/backup/qdrant-backup.sh
```

For each collection: snapshot create → download → SHA-256 sidecar →
best-effort delete the in-cluster snapshot.

---

## Restore procedures

### Full Postgres restore (worst case)

1. **Stop the gateway**: `docker compose stop gateway` — prevents partial
   writes during restore.
2. **Find the latest verified dump**:

   ```bash
   ls -1t /var/backups/aiq/*.dump | head -1
   ```

3. **Verify SHA-256** before restoring (catches storage rot):

   ```bash
   sha256sum -c aiq-YYYYMMDDTHHMMSSZ.dump.sha256
   ```

4. **Restore** with `--clean --if-exists` to drop the corrupt DB safely:

   ```bash
   PGPASSWORD="$AIQ_PG_PASSWORD" pg_restore \
       --host=postgres --username=aiq \
       --dbname=aiq \
       --clean --if-exists \
       --no-owner --no-privileges \
       --jobs=4 \
       aiq-YYYYMMDDTHHMMSSZ.dump
   ```

5. **Replay WAL** if the dump is older than 15 min: point-in-time recovery
   from the archive bucket.
6. **Validate**: run the schema fixture queries from
   `infra/postgres/init/02_schema.sql` and confirm all expected tables exist.
7. **Restart gateway** and watch `audit_log` for the first successful write.

### Point-in-time recovery (PITR)

For partial corruption (e.g. a runaway DELETE):

1. Stop gateway.
2. `pg_basebackup` the standby into `/var/lib/postgresql/data-recovery`.
3. Build `recovery.signal` with `restore_command` pointing at the WAL archive
   and `recovery_target_time = '2026-06-09T08:00:00Z'`.
4. Start Postgres in recovery mode, promote when caught up, swap the data dir.

### Qdrant collection restore

1. Recreate the empty collection with the same vector size + distance metric
   you used originally.
2. Upload the snapshot file via the snapshot API:

   ```bash
   curl -X POST "${QDRANT_URL}/collections/${name}/snapshots/upload" \
       -H "api-key: $QDRANT_API_KEY" \
       -F "snapshot=@${name}-YYYYMMDDTHHMMSSZ.snapshot"
   ```

3. Wait for indexing to complete (`/collections/{name}` will report
   `status: green`).

### Uploaded PDFs

Restore from the Btrfs snapshot:

```bash
btrfs subvolume snapshot \
    /backup/aiq/uploads/snapshot-YYYYMMDD \
    /var/lib/docker/volumes/aiq_gateway_uploads/_data
```

If the snapshot lives on the offsite NAS, `rsync` it first, then snapshot.

---

## Drill log

Append one row each quarter. Older than 12 months → archive.

| Date (UTC) | Operator | Scenario | RPO observed | RTO observed | Notes |
|-----------|----------|----------|--------------|--------------|-------|
| (none yet) | — | initial baseline | — | — | first drill scheduled for 2026-Q3 |

---

## What we explicitly do NOT do

- We do not back up the `paddleocr` model weights — they are reproducible
  from upstream and would 10x our backup size.
- We do not back up Prometheus or Loki — their data is observability,
  losing 14 days is acceptable.
- We do not store backups in the same trust domain as production. The NAS
  in particular is on its own VLAN with read-only Postgres credentials.

---

## References

- PostgreSQL: [Backup and Restore](https://www.postgresql.org/docs/current/backup.html)
- Qdrant: [Snapshots and recovery](https://qdrant.tech/documentation/concepts/snapshots/)
- WAL-G: [Streaming WAL archiver](https://github.com/wal-g/wal-g)
- Encryption: see `docs/security/encryption-at-rest.md`
