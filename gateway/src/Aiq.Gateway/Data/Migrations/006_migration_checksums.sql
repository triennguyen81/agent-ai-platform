-- Establishes checksum storage for deploy-time migrations. DatabaseMigrator
-- backfills 001-005 from the currently packaged files before applying this
-- migration. That establishes a baseline; it does not attest to the historical
-- contents used when those migrations were originally applied.
ALTER TABLE schema_migrations
    ADD COLUMN IF NOT EXISTS checksum TEXT;

ALTER TABLE schema_migrations
    DROP CONSTRAINT IF EXISTS schema_migrations_checksum_format;
ALTER TABLE schema_migrations
    ADD CONSTRAINT schema_migrations_checksum_format
    CHECK (checksum IS NULL OR checksum ~ '^[0-9a-f]{64}$');

ALTER TABLE schema_migrations
    ALTER COLUMN checksum SET NOT NULL;

COMMENT ON COLUMN schema_migrations.checksum IS
    'SHA-256 of the packaged migration file. Pre-006 rows were baselined from packaged files during the 006 upgrade.';
