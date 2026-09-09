-- Phase D1: Department domain foundation only.
--
-- This migration is deliberately additive. It creates one active default
-- department for every existing tenant and attaches existing ADMIN/USER
-- accounts to it. SUPER_ADMIN and TENANT_OWNER remain departmentless.
-- Authorization remains tenant-wide until D2.

CREATE TABLE IF NOT EXISTS departments (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    code        TEXT NOT NULL,
    name        TEXT NOT NULL,
    status      TEXT NOT NULL DEFAULT 'active'
                CHECK (status IN ('active', 'inactive')),
    is_default  BOOLEAN NOT NULL DEFAULT FALSE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT departments_code_not_blank CHECK (btrim(code) <> ''),
    CONSTRAINT departments_name_not_blank CHECK (btrim(name) <> ''),
    CONSTRAINT departments_default_is_active CHECK (NOT is_default OR status = 'active'),
    CONSTRAINT uq_departments_id_tenant UNIQUE (id, tenant_id),
    CONSTRAINT uq_departments_tenant_code UNIQUE (tenant_id, code),
    CONSTRAINT uq_departments_tenant_name UNIQUE (tenant_id, name)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_departments_one_default_per_tenant
    ON departments(tenant_id)
    WHERE is_default;

CREATE INDEX IF NOT EXISTS idx_departments_tenant_status
    ON departments(tenant_id, status);

CREATE OR REPLACE FUNCTION departments_touch_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_departments_updated_at ON departments;
CREATE TRIGGER trg_departments_updated_at
    BEFORE UPDATE ON departments
    FOR EACH ROW EXECUTE FUNCTION departments_touch_updated_at();

ALTER TABLE users
    ADD COLUMN IF NOT EXISTS department_id UUID;

-- The tenant/code uniqueness makes this safe if the SQL is deliberately
-- exercised more than once outside DatabaseMigrator. The partial unique
-- index independently prevents two default departments for one tenant.
INSERT INTO departments (tenant_id, code, name, status, is_default)
SELECT t.id, 'default', 'Default Department', 'active', TRUE
FROM tenants t
ON CONFLICT (tenant_id, code) DO UPDATE
SET is_default = TRUE,
    status = 'active',
    updated_at = NOW();

-- Explicit precedence: super_admin > tenant_owner > admin > user.
-- Although the current database permits exactly one login role per user,
-- the negative predicates make the classification safe for legacy data.
UPDATE users u
SET department_id = NULL,
    updated_at = NOW()
WHERE EXISTS (
    SELECT 1
    FROM user_roles ur
    JOIN roles r ON r.id = ur.role_id
    WHERE ur.user_id = u.id
      AND r.name IN ('super_admin', 'tenant_owner')
);

UPDATE users u
SET department_id = d.id,
    updated_at = NOW()
FROM departments d
WHERE d.tenant_id = u.tenant_id
  AND d.is_default
  AND EXISTS (
      SELECT 1
      FROM user_roles ur
      JOIN roles r ON r.id = ur.role_id
      WHERE ur.user_id = u.id
        AND r.name IN ('admin', 'user')
  )
  AND NOT EXISTS (
      SELECT 1
      FROM user_roles ur
      JOIN roles r ON r.id = ur.role_id
      WHERE ur.user_id = u.id
        AND r.name IN ('super_admin', 'tenant_owner')
  );

-- A composite FK is the canonical same-tenant invariant. MATCH SIMPLE keeps
-- department_id nullable for staged rollout and for system/owner accounts.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'users'::regclass
          AND conname = 'users_department_tenant_fkey'
    ) THEN
        ALTER TABLE users
            ADD CONSTRAINT users_department_tenant_fkey
            FOREIGN KEY (department_id, tenant_id)
            REFERENCES departments(id, tenant_id)
            ON DELETE RESTRICT;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_users_tenant_department_active
    ON users(tenant_id, department_id, is_active)
    WHERE department_id IS NOT NULL;

COMMENT ON TABLE departments IS
    'Tenant-owned security-scope entities. Department authorization begins in D2.';
COMMENT ON COLUMN users.department_id IS
    'Canonical nullable department membership. D1 backfills ADMIN/USER; mandatory role-aware enforcement begins in D2.';
