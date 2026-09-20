-- Normalize operational business data to ACL V2 Department ownership.
-- Legacy rows follow their authenticated creator where one exists; simulator
-- master data is assigned to the tenant's default Department. No browser
-- payload can set these fields.

ALTER TABLE vouchers ADD COLUMN IF NOT EXISTS department_id UUID;
UPDATE vouchers v SET department_id=u.department_id FROM users u
WHERE v.user_id=u.id AND v.tenant_id=u.tenant_id AND v.department_id IS NULL;
ALTER TABLE vouchers DROP CONSTRAINT IF EXISTS fk_vouchers_department_tenant;
ALTER TABLE vouchers ADD CONSTRAINT fk_vouchers_department_tenant
  FOREIGN KEY(department_id,tenant_id) REFERENCES departments(id,tenant_id);
CREATE INDEX IF NOT EXISTS idx_vouchers_department ON vouchers(tenant_id,department_id,created_at DESC);

ALTER TABLE sim_customers ADD COLUMN IF NOT EXISTS department_id UUID;
ALTER TABLE sim_suppliers ADD COLUMN IF NOT EXISTS department_id UUID;
UPDATE sim_customers r SET department_id=d.id FROM departments d
WHERE r.tenant_id=d.tenant_id AND d.is_default AND r.department_id IS NULL;
UPDATE sim_suppliers r SET department_id=d.id FROM departments d
WHERE r.tenant_id=d.tenant_id AND d.is_default AND r.department_id IS NULL;
ALTER TABLE sim_customers DROP CONSTRAINT IF EXISTS fk_sim_customers_department_tenant;
ALTER TABLE sim_customers ADD CONSTRAINT fk_sim_customers_department_tenant
  FOREIGN KEY(department_id,tenant_id) REFERENCES departments(id,tenant_id);
ALTER TABLE sim_suppliers DROP CONSTRAINT IF EXISTS fk_sim_suppliers_department_tenant;
ALTER TABLE sim_suppliers ADD CONSTRAINT fk_sim_suppliers_department_tenant
  FOREIGN KEY(department_id,tenant_id) REFERENCES departments(id,tenant_id);
CREATE INDEX IF NOT EXISTS idx_sim_customers_department ON sim_customers(tenant_id,department_id,name);
CREATE INDEX IF NOT EXISTS idx_sim_suppliers_department ON sim_suppliers(tenant_id,department_id,name);
