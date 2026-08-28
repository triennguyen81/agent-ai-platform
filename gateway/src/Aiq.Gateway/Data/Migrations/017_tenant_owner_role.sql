-- Tenant Owner RBAC — Stage 1: role support only.
--
-- Adds `tenant_owner` as a legal login role and teaches the existing
-- identity triggers (from 001_identity_tenant_normalization.sql) about it.
-- Deliberately does NOT:
--   * assign the role to anyone (no tenant->owner mapping — that is an
--     explicit, audited, application-level action in Stage 2, never baked
--     into an immutable migration),
--   * enforce "exactly one active tenant_owner per tenant" (Stage 3, once
--     Stage 2 has actually produced clean, complete ownership data to
--     enforce against),
--   * touch the `tenants` table at all.
-- Every existing ADMIN keeps the `admin` role exactly as-is.

INSERT INTO roles (name, description)
SELECT 'tenant_owner', 'Sole accountable owner of one tenant — created only via explicit SUPER_ADMIN assignment'
WHERE NOT EXISTS (SELECT 1 FROM roles WHERE name = 'tenant_owner');

CREATE OR REPLACE FUNCTION enforce_login_identity()
RETURNS TRIGGER AS $$
DECLARE
    target_user UUID := COALESCE(NEW.user_id, OLD.user_id);
    target_role TEXT;
    target_tenant UUID;
BEGIN
    IF TG_OP = 'DELETE' THEN
        SELECT name INTO target_role FROM roles WHERE id = OLD.role_id;
        IF target_role = 'super_admin'
           AND (SELECT is_active FROM users WHERE id = OLD.user_id)
           AND NOT EXISTS (
               SELECT 1 FROM user_roles ur JOIN roles r ON r.id = ur.role_id
               JOIN users u ON u.id = ur.user_id
               WHERE r.name = 'super_admin' AND u.is_active AND ur.user_id <> OLD.user_id
           ) THEN
            RAISE EXCEPTION 'the sole active super_admin cannot be removed' USING ERRCODE = '23514';
        END IF;
        -- Stage 3 adds the equivalent "sole active tenant_owner" DELETE guard,
        -- once per-tenant uniqueness is actually enforced.
    ELSE
        SELECT name INTO target_role FROM roles WHERE id = NEW.role_id;
        IF target_role NOT IN ('super_admin', 'tenant_owner', 'admin', 'user') THEN
            RAISE EXCEPTION 'unsupported login role: %', target_role USING ERRCODE = '23514';
        END IF;
        SELECT tenant_id INTO target_tenant FROM users WHERE id = target_user;
        IF target_role = 'super_admin' THEN
            IF target_tenant IS NOT NULL THEN
                RAISE EXCEPTION 'super_admin must not belong to a tenant' USING ERRCODE = '23514';
            END IF;
            PERFORM pg_advisory_xact_lock(731991);
            IF EXISTS (
                SELECT 1 FROM user_roles ur JOIN roles r ON r.id = ur.role_id
                JOIN users u ON u.id = ur.user_id
                WHERE r.name = 'super_admin' AND u.is_active AND ur.user_id <> target_user
            ) THEN
                RAISE EXCEPTION 'exactly one active super_admin is allowed' USING ERRCODE = '23505';
            END IF;
        ELSIF target_tenant IS NULL THEN
            -- Covers tenant_owner, admin, and user alike: all three must
            -- belong to exactly one tenant. Stage 3 layers the additional
            -- "exactly one active tenant_owner per tenant" rule on top of
            -- this same branch once ownership data is verified complete.
            RAISE EXCEPTION '% must belong to one tenant', target_role USING ERRCODE = '23514';
        END IF;
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION enforce_user_identity_state()
RETURNS TRIGGER AS $$
DECLARE current_role TEXT;
BEGIN
    SELECT r.name INTO current_role
    FROM user_roles ur JOIN roles r ON r.id = ur.role_id
    WHERE ur.user_id = NEW.id;
    IF current_role = 'super_admin' AND (NOT NEW.is_active OR NEW.tenant_id IS NOT NULL) THEN
        RAISE EXCEPTION 'the active super_admin is system-scoped and cannot be disabled' USING ERRCODE = '23514';
    END IF;
    IF current_role IN ('tenant_owner', 'admin', 'user') AND NEW.tenant_id IS NULL THEN
        RAISE EXCEPTION '% must belong to one tenant', current_role USING ERRCODE = '23514';
    END IF;
    -- Stage 3 adds: a tenant_owner who is the sole active owner of their
    -- tenant cannot be disabled without an atomic successor already in
    -- place — deferred until ownership data is complete and verified.
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
