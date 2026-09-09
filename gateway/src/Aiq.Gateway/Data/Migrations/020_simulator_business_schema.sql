-- Phase D1.5: canonical schema (no demo rows) for the business/simulator
-- tables queried by Gateway. Demo content remains in explicit scripts/ and
-- never participates in docker-entrypoint initialization.

CREATE TABLE IF NOT EXISTS sim_customers (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    customer_code TEXT NOT NULL,
    name TEXT NOT NULL,
    mst TEXT,
    address TEXT,
    phone TEXT,
    email TEXT,
    category TEXT DEFAULT 'phong_kham',
    credit_limit NUMERIC(18,2) DEFAULT 0,
    credit_terms_days INT DEFAULT 0,
    current_ar NUMERIC(18,2) DEFAULT 0,
    extras JSONB DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, customer_code),
    UNIQUE (tenant_id, mst)
);

CREATE TABLE IF NOT EXISTS sim_suppliers (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    supplier_code TEXT NOT NULL,
    name TEXT NOT NULL,
    mst TEXT,
    country TEXT DEFAULT 'VN',
    currency TEXT DEFAULT 'VND',
    payment_terms TEXT,
    payment_terms_days INT DEFAULT 0,
    current_ap NUMERIC(18,2) DEFAULT 0,
    extras JSONB DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, supplier_code)
);

ALTER TABLE sim_suppliers
    ADD COLUMN IF NOT EXISTS payment_terms_days INT DEFAULT 0;

CREATE TABLE IF NOT EXISTS sim_employees (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    employee_code TEXT NOT NULL,
    full_name TEXT NOT NULL,
    department TEXT,
    position TEXT,
    email TEXT,
    phone TEXT,
    monthly_expense_budget NUMERIC(18,2) DEFAULT 0,
    ytd_expense NUMERIC(18,2) DEFAULT 0,
    outstanding_advance NUMERIC(18,2) DEFAULT 0,
    extras JSONB DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, employee_code)
);

CREATE TABLE IF NOT EXISTS sim_expense_claims (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    document_number TEXT NOT NULL,
    document_date DATE NOT NULL,
    employee_id UUID NOT NULL REFERENCES sim_employees(id),
    purpose TEXT,
    trip_from DATE,
    trip_to DATE,
    items JSONB NOT NULL DEFAULT '[]'::jsonb,
    total_claim NUMERIC(18,2) NOT NULL,
    advance_taken NUMERIC(18,2) DEFAULT 0,
    reimbursement_needed NUMERIC(18,2)
        GENERATED ALWAYS AS (total_claim - advance_taken) STORED,
    status TEXT DEFAULT 'submitted',
    approved_by TEXT,
    approved_at TIMESTAMPTZ,
    paid_at TIMESTAMPTZ,
    journal_entry_id UUID,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, document_number)
);

CREATE TABLE IF NOT EXISTS sim_ar_ledger (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    customer_id UUID NOT NULL REFERENCES sim_customers(id),
    document_date DATE NOT NULL,
    document_number TEXT NOT NULL,
    document_type TEXT NOT NULL,
    debit NUMERIC(18,2) DEFAULT 0,
    credit NUMERIC(18,2) DEFAULT 0,
    balance_after NUMERIC(18,2),
    due_date DATE,
    reference_note TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS sim_products (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    sku TEXT NOT NULL,
    name TEXT NOT NULL,
    category TEXT NOT NULL,
    unit TEXT NOT NULL DEFAULT 'cái',
    unit_price NUMERIC(18,2) NOT NULL,
    vat_rate NUMERIC(5,2) DEFAULT 5,
    cost_price NUMERIC(18,2),
    warranty_months INT DEFAULT 12,
    supplier_code TEXT,
    stock_on_hand INT DEFAULT 0,
    reorder_point INT DEFAULT 0,
    extras JSONB DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, sku)
);

CREATE TABLE IF NOT EXISTS sim_sales_orders (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    order_number TEXT NOT NULL,
    order_date DATE NOT NULL,
    customer_id UUID NOT NULL REFERENCES sim_customers(id),
    contract_id UUID,
    salesperson_code TEXT,
    status TEXT DEFAULT 'confirmed',
    expected_delivery_date DATE,
    payment_terms_days INT DEFAULT 30,
    subtotal NUMERIC(18,2) NOT NULL,
    vat_amount NUMERIC(18,2) NOT NULL,
    total_amount NUMERIC(18,2) NOT NULL,
    invoiced_ref TEXT,
    delivery_note_ref TEXT,
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, order_number)
);

CREATE TABLE IF NOT EXISTS sim_sales_order_lines (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    order_id UUID NOT NULL REFERENCES sim_sales_orders(id) ON DELETE CASCADE,
    ordinal INT NOT NULL,
    product_id UUID REFERENCES sim_products(id),
    sku TEXT NOT NULL,
    description TEXT NOT NULL,
    unit TEXT,
    quantity NUMERIC(18,4) NOT NULL,
    unit_price NUMERIC(18,2) NOT NULL,
    subtotal NUMERIC(18,2) NOT NULL,
    vat_rate NUMERIC(5,2) NOT NULL,
    vat_amount NUMERIC(18,2) NOT NULL,
    total_amount NUMERIC(18,2) NOT NULL,
    UNIQUE (order_id, ordinal)
);

CREATE TABLE IF NOT EXISTS sim_delivery_notes (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    dn_number TEXT NOT NULL,
    dn_date DATE NOT NULL,
    customer_id UUID NOT NULL REFERENCES sim_customers(id),
    sales_order_id UUID REFERENCES sim_sales_orders(id),
    warehouse_from TEXT DEFAULT 'Kho HCM',
    delivered_by TEXT,
    received_by TEXT,
    delivery_address TEXT,
    status TEXT DEFAULT 'delivered',
    delivered_at TIMESTAMPTZ,
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, dn_number)
);

CREATE TABLE IF NOT EXISTS sim_contracts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    contract_number TEXT NOT NULL,
    contract_date DATE NOT NULL,
    customer_id UUID REFERENCES sim_customers(id),
    supplier_code TEXT,
    contract_type TEXT NOT NULL,
    title TEXT NOT NULL,
    contract_value NUMERIC(18,2) NOT NULL,
    currency TEXT DEFAULT 'VND',
    valid_from DATE NOT NULL,
    valid_to DATE,
    payment_terms_days INT DEFAULT 30,
    revenue_recognition TEXT DEFAULT 'delivery',
    vat_rate NUMERIC(5,2) DEFAULT 5,
    warranty_period_months INT DEFAULT 12,
    penalty_late_pct_per_day NUMERIC(5,4) DEFAULT 0.05,
    status TEXT DEFAULT 'active',
    signed_by_party_a TEXT,
    signed_by_party_b TEXT,
    notes TEXT,
    extras JSONB DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, contract_number)
);

CREATE TABLE IF NOT EXISTS sim_contract_milestones (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    contract_id UUID NOT NULL REFERENCES sim_contracts(id) ON DELETE CASCADE,
    milestone_no INT NOT NULL,
    description TEXT NOT NULL,
    percentage NUMERIC(5,2) NOT NULL,
    amount NUMERIC(18,2) NOT NULL,
    expected_date DATE NOT NULL,
    actual_date DATE,
    status TEXT DEFAULT 'pending',
    invoice_ref TEXT,
    UNIQUE (contract_id, milestone_no)
);

CREATE TABLE IF NOT EXISTS sim_bank_accounts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    account_number TEXT NOT NULL,
    bank_name TEXT NOT NULL,
    branch TEXT,
    account_holder TEXT NOT NULL,
    currency TEXT DEFAULT 'VND',
    account_type TEXT DEFAULT 'checking',
    current_balance NUMERIC(18,2) DEFAULT 0,
    gl_account_code TEXT NOT NULL,
    is_primary BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, account_number)
);

CREATE TABLE IF NOT EXISTS sim_bank_transactions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id UUID NOT NULL REFERENCES sim_bank_accounts(id) ON DELETE CASCADE,
    tx_date DATE NOT NULL,
    tx_ref TEXT,
    description TEXT NOT NULL,
    debit NUMERIC(18,2) DEFAULT 0,
    credit NUMERIC(18,2) DEFAULT 0,
    balance_after NUMERIC(18,2),
    counterparty_name TEXT,
    counterparty_account TEXT,
    matched_document_type TEXT,
    matched_document_number TEXT,
    suggested_gl_debit TEXT,
    suggested_gl_credit TEXT,
    reconciled BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS sim_purchase_orders (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    po_number TEXT NOT NULL,
    po_date DATE NOT NULL,
    supplier_code TEXT NOT NULL,
    order_type TEXT DEFAULT 'goods',
    status TEXT DEFAULT 'confirmed',
    expected_delivery DATE,
    currency TEXT DEFAULT 'VND',
    exchange_rate NUMERIC(18,4) DEFAULT 1,
    subtotal_foreign NUMERIC(18,2),
    subtotal_vnd NUMERIC(18,2) NOT NULL,
    import_duty_pct NUMERIC(5,2),
    vat_import_pct NUMERIC(5,2),
    incoterm TEXT,
    payment_terms TEXT,
    goods_received_note TEXT,
    matched_invoice_number TEXT,
    notes TEXT,
    total_amount NUMERIC(18,2),
    payment_terms_days INT DEFAULT 0,
    delivery_note_ref TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, po_number)
);

ALTER TABLE sim_purchase_orders
    ADD COLUMN IF NOT EXISTS total_amount NUMERIC(18,2),
    ADD COLUMN IF NOT EXISTS payment_terms_days INT DEFAULT 0,
    ADD COLUMN IF NOT EXISTS delivery_note_ref TEXT;

CREATE TABLE IF NOT EXISTS sim_ap_ledger (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    supplier_id UUID NOT NULL REFERENCES sim_suppliers(id),
    voucher_id UUID,
    document_date DATE NOT NULL,
    document_number TEXT NOT NULL,
    document_type TEXT NOT NULL,
    debit NUMERIC(18,2) DEFAULT 0,
    credit NUMERIC(18,2) DEFAULT 0,
    balance_after NUMERIC(18,2),
    due_date DATE,
    reference_note TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS sim_goods_received_notes (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    grn_number TEXT NOT NULL,
    grn_date DATE NOT NULL,
    po_number TEXT NOT NULL,
    warehouse_to TEXT,
    delivered_by TEXT,
    received_by TEXT,
    status TEXT DEFAULT 'received',
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (tenant_id, grn_number)
);

CREATE INDEX IF NOT EXISTS idx_sim_customers_mst ON sim_customers(mst) WHERE mst IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_sim_employees_code ON sim_employees(employee_code);
CREATE INDEX IF NOT EXISTS idx_sim_ar_customer_date ON sim_ar_ledger(customer_id, document_date DESC);
CREATE INDEX IF NOT EXISTS idx_sim_expense_status ON sim_expense_claims(tenant_id, status, document_date DESC);
CREATE INDEX IF NOT EXISTS idx_sim_products_category ON sim_products(tenant_id, category);
CREATE INDEX IF NOT EXISTS idx_sim_so_customer ON sim_sales_orders(customer_id, order_date DESC);
CREATE INDEX IF NOT EXISTS idx_sim_so_invoiced ON sim_sales_orders(tenant_id, invoiced_ref);
CREATE INDEX IF NOT EXISTS idx_sim_bank_tx_date ON sim_bank_transactions(account_id, tx_date DESC);
CREATE INDEX IF NOT EXISTS idx_sim_ap_supplier_date ON sim_ap_ledger(supplier_id, document_date DESC);
CREATE INDEX IF NOT EXISTS idx_sim_ap_open ON sim_ap_ledger(supplier_id, due_date) WHERE debit = 0;
CREATE INDEX IF NOT EXISTS idx_sim_ap_voucher ON sim_ap_ledger(voucher_id) WHERE voucher_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_sim_grn_po ON sim_goods_received_notes(tenant_id, po_number);
CREATE INDEX IF NOT EXISTS idx_sim_grn_date ON sim_goods_received_notes(tenant_id, grn_date DESC);

COMMENT ON TABLE sim_customers IS
    'Business simulator schema owned by Gateway migrations; demo rows are seeded separately.';
