INSERT INTO tools (
    code,
    name,
    description,
    category,
    risk_tier,
    min_action_tier
)
VALUES
(
    'mcp__aiq__list_customers',
    'List/search customers',
    'Tìm khách hàng theo tên/MST/mã KH',
    'read',
    'low',
    'read_only'
),
(
    'mcp__aiq__get_customer_receivables',
    'Get customer receivables (AR)',
    'Chi tiết công nợ phải thu 1 khách hàng',
    'read',
    'low',
    'read_only'
),
(
    'mcp__aiq__list_suppliers',
    'List/search suppliers',
    'Tìm nhà cung cấp theo tên/MST/mã NCC',
    'read',
    'low',
    'read_only'
),
(
    'mcp__aiq__get_supplier_payables',
    'Get supplier payables (AP)',
    'Chi tiết công nợ phải trả 1 nhà cung cấp',
    'read',
    'low',
    'read_only'
),
(
    'mcp__aiq__get_ap_summary',
    'Get AP summary',
    'Tổng hợp công nợ phải trả toàn phòng ban',
    'read',
    'low',
    'read_only'
),
(
    'mcp__aiq__list_vouchers',
    'List/search vouchers',
    'Tìm chứng từ theo loại/khoảng ngày/số hoá đơn',
    'read',
    'low',
    'read_only'
),
(
    'mcp__aiq__get_weekly_summary_report',
    'Get weekly summary report',
    'Báo cáo tuần ban giám đốc (dòng tiền, tuổi nợ, chi phí)',
    'read',
    'low',
    'read_only'
)
ON CONFLICT (code) DO UPDATE SET
    name = EXCLUDED.name,
    description = EXCLUDED.description,
    category = EXCLUDED.category,
    risk_tier = EXCLUDED.risk_tier,
    min_action_tier = EXCLUDED.min_action_tier;
