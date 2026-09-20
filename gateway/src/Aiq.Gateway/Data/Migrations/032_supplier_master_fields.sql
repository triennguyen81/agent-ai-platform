-- Supplier module completion: sim_suppliers is the canonical Supplier
-- Master (see docs/SUPPLIER_MODULE_TODO.md's decision) — this adds the
-- remaining fields needed for real create/edit/lifecycle instead of a
-- read-only simulator view, plus a lightweight audit trail. Mirrors
-- migration 031 (Customer) — no DB-level UNIQUE on mst here (see TODO doc:
-- sim_suppliers never had one, unlike sim_customers, and adding one now
-- risks failing on legacy duplicate data).

ALTER TABLE sim_suppliers
  ADD COLUMN IF NOT EXISTS supplier_type TEXT NOT NULL DEFAULT 'company',
  ADD COLUMN IF NOT EXISTS representative TEXT,
  ADD COLUMN IF NOT EXISTS phone TEXT,
  ADD COLUMN IF NOT EXISTS email TEXT,
  ADD COLUMN IF NOT EXISTS address TEXT,
  ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'active',
  ADD COLUMN IF NOT EXISTS notes TEXT,
  ADD COLUMN IF NOT EXISTS created_by UUID REFERENCES users(id) ON DELETE SET NULL,
  ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

DO $$ BEGIN
  ALTER TABLE sim_suppliers ADD CONSTRAINT chk_sim_suppliers_supplier_type
    CHECK (supplier_type IN ('company','individual'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
  ALTER TABLE sim_suppliers ADD CONSTRAINT chk_sim_suppliers_status
    CHECK (status IN ('active','inactive'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE INDEX IF NOT EXISTS idx_sim_suppliers_status ON sim_suppliers(tenant_id,department_id,status);

CREATE TABLE IF NOT EXISTS supplier_history (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  department_id UUID NOT NULL,
  supplier_id UUID NOT NULL REFERENCES sim_suppliers(id) ON DELETE CASCADE,
  actor_id UUID REFERENCES users(id) ON DELETE SET NULL,
  event_type TEXT NOT NULL,
  details JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_supplier_history_supplier ON supplier_history(supplier_id, created_at DESC);
