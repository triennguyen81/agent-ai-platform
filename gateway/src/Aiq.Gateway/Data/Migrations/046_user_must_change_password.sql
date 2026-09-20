-- Password-management: self-service change-password and privileged
-- reset-password (see Endpoints/UserEndpoints.cs, Endpoints/MeEndpoints.cs).
--
-- must_change_password is a soft, app-layer flag only — it is surfaced via
-- /me and the login response so the web tier can prompt the user, but is
-- NOT enforced by the authentication pipeline itself (TenantContextMiddleware
-- stays untouched). A hard server-side lockout would need every protected
-- route to special-case an exception for the change-password endpoint,
-- which is a materially bigger change to the shared auth path than this
-- feature warrants.
ALTER TABLE users
    ADD COLUMN IF NOT EXISTS must_change_password BOOLEAN NOT NULL DEFAULT FALSE;
