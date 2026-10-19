# Seed: Sáu Trực accounting demo data

Dedicated, repeatable demo-data seed for the "Sáu Trực" tenant's Accounting
Agent (Trợ lý kế toán). Writes through the real Gateway repository classes
(`ICustomerRepository`, `ISupplierRepository`, `IVoucherRepository`,
`IContractRepository`) — the same code the Gateway API itself uses — so
every row is indistinguishable from one created through the real product.

- Target tenant is resolved from `tenant_company_profiles.tax_code =
  '0123456789'` — never a hard-coded tenant id.
- Idempotent: re-running creates nothing new (natural-key upserts +
  voucher_id-scoped idempotent AR/AP application, both built into the
  repository layer itself).
- Marker: every demo customer/supplier code is prefixed `DEMO-`, every demo
  voucher's `document_number` is prefixed `DEMO-`, and every demo
  voucher/contract carries the tag `sau-truc-accounting-demo`.

## Run

```bash
docker run --rm --network aiq-agentic_aiq-net \
  -v "$(pwd)":/src -w /src/scripts/seed-demo-accounting \
  -e POSTGRES_HOST=postgres -e POSTGRES_PASSWORD='<postgres password>' \
  -e POSTGRES_USER=aiq -e POSTGRES_DB=aiq \
  mcr.microsoft.com/dotnet/sdk:9.0 dotnet run
```

(from the repo root; adjust the network name / Postgres credentials to match
your compose stack — see `docker-compose.yml`).

## Clean up

```bash
docker exec -i aiq-postgres psql -U aiq -d aiq < cleanup.sql
```

Deletes only rows carrying the `DEMO-` code prefix / `sau-truc-accounting-demo`
tag for the tenant resolved the same way — never touches any other tenant or
any non-demo data.
