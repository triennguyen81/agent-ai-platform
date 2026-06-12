-- ----------------------------------------------------------------------------
-- Role migration — adds the 3-tier role model (super_admin / admin / user)
--
-- Idempotent: safe to run multiple times. Existing roles are left intact
-- (reviewer/uploader/viewer kept as legacy, do not surface in UI).
--
-- For an existing prod DB (data already loaded), run manually:
--   docker exec -i aiq-postgres psql -U aiq -d aiq < 06_role_migration.sql
-- ----------------------------------------------------------------------------

-- 1. Ensure new roles exist
INSERT INTO roles (name, description) VALUES
    ('super_admin', 'Top-level administrator: manages users and roles, full system access'),
    ('admin',       'Knowledge contributor: can add and edit notes / KB content'),
    ('user',        'Knowledge consumer: can chat using the KB')
ON CONFLICT (name) DO NOTHING;

-- 2. Promote any user who currently has the "admin" role to also have
--    "super_admin" — so the original founder account doesn't lose access
--    when /admin/users tightens its policy to super_admin only.
INSERT INTO user_roles (user_id, role_id)
SELECT ur.user_id, r_new.id
  FROM user_roles ur
  JOIN roles r_old ON r_old.id = ur.role_id AND r_old.name = 'admin'
  JOIN roles r_new ON r_new.name = 'super_admin'
ON CONFLICT DO NOTHING;

-- 3. Grant every existing active user the baseline "user" role so chat
--    works for them after the auth refactor (default for new signups).
INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
  FROM users u
 CROSS JOIN roles r
 WHERE u.is_active = TRUE
   AND r.name = 'user'
ON CONFLICT DO NOTHING;

-- 4. Sanity check — print role membership counts
SELECT r.name AS role, COUNT(ur.user_id) AS users_with_role
  FROM roles r
  LEFT JOIN user_roles ur ON ur.role_id = r.id
 GROUP BY r.name
 ORDER BY r.name;
