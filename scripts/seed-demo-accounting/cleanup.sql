-- Removes ONLY the "sau-truc-accounting-demo" demo data created by
-- Program.cs in this directory — scoped by the tenant resolved from
-- tenant_company_profiles (tax_code below), never a hard-coded tenant id,
-- and by the DEMO-/demo tag markers so no other tenant's or non-demo data
-- can ever be touched even if this script is run against a shared DB.
--
-- Run (from repo root):
--   docker exec -i aiq-postgres psql -U aiq -d aiq < scripts/seed-demo-accounting/cleanup.sql

BEGIN;

DO $$
DECLARE
    v_tenant_id UUID;
BEGIN
    SELECT tenant_id INTO v_tenant_id FROM tenant_company_profiles WHERE tax_code = '0123456789';
    IF v_tenant_id IS NULL THEN
        RAISE EXCEPTION 'No tenant found for tax_code=0123456789 — nothing to clean up, aborting.';
    END IF;

    -- 0. Receipt/payment settlement rows (migration 051) for the demo
    --    customers/suppliers. They reference ledger rows, vouchers and each
    --    other with RESTRICT FKs, so unlink then delete in dependency order.
    --    Explicit demo teardown only — the product itself never deletes these.
    UPDATE sim_ar_ledger SET payment_application_id = NULL
    WHERE tenant_id = v_tenant_id
      AND customer_id IN (SELECT id FROM sim_customers WHERE tenant_id = v_tenant_id AND customer_code LIKE 'DEMO-%');
    UPDATE sim_ap_ledger SET payment_application_id = NULL
    WHERE tenant_id = v_tenant_id
      AND supplier_id IN (SELECT id FROM sim_suppliers WHERE tenant_id = v_tenant_id AND supplier_code LIKE 'DEMO-%');
    DELETE FROM payment_applications
    WHERE tenant_id = v_tenant_id
      AND cash_transaction_id IN (
        SELECT id FROM cash_transactions WHERE tenant_id = v_tenant_id AND (
          customer_id IN (SELECT id FROM sim_customers WHERE tenant_id = v_tenant_id AND customer_code LIKE 'DEMO-%')
          OR supplier_id IN (SELECT id FROM sim_suppliers WHERE tenant_id = v_tenant_id AND supplier_code LIKE 'DEMO-%')));
    DELETE FROM voucher_journal_suggestions
    WHERE cash_transaction_id IN (
        SELECT id FROM cash_transactions WHERE tenant_id = v_tenant_id AND (
          customer_id IN (SELECT id FROM sim_customers WHERE tenant_id = v_tenant_id AND customer_code LIKE 'DEMO-%')
          OR supplier_id IN (SELECT id FROM sim_suppliers WHERE tenant_id = v_tenant_id AND supplier_code LIKE 'DEMO-%')));
    DELETE FROM cash_transactions
    WHERE tenant_id = v_tenant_id AND (
      customer_id IN (SELECT id FROM sim_customers WHERE tenant_id = v_tenant_id AND customer_code LIKE 'DEMO-%')
      OR supplier_id IN (SELECT id FROM sim_suppliers WHERE tenant_id = v_tenant_id AND supplier_code LIKE 'DEMO-%'));

    -- 1. AR/AP ledger rows for the demo customers/suppliers (must go before
    --    the customers/suppliers themselves — sim_ar_ledger/sim_ap_ledger's
    --    FKs are RESTRICT, not CASCADE).
    DELETE FROM sim_ar_ledger
    WHERE tenant_id = v_tenant_id
      AND customer_id IN (SELECT id FROM sim_customers WHERE tenant_id = v_tenant_id AND customer_code LIKE 'DEMO-%');

    DELETE FROM sim_ap_ledger
    WHERE tenant_id = v_tenant_id
      AND supplier_id IN (SELECT id FROM sim_suppliers WHERE tenant_id = v_tenant_id AND supplier_code LIKE 'DEMO-%');

    -- 2. Vouchers tagged as demo data.
    DELETE FROM vouchers
    WHERE tenant_id = v_tenant_id
      AND (tags @> ARRAY['sau-truc-accounting-demo'] OR tags @> ARRAY['live-verify-cash-settlement']);

    -- 3. Contract parties + contracts tagged as demo data.
    DELETE FROM contract_parties
    WHERE tenant_id = v_tenant_id
      AND contract_id IN (SELECT id FROM contracts WHERE tenant_id = v_tenant_id AND tags @> ARRAY['sau-truc-accounting-demo']);

    DELETE FROM contracts
    WHERE tenant_id = v_tenant_id AND tags @> ARRAY['sau-truc-accounting-demo'];

    -- 4. Customers/suppliers themselves.
    DELETE FROM sim_customers WHERE tenant_id = v_tenant_id AND customer_code LIKE 'DEMO-%';
    DELETE FROM sim_suppliers WHERE tenant_id = v_tenant_id AND supplier_code LIKE 'DEMO-%';

    RAISE NOTICE 'Demo data removed for tenant %', v_tenant_id;
END $$;

COMMIT;
