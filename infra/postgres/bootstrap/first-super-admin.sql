\set ON_ERROR_STOP on

BEGIN;
SELECT pg_advisory_xact_lock(731991);

CREATE TEMP TABLE bootstrap_super_admin_target ON COMMIT DROP AS
SELECT id
FROM users
WHERE lower(email) = lower(:'email')
  AND is_active;

DO $$
BEGIN
    IF (SELECT count(*) FROM bootstrap_super_admin_target) <> 1 THEN
        RAISE EXCEPTION 'bootstrap email must identify exactly one active registered user';
    END IF;
    IF EXISTS (
        SELECT 1 FROM users u
        JOIN user_roles ur ON ur.user_id = u.id
        JOIN roles r ON r.id = ur.role_id
        WHERE u.is_active AND r.name = 'super_admin'
    ) THEN
        RAISE EXCEPTION 'an active super_admin already exists';
    END IF;
END $$;

DELETE FROM tenant_members
WHERE user_id = (SELECT id FROM bootstrap_super_admin_target);

DELETE FROM user_roles
WHERE user_id = (SELECT id FROM bootstrap_super_admin_target);

-- D1/D2 added users.department_id, and DepartmentAccessPolicy.ValidateMembership
-- fails closed (403) unless a super_admin/tenant_owner is departmentless — a
-- registered user promoted here may already carry the default tenant's
-- default department_id from self-registration, so it must be cleared too,
-- not just tenant_id.
UPDATE users
SET tenant_id = NULL,
    department_id = NULL,
    updated_at = NOW()
WHERE id = (SELECT id FROM bootstrap_super_admin_target);

INSERT INTO user_roles (user_id, role_id)
SELECT target.id, role_row.id
FROM bootstrap_super_admin_target target
CROSS JOIN roles role_row
WHERE role_row.name = 'super_admin';

COMMIT;
