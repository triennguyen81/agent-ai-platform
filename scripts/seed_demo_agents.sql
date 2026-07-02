-- Seed demo agents for pilot demonstration.
-- Idempotent: safe to re-run. ON CONFLICT DO NOTHING/UPDATE everywhere.

BEGIN;

-- ============================================================================
-- 1. Seed MCP tools referenced by demo agents (if not yet in registry)
-- ============================================================================
INSERT INTO tools (code, name, description, category, risk_tier, min_action_tier)
VALUES
    -- Knowledge / web
    ('mcp__aiq__search_notes',      'Search notes',      'Semantic search over knowledge base notes.', 'read',     'low',    'read_only'),
    ('mcp__aiq__get_note',          'Get note',          'Read a single note by id.',                  'read',     'low',    'read_only'),
    ('mcp__aiq__save_note',         'Save note',         'Create or update a knowledge note.',         'write',    'medium', 'execute_with_approval'),
    ('mcp__aiq__fetch_url',         'Fetch URL',         'HTTP GET a public URL and return text.',     'external', 'medium', 'suggest'),
    -- Accounting Vietnam
    ('mcp__aiq__calculate_vat',     'Calculate VAT',     'Compute Vietnamese VAT (5/8/10%).',          'read',     'low',    'read_only'),
    ('mcp__aiq__calculate_pit',     'Calculate PIT',     'Compute personal income tax (Vietnam).',     'read',     'low',    'read_only'),
    ('mcp__aiq__calculate_cit',     'Calculate CIT',     'Compute corporate income tax (Vietnam).',    'read',     'low',    'read_only'),
    ('mcp__aiq__lookup_account',    'Lookup account',    'Look up chart-of-accounts entry.',           'read',     'low',    'read_only'),
    ('mcp__aiq__check_filing_deadline','Check deadline', 'Return the next tax filing deadline.',       'read',     'low',    'read_only'),
    ('mcp__aiq__journal_entry_validator','Validate JE',  'Validate a journal entry (Dr = Cr).',        'read',     'medium', 'suggest'),
    ('mcp__aiq__import_excel_ledger','Import Excel',     'Parse Excel ledger + stage into DB.',        'write',    'high',   'execute_with_approval'),
    -- HR (placeholder tool codes; SKILL logic can live entirely in prompts for MVP)
    ('mcp__aiq__lookup_employee',   'Lookup employee',   'Read an employee record from HR DB.',        'read',     'medium', 'read_only'),
    ('mcp__aiq__calculate_payroll', 'Calculate payroll', 'Compute monthly payroll for an employee.',   'read',     'medium', 'suggest'),
    ('mcp__aiq__leave_balance',     'Leave balance',     'Query remaining annual/sick leave for user.','read',     'low',    'read_only'),
    ('mcp__aiq__submit_leave_request','Submit leave',    'Create a leave request pending approval.',   'write',    'medium', 'execute_with_approval'),
    ('mcp__aiq__lookup_labor_law',  'Lookup labor law',  'Fetch relevant Vietnamese labor law clause.','read',     'low',    'read_only')
ON CONFLICT (code) DO NOTHING;

-- ============================================================================
-- 2. Insert demo agents
-- ============================================================================

WITH tenant AS (SELECT id AS tid FROM tenants WHERE code='default'),
     owner_admin AS (SELECT id AS uid FROM users WHERE email='tuong247@gmail.com'),
     owner_demo  AS (SELECT id AS uid FROM users WHERE email='demo@cattuong.ai')

INSERT INTO agents (tenant_id, code, name, type, description, lifecycle_state, owner_user_id, sponsor_team, action_tier, config)
SELECT tenant.tid, code, name, type, description, lifecycle_state, owner, sponsor, action_tier, config::jsonb
FROM tenant, (
    VALUES
        (
            'kt-bookkeeper',
            'Trợ lý kế toán',
            'domain',
            'Kế toán viên AI — tính VAT/PIT/CIT, tra cứu số hiệu tài khoản, validate journal entry, đề xuất bút toán theo TT 200/2014.',
            'production',
            (SELECT uid FROM owner_admin),
            'Kế toán',
            'suggest',
            '{"skills":["accounting-vietnam"],"model":"claude-haiku-4-5-20251001","max_turns":8}'
        ),
        (
            'hr-assistant',
            'Trợ lý nhân sự',
            'domain',
            'Chuyên viên HR AI — tra cứu thông tin nhân viên, tính lương, kiểm tra số ngày phép còn lại, xử lý đơn nghỉ phép, tư vấn Bộ luật Lao động Việt Nam.',
            'testing',
            (SELECT uid FROM owner_admin),
            'HR',
            'suggest',
            '{"skills":["hr-vietnam"],"model":"claude-haiku-4-5-20251001","max_turns":8,"policies":{"payroll_confidentiality":true,"require_approval_for_leave":true}}'
        ),
        (
            'knowledge-searcher',
            'Trợ lý tra cứu KB',
            'domain',
            'Chuyên viên tìm kiếm — chỉ đọc knowledge base + fetch URL công khai, không được ghi bất kỳ dữ liệu nào.',
            'production',
            (SELECT uid FROM owner_admin),
            'IT',
            'read_only',
            '{"skills":["vietnamese-style"],"model":"claude-haiku-4-5-20251001","max_turns":6}'
        ),
        (
            'main-orchestrator',
            'Điều phối viên chính',
            'orchestrator',
            'Agent điều phối — nhận yêu cầu, phân tích, và uỷ nhiệm cho domain agent phù hợp (contract / kế toán / HR / KB).',
            'testing',
            (SELECT uid FROM owner_admin),
            'Platform',
            'execute_with_approval',
            '{"delegates_to":["contract-monitor","kt-bookkeeper","hr-assistant","knowledge-searcher"],"model":"claude-sonnet-4-6","max_turns":12}'
        ),
        (
            'vat-quick-check',
            'VAT quick-check tool',
            'tool_only',
            'Tool-only agent chỉ để gọi calculate_vat — dùng cho web widget hoặc automation không cần LLM reasoning.',
            'draft',
            (SELECT uid FROM owner_demo),
            'Kế toán',
            'read_only',
            '{"skills":[],"single_tool":"mcp__aiq__calculate_vat"}'
        )
) AS v(code, name, type, description, lifecycle_state, owner, sponsor, action_tier, config)
ON CONFLICT (tenant_id, code) DO NOTHING;

-- ============================================================================
-- 3. Auto-grant owner ACL for each newly-created agent
-- ============================================================================
INSERT INTO agent_acl (agent_id, principal_id, principal_type, access_level, granted_by)
SELECT a.id, a.owner_user_id, 'user', 'owner', a.owner_user_id
FROM agents a
JOIN tenants t ON t.id=a.tenant_id AND t.code='default'
WHERE a.code IN ('kt-bookkeeper','hr-assistant','knowledge-searcher','main-orchestrator','vat-quick-check')
  AND a.owner_user_id IS NOT NULL
ON CONFLICT (agent_id, principal_id, principal_type) DO NOTHING;

-- ============================================================================
-- 4. Grant appropriate tools per agent (tier-filtered)
-- ============================================================================

-- kt-bookkeeper (tier=suggest): all accounting read tools + fetch + validator
INSERT INTO agent_tool_grants (agent_id, tool_id)
SELECT a.id, t.id
FROM agents a
JOIN tenants tn ON tn.id=a.tenant_id AND tn.code='default'
CROSS JOIN tools t
WHERE a.code='kt-bookkeeper'
  AND t.code IN (
      'mcp__aiq__calculate_vat','mcp__aiq__calculate_pit','mcp__aiq__calculate_cit',
      'mcp__aiq__lookup_account','mcp__aiq__check_filing_deadline',
      'mcp__aiq__journal_entry_validator','mcp__aiq__fetch_url'
  )
ON CONFLICT (agent_id, tool_id) DO NOTHING;

-- hr-assistant (tier=suggest): HR tools + labor law lookup + fetch
INSERT INTO agent_tool_grants (agent_id, tool_id)
SELECT a.id, t.id
FROM agents a
JOIN tenants tn ON tn.id=a.tenant_id AND tn.code='default'
CROSS JOIN tools t
WHERE a.code='hr-assistant'
  AND t.code IN (
      'mcp__aiq__lookup_employee','mcp__aiq__calculate_payroll',
      'mcp__aiq__leave_balance','mcp__aiq__lookup_labor_law',
      'mcp__aiq__fetch_url'
  )
ON CONFLICT (agent_id, tool_id) DO NOTHING;

-- knowledge-searcher (tier=read_only): only tools with min_tier=read_only
INSERT INTO agent_tool_grants (agent_id, tool_id)
SELECT a.id, t.id
FROM agents a
JOIN tenants tn ON tn.id=a.tenant_id AND tn.code='default'
CROSS JOIN tools t
WHERE a.code='knowledge-searcher'
  AND t.code IN (
      'mcp__aiq__search_notes','mcp__aiq__get_note',
      'mcp__aiq__get_contract','mcp__aiq__lookup_account',
      'mcp__aiq__lookup_labor_law','mcp__aiq__leave_balance'
  )
ON CONFLICT (agent_id, tool_id) DO NOTHING;

-- main-orchestrator (tier=execute_with_approval): broad access across domains
INSERT INTO agent_tool_grants (agent_id, tool_id)
SELECT a.id, t.id
FROM agents a
JOIN tenants tn ON tn.id=a.tenant_id AND tn.code='default'
CROSS JOIN tools t
WHERE a.code='main-orchestrator'
  AND t.code IN (
      'mcp__aiq__search_notes','mcp__aiq__get_note','mcp__aiq__save_note','mcp__aiq__fetch_url',
      'mcp__aiq__get_contract','mcp__aiq__save_contract',
      'mcp__aiq__calculate_vat','mcp__aiq__lookup_account',
      'mcp__aiq__lookup_employee','mcp__aiq__leave_balance','mcp__aiq__submit_leave_request'
  )
ON CONFLICT (agent_id, tool_id) DO NOTHING;

-- vat-quick-check (tier=read_only): single tool
INSERT INTO agent_tool_grants (agent_id, tool_id)
SELECT a.id, t.id
FROM agents a
JOIN tenants tn ON tn.id=a.tenant_id AND tn.code='default'
CROSS JOIN tools t
WHERE a.code='vat-quick-check' AND t.code='mcp__aiq__calculate_vat'
ON CONFLICT (agent_id, tool_id) DO NOTHING;

-- ============================================================================
-- 5. Assign demo agents to 3 users to test admin-assignment flow
-- ============================================================================
INSERT INTO agent_acl (agent_id, principal_id, principal_type, access_level, granted_by)
SELECT a.id, u.id, 'user', mapping.level, (SELECT id FROM users WHERE email='tuong247@gmail.com')
FROM agents a
JOIN tenants tn ON tn.id=a.tenant_id AND tn.code='default'
CROSS JOIN (VALUES
    ('demo@cattuong.ai',      'kt-bookkeeper',       'user'),
    ('demo@cattuong.ai',      'hr-assistant',        'user'),
    ('demo@cattuong.ai',      'knowledge-searcher',  'user'),
    ('smoke@aiq.local',       'knowledge-searcher',  'user'),
    ('smoke@aiq.local',       'kt-bookkeeper',       'user'),
    ('toannguyenq@gmail.com', 'contract-monitor',    'user'),
    ('toannguyenq@gmail.com', 'kt-bookkeeper',       'editor'),
    ('toannguyenq@gmail.com', 'hr-assistant',        'user')
) AS mapping(email, agent_code, level)
JOIN users u ON u.email=mapping.email
WHERE a.code=mapping.agent_code
ON CONFLICT (agent_id, principal_id, principal_type)
DO UPDATE SET access_level=EXCLUDED.access_level;

COMMIT;

-- ============================================================================
-- Verification queries
-- ============================================================================
\echo ''
\echo '=== Agents by tenant (after seeding) ==='
SELECT t.code AS tenant, a.code, a.type, a.lifecycle_state, a.action_tier,
       (SELECT email FROM users WHERE id=a.owner_user_id) AS owner,
       (SELECT COUNT(*) FROM agent_tool_grants g WHERE g.agent_id=a.id) AS tools,
       (SELECT COUNT(*) FROM agent_acl acl WHERE acl.agent_id=a.id) AS acl
FROM agents a JOIN tenants t ON t.id=a.tenant_id
ORDER BY t.code, a.type, a.code;

\echo ''
\echo '=== User agent assignments ==='
SELECT u.email, a.code AS agent, acl.access_level
FROM agent_acl acl
JOIN users u ON u.id=acl.principal_id
JOIN agents a ON a.id=acl.agent_id
ORDER BY u.email, a.code;
