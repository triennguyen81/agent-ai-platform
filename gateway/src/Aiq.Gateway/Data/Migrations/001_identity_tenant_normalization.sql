-- Normalize login identity while retaining lowercase values for compatibility.
ALTER TABLE users
    ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES tenants(id) ON DELETE RESTRICT;

-- Prefer the earliest active membership as the one tenant for admin/user.
UPDATE users u
SET tenant_id = (
    SELECT tm.tenant_id
    FROM tenant_members tm
    JOIN tenants t ON t.id = tm.tenant_id AND t.status = 'active'
    WHERE tm.user_id = u.id
    ORDER BY tm.joined_at, tm.tenant_id
    LIMIT 1
)
WHERE u.tenant_id IS NULL;

UPDATE users u
SET tenant_id = (SELECT id FROM tenants WHERE code = 'default')
WHERE u.tenant_id IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM user_roles ur JOIN roles r ON r.id = ur.role_id
      WHERE ur.user_id = u.id AND r.name = 'super_admin'
  );

-- Retain exactly one active super_admin deterministically. Additional historic
-- super_admin grants become tenant admins; no user or business data is deleted.
WITH ranked AS (
    SELECT ur.user_id,
           row_number() OVER (ORDER BY u.created_at, u.id) AS ordinal
    FROM user_roles ur
    JOIN roles r ON r.id = ur.role_id AND r.name = 'super_admin'
    JOIN users u ON u.id = ur.user_id AND u.is_active
), extras AS (
    SELECT user_id FROM ranked WHERE ordinal > 1
)
DELETE FROM user_roles ur
USING extras e, roles r
WHERE ur.user_id = e.user_id
  AND ur.role_id = r.id
  AND r.name = 'super_admin';

-- One login role per user, using highest privilege. Legacy login-role grants
-- are removed from users but role catalog rows remain for historical FKs/data.
WITH ranked AS (
    SELECT ur.user_id, ur.role_id,
           row_number() OVER (
               PARTITION BY ur.user_id
               ORDER BY CASE r.name
                   WHEN 'super_admin' THEN 1
                   WHEN 'admin' THEN 2
                   WHEN 'user' THEN 3
                   ELSE 4
               END, ur.role_id
           ) AS ordinal,
           r.name
    FROM user_roles ur
    JOIN roles r ON r.id = ur.role_id
)
DELETE FROM user_roles ur
USING ranked x
WHERE ur.user_id = x.user_id
  AND ur.role_id = x.role_id
  AND (x.ordinal > 1 OR x.name NOT IN ('super_admin', 'admin', 'user'));

-- Preserve access for active legacy-role-only accounts by converting them to
-- the least-privileged supported role inside their backfilled tenant.
INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
CROSS JOIN roles r
WHERE r.name = 'user'
  AND u.is_active
  AND u.tenant_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM user_roles ur WHERE ur.user_id = u.id);

-- Super admin is system-scoped. Tenant users retain one compatibility
-- membership matching users.tenant_id; all other memberships are removed.
UPDATE users u SET tenant_id = NULL
WHERE EXISTS (
    SELECT 1 FROM user_roles ur JOIN roles r ON r.id = ur.role_id
    WHERE ur.user_id = u.id AND r.name = 'super_admin'
);

DELETE FROM tenant_members tm
USING users u
WHERE tm.user_id = u.id
  AND (u.tenant_id IS NULL OR tm.tenant_id <> u.tenant_id);

INSERT INTO tenant_members (tenant_id, user_id, role)
SELECT u.tenant_id, u.id,
       CASE r.name WHEN 'admin' THEN 'tenant_admin' ELSE 'member' END
FROM users u
JOIN user_roles ur ON ur.user_id = u.id
JOIN roles r ON r.id = ur.role_id
WHERE u.tenant_id IS NOT NULL
ON CONFLICT (tenant_id, user_id) DO NOTHING;

UPDATE tenant_members tm
SET role = CASE r.name WHEN 'admin' THEN 'tenant_admin' ELSE 'member' END
FROM users u
JOIN user_roles ur ON ur.user_id = u.id
JOIN roles r ON r.id = ur.role_id
WHERE tm.user_id = u.id AND tm.tenant_id = u.tenant_id;

CREATE UNIQUE INDEX IF NOT EXISTS uq_user_single_login_role ON user_roles(user_id);
CREATE INDEX IF NOT EXISTS idx_users_tenant_active ON users(tenant_id, is_active)
    WHERE tenant_id IS NOT NULL;

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
    ELSE
        SELECT name INTO target_role FROM roles WHERE id = NEW.role_id;
        IF target_role NOT IN ('super_admin', 'admin', 'user') THEN
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
            RAISE EXCEPTION '% must belong to one tenant', target_role USING ERRCODE = '23514';
        END IF;
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_enforce_login_identity ON user_roles;
CREATE TRIGGER trg_enforce_login_identity
BEFORE INSERT OR UPDATE OR DELETE ON user_roles
FOR EACH ROW EXECUTE FUNCTION enforce_login_identity();

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
    IF current_role IN ('admin', 'user') AND NEW.tenant_id IS NULL THEN
        RAISE EXCEPTION '% must belong to one tenant', current_role USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_enforce_user_identity_state ON users;
CREATE TRIGGER trg_enforce_user_identity_state
BEFORE UPDATE OF tenant_id, is_active ON users
FOR EACH ROW EXECUTE FUNCTION enforce_user_identity_state();
