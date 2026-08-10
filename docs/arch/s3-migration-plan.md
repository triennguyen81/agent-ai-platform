# S3/MinIO Migration Plan — L1 Data & Infra gap

## Motivation

Currently `/var/aiq/uploads` = Docker named volume `gateway_uploads`. Files
(voucher images + PDF, contract PDFs) survive container rebuilds but:

- Not accessible outside the gateway container
- Not replicable / not backed up automatically
- Not addressable by URL (each file needs a proxy download endpoint)
- Not encrypted at rest
- Full disk = deploy blocker

Object storage (S3-compatible) fixes all of the above. MinIO ships as
one Docker container, wire-compatible with the AWS S3 SDK, so we can
run it locally on cosmos now and move to AWS/Cloudflare R2 later
without changing app code.

## Scope

**Phase 1 — Voucher uploads only** (smallest blast radius):
- Upload image/PDF via `POST /vouchers/upload` → write to
  MinIO bucket `voucher-uploads` instead of local disk
- Store `s3_key` on the `vouchers.source_file_path` field
- Detail page fetches signed URL (5-minute expiry) to render the image
- Retro-migrate the 3 existing rows from local volume to MinIO

**Deferred** — contracts, `bulk-md-upload`, book OCR uploads. Same
pattern once vouchers prove it out.

## Components

### 1. MinIO container (docker-compose.yml)

```yaml
minio:
  image: minio/minio:latest
  container_name: aiq-minio
  restart: unless-stopped
  command: server /data --console-address ":9001"
  environment:
    MINIO_ROOT_USER: ${MINIO_ROOT_USER}
    MINIO_ROOT_PASSWORD: ${MINIO_ROOT_PASSWORD}
  volumes:
    - minio_data:/data
  networks:
    - aiq-net
  # No host port for API (9000) — Gateway talks to it over the private
  # network. Console (9001) mapped to localhost only for admin.
  ports:
    - "127.0.0.1:9001:9001"
  healthcheck:
    test: ["CMD", "curl", "-f", "http://localhost:9000/minio/health/live"]
    interval: 30s
```

### 2. Bucket bootstrap

One-off init container or startup hook that runs `mc mb aiq/voucher-uploads`.

### 3. Gateway refactor

- Add `AWSSDK.S3` NuGet package (works with MinIO — set `ForcePathStyle=true`,
  point `ServiceURL` at `http://aiq-minio:9000`)
- New `MinioStorageService` implements a small `IObjectStorage` interface:
  - `PutObjectAsync(bucket, key, stream) → sha256`
  - `GetSignedUrlAsync(bucket, key, expirySeconds) → URL`
  - `DeleteObjectAsync(bucket, key)`
- Refactor `VoucherEndpoints.UploadAsync`:
  - Instead of `File.Create(stagedPath)`, `storage.PutObjectAsync("voucher-uploads", key, stream)`
  - Store `key` on `vouchers.source_file_path` (rename column to `source_object_key` in a later migration)
- Refactor `GetAsync` / detail page:
  - Web calls new endpoint `GET /vouchers/{id}/preview-url` → returns signed URL
  - `<img src=signedUrl>` — browser fetches directly from MinIO

### 4. Config

`.env` additions:
```
MINIO_ROOT_USER=aiq
MINIO_ROOT_PASSWORD=<random 32 chars>
S3_ENDPOINT_URL=http://aiq-minio:9000
S3_ACCESS_KEY=aiq
S3_SECRET_KEY=<same as root password OR create service account>
S3_BUCKET_VOUCHERS=voucher-uploads
S3_FORCE_PATH_STYLE=true
```

### 5. Migration script

One-off `scripts/migrate_local_uploads_to_s3.sh`:
```
for f in /var/aiq/uploads/voucher-*; do
    key="legacy/$(basename $f)"
    mc cp $f aiq/voucher-uploads/$key
    # update vouchers.source_file_path in Postgres
done
```

## Rollout order

1. Deploy MinIO container + bucket bootstrap (0 code changes) — verify healthy
2. Add IObjectStorage + MinioStorageService (behind feature flag)
3. Deploy gateway with flag OFF — no behavior change
4. Migrate existing 3 vouchers via script
5. Flip feature flag ON → new uploads go to MinIO
6. Serve preview URLs from MinIO for both old + new
7. Once stable ~1 week, decommission `gateway_uploads` volume

## Estimated timeline

- MinIO container + bootstrap: **0.5 day**
- Gateway IObjectStorage + refactor upload: **1 day**
- Web preview URL flow: **0.5 day**
- Migration script + rollout: **0.5 day**
- Test + docs: **0.5 day**
- **Total: 2-3 days**

## Rollback plan

Feature flag off + delete new endpoint. Files still on MinIO volume,
not deleted. Old local files still on `gateway_uploads` volume — untouched
until step 7. Full rollback = 5 minutes.

## Security notes

- MinIO API port (9000) NOT exposed to host — only reachable inside
  Docker network by containers on `aiq-net`
- Console port (9001) bound to `127.0.0.1` only, SSH-tunnel for admin
- Root password stored in `.env` (already chmod 600 post-incident)
- Signed URLs expire in 5 min — no permanent public URLs
- MinIO server-side encryption (SSE-S3) can be enabled later if needed
