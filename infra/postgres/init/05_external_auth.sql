-- External identity link (Clerk, future Auth0/Cognito, etc.). One user can
-- be linked to at most one external provider account at a time — extend to
-- a separate user_external_logins table if multi-provider per user becomes
-- a requirement.

ALTER TABLE users
    ADD COLUMN IF NOT EXISTS external_provider TEXT,
    ADD COLUMN IF NOT EXISTS external_id       TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS uniq_users_external
    ON users(external_provider, external_id)
    WHERE external_provider IS NOT NULL;
