# PostgreSQL bootstrap baseline

Files in this directory run only when PostgreSQL initializes an empty data volume.
They provide extensions and the legacy baseline required by Gateway migration 001.

Persistent schema changes after this baseline belong in
`gateway/src/Aiq.Gateway/Data/Migrations` and are ordered and checksummed by
`DatabaseMigrator`. Do not add demo tenants, users, simulator rows, or later
application schema here.

Demo data belongs in `scripts/` and must be applied explicitly after Gateway
migrations and tenant onboarding have completed.
