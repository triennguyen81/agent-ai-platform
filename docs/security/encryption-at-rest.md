# Encryption at Rest

> **Status:** Deployment-side responsibility. The application layer does not
> manage disk encryption — that belongs in the host/infra provisioning step.
> This document defines what must be configured for each data store before
> production traffic is admitted.

---

## Threat model

Encryption at rest defends against:

- **Lost / decommissioned disks** being read by a third party.
- **Backup tape or snapshot exfiltration** from object storage.
- **Cold cloud-provider access** (e.g. break-glass support engineers reading
  a raw EBS snapshot).

It does **not** defend against:

- A compromised application process that holds valid decryption keys
  (Postgres still serves cleartext to authenticated clients).
- A compromised KMS principal that can decrypt at will.
- Live memory dumps.

For those threats see `security-review`, `audit-logging`, and
`secret-rotation` documents.

---

## Per-store requirements

### PostgreSQL (`aiq` database — books, users, corrections, audit_log)

Choose **one** approach per environment:

| Tier | Approach | Notes |
|------|----------|-------|
| Managed (RDS / Cloud SQL / Aurora) | Enable storage encryption at instance creation. Use a customer-managed KMS key, not the provider default. | Cannot be enabled after the fact — must recreate the instance from a snapshot. |
| Self-hosted (Linux) | LUKS volume mounted at `/var/lib/postgresql/data`. | `cryptsetup luksFormat`, persisted keyfile or systemd-cryptenroll TPM2. |
| Bare-metal cluster | Btrfs subvolume with `aes-xts-plain64` or ZFS native encryption. | Snapshot-friendly; pairs well with the WAL archiving in the Phase 4 DR plan. |

**Required actions in this repo:**

- Document the KMS key alias in your environment's secret manifest.
- Ensure pg-backup pipelines write to an encrypted bucket too — encryption
  at rest is only end-to-end if every copy is encrypted.

### Qdrant (vector embeddings)

Qdrant **does not** support native encryption at rest in the open-source
edition. Cover it at the filesystem layer:

| Deployment | Approach |
|------------|----------|
| Compose / VM | Mount `qdrant_data` volume on a LUKS-encrypted block device. |
| Kubernetes | StorageClass backed by an encrypted CSI (EBS gp3, GCP-PD with CMEK, OpenEBS LocalPV with LUKS). |
| Backup | When exporting Qdrant snapshots, encrypt them with `age` or GPG before they leave the cluster. |

If your collection contains user-identifiable embeddings (e.g. uploader
fingerprints), treat the disk as PII — auditors will.

### Redis (chat memory + Hangfire queue cache)

Two layers:

1. **At rest** — back the `redis_data` volume on the same encrypted device
   as Postgres (LUKS / EBS encryption). Redis writes RDB + AOF; both need
   the disk treatment.
2. **In transit** — enable TLS between Gateway/Agent and Redis (`tls-port`
   in `redis.conf`, `rediss://` URL in the agent's `REDIS_URL`). Even within
   a compose network we recommend TLS once you cross VM boundaries.

Memory is held in cleartext while Redis is running. Treat it as PII.

### Object storage (MinIO / S3 — staged uploads + future raw PDFs)

- **MinIO**: configure server-side encryption with `MINIO_KMS_SECRET_KEY`
  pointing at a KES (KMS-as-a-Service) instance. SSE-S3 is the minimum;
  prefer SSE-KMS so per-object keys are auditable.
- **AWS S3 / GCS**: use SSE-KMS with a customer-managed key. Enable
  default-encryption at the bucket level so misconfigured uploaders cannot
  store cleartext objects.
- **Bucket policies** must also deny `s3:PutObject` without a
  `x-amz-server-side-encryption` header — belt and suspenders.

The Hangfire upload staging dir (`gateway_uploads` volume) is the same
sensitivity tier as raw PDFs — encrypt the underlying disk.

---

## Backup encryption

Daily snapshots, WAL archives, and offsite copies must each be encrypted.

| Tool | Recommendation |
|------|----------------|
| `pg_basebackup` + WAL-G | Use `WALG_LIBSODIUM_KEY` for streaming encryption to object storage. |
| `borgbackup` | Native repokey-blake2 mode; passphrase from KMS, not from disk. |
| `restic` | `RESTIC_PASSWORD_COMMAND` reads from KMS/Vault on demand. |
| Synology NAS | Encrypted shared folder (AES-256). Disable plaintext NFS once enabled. |

---

## Key management

A single KMS is mandatory for all of the above. Recommendations:

- **Self-hosted:** HashiCorp Vault with the Transit engine. Mount it as the
  audit-keyed backend so every decrypt is logged to `audit_log`.
- **Cloud:** AWS KMS or Google Cloud KMS with customer-managed keys. Grant
  per-environment key aliases via IAM, never `kms:*`.
- Rotate keys **annually** at minimum; quarterly if regulated.
- Document the **break-glass procedure** for losing a key — Postgres
  storage encryption uses a derived key, so loss of the KMS root means
  permanent data loss. Don't skip this drill.

---

## Verification checklist (per environment)

Before promoting an environment to production:

- [ ] `lsblk -o NAME,FSTYPE,MOUNTPOINT,TYPE` shows `crypt` for every
      AIQ-related mount point.
- [ ] Storage class / EBS volume describes itself as `encrypted: true`
      with the expected KMS key.
- [ ] Backups in object storage are listed with `ServerSideEncryption ≠ NONE`.
- [ ] KMS audit log shows expected services and no unknown principals.
- [ ] Disaster-recovery runbook references the right key alias.

---

## What we do **not** do

- We do not implement column-level encryption in Postgres for Phase 1–3.
  When the Phase 4 PII expansion lands, revisit `pgcrypto` + envelope
  encryption for fields tagged `sensitive=true` in the schema.
- We do not encrypt logs in transit beyond TLS. Loki's storage is covered
  by the disk-level encryption above.
- We do not roll our own crypto.

---

## References

- PostgreSQL 16: [Encryption Options](https://www.postgresql.org/docs/current/encryption-options.html)
- Qdrant: [Security overview](https://qdrant.tech/documentation/guides/security/)
- Redis: [TLS support](https://redis.io/docs/manual/security/encryption/)
- HashiCorp Vault: [Transit secrets engine](https://developer.hashicorp.com/vault/docs/secrets/transit)
