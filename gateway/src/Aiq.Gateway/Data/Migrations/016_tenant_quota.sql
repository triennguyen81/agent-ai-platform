-- Phase 7 closure: minimal real tenant quota. NULL = unlimited. Counts are
-- derived at read time from existing tables (users.tenant_id,
-- agent_tenant_assignments) rather than duplicated/cached, so there is
-- nothing to keep in sync.
ALTER TABLE tenants
  ADD COLUMN IF NOT EXISTS max_users  integer,
  ADD COLUMN IF NOT EXISTS max_agents integer;

ALTER TABLE tenants
  ADD CONSTRAINT tenants_max_users_nonnegative  CHECK (max_users  IS NULL OR max_users  >= 0),
  ADD CONSTRAINT tenants_max_agents_nonnegative CHECK (max_agents IS NULL OR max_agents >= 0);
