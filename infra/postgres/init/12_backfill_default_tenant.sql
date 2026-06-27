-- ----------------------------------------------------------------------------
-- G1 — Backfill default tenant + add tenant_id to existing tables
--
-- Strategy:
--   1. Create the 'default' tenant
--   2. Add every existing user as tenant_admin of default
--   3. Add tenant_id NULLABLE to data tables (contracts, notes, books,
--      audit_log, notifications, contract_clauses, contract_checklist_items)
--   4. Backfill all existing rows to default tenant
--   5. Set tenant_id NOT NULL after backfill
--   6. Create the 'contract-monitor' agent in default tenant — gives the
--      existing Contract Assistant an identity in the new framework.
--
-- Idempotent: re-running is safe (INSERT ... ON CONFLICT, ALTER ... IF NOT EXISTS).
-- ----------------------------------------------------------------------------

-- 1. Default tenant
INSERT INTO tenants (code, name, status, metadata)
VALUES ('default', 'AIQ Default Tenant', 'active',
        '{"backfill": true, "description": "Auto-created during G1 governance migration"}'::jsonb)
ON CONFLICT (code) DO NOTHING;

-- 2. All existing users → tenant_admin of default tenant
INSERT INTO tenant_members (tenant_id, user_id, role)
SELECT t.id, u.id, 'tenant_admin'
FROM tenants t, users u
WHERE t.code = 'default'
ON CONFLICT (tenant_id, user_id) DO NOTHING;

-- 3. Add tenant_id columns (NULLABLE first for safe backfill)
ALTER TABLE contracts                 ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE;
ALTER TABLE contract_clauses          ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE;
ALTER TABLE contract_checklist_items  ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE;
ALTER TABLE notes                     ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE;
ALTER TABLE books                     ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE;
ALTER TABLE notifications             ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE;
ALTER TABLE audit_log                 ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE;

-- 4. Backfill — every existing row → default tenant
UPDATE contracts                SET tenant_id = (SELECT id FROM tenants WHERE code='default') WHERE tenant_id IS NULL;
UPDATE contract_clauses         SET tenant_id = (SELECT id FROM tenants WHERE code='default') WHERE tenant_id IS NULL;
UPDATE contract_checklist_items SET tenant_id = (SELECT id FROM tenants WHERE code='default') WHERE tenant_id IS NULL;
UPDATE notes                    SET tenant_id = (SELECT id FROM tenants WHERE code='default') WHERE tenant_id IS NULL;
UPDATE books                    SET tenant_id = (SELECT id FROM tenants WHERE code='default') WHERE tenant_id IS NULL;
UPDATE notifications            SET tenant_id = (SELECT id FROM tenants WHERE code='default') WHERE tenant_id IS NULL;
UPDATE audit_log                SET tenant_id = (SELECT id FROM tenants WHERE code='default') WHERE tenant_id IS NULL;

-- 5. Now safe to enforce NOT NULL
ALTER TABLE contracts                 ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE contract_clauses          ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE contract_checklist_items  ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE notes                     ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE books                     ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE notifications             ALTER COLUMN tenant_id SET NOT NULL;
-- audit_log kept nullable (system events without tenant context)

-- 6. Composite indexes for tenant-scoped queries (most frequent access pattern)
CREATE INDEX IF NOT EXISTS idx_contracts_tenant_user      ON contracts(tenant_id, user_id);
CREATE INDEX IF NOT EXISTS idx_notes_tenant_user          ON notes(tenant_id, user_id);
CREATE INDEX IF NOT EXISTS idx_books_tenant               ON books(tenant_id, uploaded_by);
CREATE INDEX IF NOT EXISTS idx_notifications_tenant_user  ON notifications(tenant_id, user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_audit_log_tenant_time      ON audit_log(tenant_id, created_at DESC) WHERE tenant_id IS NOT NULL;

-- 7. Create the contract-monitor agent for the existing Contract Assistant.
--    Owner = first super_admin if any, otherwise NULL (admin can claim later).
INSERT INTO agents (
    tenant_id, code, name, type, description,
    lifecycle_state, owner_user_id, action_tier, config
)
SELECT
    t.id,
    'contract-monitor',
    'Contract Monitoring Assistant',
    'domain',
    'Extracts info from contract PDFs, generates checklists, scans deadlines. Backfilled from Phase 1+2+3 implementation.',
    'production',  -- already deployed and running, mark as production
    (SELECT u.id
     FROM users u
     JOIN user_roles ur ON ur.user_id = u.id
     JOIN roles r ON r.id = ur.role_id
     WHERE r.name = 'super_admin'
     ORDER BY u.created_at
     LIMIT 1),
    'execute_with_approval',
    '{"skills": ["contract-extractor", "contract-checklist-builder"], "model": "claude-haiku-4-5-20251001"}'::jsonb
FROM tenants t
WHERE t.code = 'default'
ON CONFLICT (tenant_id, code) DO NOTHING;

-- Grant the contract-monitor agent's owner an 'owner' ACL entry (idempotent)
INSERT INTO agent_acl (agent_id, principal_id, principal_type, access_level)
SELECT a.id, a.owner_user_id, 'user', 'owner'
FROM agents a
JOIN tenants t ON t.id = a.tenant_id
WHERE t.code = 'default' AND a.code = 'contract-monitor' AND a.owner_user_id IS NOT NULL
ON CONFLICT (agent_id, principal_id, principal_type) DO NOTHING;
