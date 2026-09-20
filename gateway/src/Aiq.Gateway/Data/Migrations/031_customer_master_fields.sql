-- Customer module completion: sim_customers is the canonical Customer
-- Master (see docs/CUSTOMER_MODULE_TODO.md's decision) — this adds the
-- remaining fields needed for real create/edit/lifecycle instead of a
-- read-only simulator view, plus a lightweight audit trail.

ALTER TABLE sim_customers
  ADD COLUMN IF NOT EXISTS customer_type TEXT NOT NULL DEFAULT 'company',
  ADD COLUMN IF NOT EXISTS representative TEXT,
  ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'active',
  ADD COLUMN IF NOT EXISTS notes TEXT,
  ADD COLUMN IF NOT EXISTS created_by UUID REFERENCES users(id) ON DELETE SET NULL,
  ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

DO $$ BEGIN
  ALTER TABLE sim_customers ADD CONSTRAINT chk_sim_customers_customer_type
    CHECK (customer_type IN ('company','individual'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
  ALTER TABLE sim_customers ADD CONSTRAINT chk_sim_customers_status
    CHECK (status IN ('active','inactive'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE INDEX IF NOT EXISTS idx_sim_customers_status ON sim_customers(tenant_id,department_id,status);

CREATE TABLE IF NOT EXISTS customer_history (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  department_id UUID NOT NULL,
  customer_id UUID NOT NULL REFERENCES sim_customers(id) ON DELETE CASCADE,
  actor_id UUID REFERENCES users(id) ON DELETE SET NULL,
  event_type TEXT NOT NULL,
  details JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_customer_history_customer ON customer_history(customer_id, created_at DESC);
