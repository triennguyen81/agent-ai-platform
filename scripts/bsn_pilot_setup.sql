-- ============================================================================
-- BSN Pilot Setup — Day 1, 2, 3 combined
--
-- - Day 1: tenant 'bsn' + agent 'bsn-assistant' (production) + 4 starter notes
-- - Day 2: 1 admin account + 3 user accounts, all with password BSN@2026
-- - Day 3: assign agent to all users; attach knowledge; grant admin tenant_admin
--
-- All operations idempotent. Safe to re-run.
-- ============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. Tenant
-- ---------------------------------------------------------------------------
INSERT INTO tenants (code, name, status, metadata)
VALUES ('bsn', 'BSN', 'active',
        '{"pilot": true, "domain": "bsn.vn", "created": "day-1"}'::jsonb)
ON CONFLICT (code) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 2. Accounts — 1 admin + 3 users (password: BSN@2026)
-- Hash generated via bcrypt (rounds=12): $2b$12$UPoNOZ7XpKYDJP9BxvutRuYXX9NeaFkdP/lk2XcjXCYQhc3RKILeq
-- ---------------------------------------------------------------------------
INSERT INTO users (email, password_hash, display_name, is_active)
VALUES
    ('admin@bsn.vn',  '$2b$12$UPoNOZ7XpKYDJP9BxvutRuYXX9NeaFkdP/lk2XcjXCYQhc3RKILeq', 'BSN Admin',   TRUE),
    ('user1@bsn.vn',  '$2b$12$UPoNOZ7XpKYDJP9BxvutRuYXX9NeaFkdP/lk2XcjXCYQhc3RKILeq', 'BSN User 1',  TRUE),
    ('user2@bsn.vn',  '$2b$12$UPoNOZ7XpKYDJP9BxvutRuYXX9NeaFkdP/lk2XcjXCYQhc3RKILeq', 'BSN User 2',  TRUE),
    ('user3@bsn.vn',  '$2b$12$UPoNOZ7XpKYDJP9BxvutRuYXX9NeaFkdP/lk2XcjXCYQhc3RKILeq', 'BSN User 3',  TRUE)
ON CONFLICT (email) DO NOTHING;

-- Grant platform-level roles: admin gets admin + user, everyone else gets user
INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u CROSS JOIN roles r
WHERE u.email = 'admin@bsn.vn' AND r.name IN ('admin', 'user')
ON CONFLICT DO NOTHING;

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u CROSS JOIN roles r
WHERE u.email IN ('user1@bsn.vn', 'user2@bsn.vn', 'user3@bsn.vn') AND r.name = 'user'
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- 3. Tenant memberships — admin as tenant_admin, others as member
-- ---------------------------------------------------------------------------
INSERT INTO tenant_members (tenant_id, user_id, role)
SELECT t.id, u.id, 'tenant_admin'
FROM tenants t, users u
WHERE t.code='bsn' AND u.email='admin@bsn.vn'
ON CONFLICT (tenant_id, user_id) DO NOTHING;

INSERT INTO tenant_members (tenant_id, user_id, role)
SELECT t.id, u.id, 'member'
FROM tenants t, users u
WHERE t.code='bsn' AND u.email IN ('user1@bsn.vn', 'user2@bsn.vn', 'user3@bsn.vn')
ON CONFLICT (tenant_id, user_id) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 4. Agent for BSN — 'bsn-assistant' in production state
-- ---------------------------------------------------------------------------
INSERT INTO agents (
    tenant_id, code, name, type, description,
    lifecycle_state, owner_user_id, sponsor_team, action_tier, config
)
SELECT
    (SELECT id FROM tenants WHERE code='bsn'),
    'bsn-assistant',
    'Trợ lý AI BSN',
    'domain',
    'Trợ lý AI của BSN — trả lời câu hỏi về công ty, chính sách, dịch vụ dựa trên tài liệu nội bộ.',
    'production',
    (SELECT id FROM users WHERE email='admin@bsn.vn'),
    'BSN',
    'suggest',
    '{"skills":["vietnamese-style"],"model":"claude-haiku-4-5-20251001","max_turns":8}'::jsonb
ON CONFLICT (tenant_id, code) DO NOTHING;

-- Grant owner ACL to admin@bsn.vn
INSERT INTO agent_acl (agent_id, principal_id, principal_type, access_level, granted_by)
SELECT a.id,
       (SELECT id FROM users WHERE email='admin@bsn.vn'),
       'user', 'owner',
       (SELECT id FROM users WHERE email='admin@bsn.vn')
FROM agents a
JOIN tenants t ON t.id=a.tenant_id
WHERE t.code='bsn' AND a.code='bsn-assistant'
ON CONFLICT (agent_id, principal_id, principal_type) DO NOTHING;

-- Grant user access to all 3 user accounts
INSERT INTO agent_acl (agent_id, principal_id, principal_type, access_level, granted_by)
SELECT a.id, u.id, 'user', 'user',
       (SELECT id FROM users WHERE email='admin@bsn.vn')
FROM agents a
JOIN tenants t ON t.id=a.tenant_id
CROSS JOIN users u
WHERE t.code='bsn' AND a.code='bsn-assistant'
  AND u.email IN ('user1@bsn.vn', 'user2@bsn.vn', 'user3@bsn.vn')
ON CONFLICT (agent_id, principal_id, principal_type) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 5. Starter knowledge — 4 placeholder notes admin@bsn.vn owns
--    (Admin will edit content thật khi có tài liệu BSN)
-- ---------------------------------------------------------------------------

INSERT INTO notes (user_id, tenant_id, title, content, tags)
SELECT (SELECT id FROM users WHERE email='admin@bsn.vn'),
       (SELECT id FROM tenants WHERE code='bsn'),
       t.title, t.content, t.tags
FROM (VALUES
    (
        'Giới thiệu công ty BSN',
        E'*Placeholder — Admin BSN vui lòng cập nhật nội dung dưới đây.*\n\n' ||
        E'## Về BSN\n' ||
        E'- Tên đầy đủ: BSN [cần điền]\n' ||
        E'- Ngành nghề: [cần điền]\n' ||
        E'- Địa chỉ: [cần điền]\n' ||
        E'- Website: bsn.vn\n' ||
        E'- Số điện thoại: [cần điền]\n' ||
        E'- Email liên hệ: [cần điền]\n\n' ||
        E'## Lịch sử phát triển\n' ||
        E'[Điền vài dòng lịch sử công ty]\n\n' ||
        E'## Sứ mệnh & Giá trị cốt lõi\n' ||
        E'[Điền sứ mệnh]',
        ARRAY['gioi-thieu','cong-ty','bsn']::text[]
    ),
    (
        'Dịch vụ / Sản phẩm chính của BSN',
        E'*Placeholder — Admin BSN vui lòng cập nhật.*\n\n' ||
        E'## Danh mục sản phẩm / dịch vụ\n' ||
        E'1. **[Tên sản phẩm 1]** — mô tả ngắn, giá tham khảo\n' ||
        E'2. **[Tên sản phẩm 2]** — ...\n' ||
        E'3. **[Tên sản phẩm 3]** — ...\n\n' ||
        E'## Bảng giá tham khảo\n' ||
        E'| Sản phẩm | Giá | Ghi chú |\n' ||
        E'|----------|-----|--------|\n' ||
        E'| ... | ... | ... |\n\n' ||
        E'## Chính sách bán hàng\n' ||
        E'- Ưu đãi khách mới: ...\n' ||
        E'- Điều kiện thanh toán: ...\n' ||
        E'- Vận chuyển: ...',
        ARRAY['san-pham','dich-vu','bang-gia']::text[]
    ),
    (
        'Chính sách bảo hành & hỗ trợ khách hàng BSN',
        E'*Placeholder — Admin BSN cập nhật.*\n\n' ||
        E'## Thời hạn bảo hành\n' ||
        E'- Sản phẩm A: [X tháng]\n' ||
        E'- Sản phẩm B: [Y tháng]\n\n' ||
        E'## Điều kiện bảo hành\n' ||
        E'- Còn tem/số serial\n' ||
        E'- Không có dấu hiệu tự tháo mở\n' ||
        E'- ...\n\n' ||
        E'## Quy trình bảo hành\n' ||
        E'1. Liên hệ hotline / email hỗ trợ\n' ||
        E'2. Cung cấp mã đơn hàng + mô tả lỗi\n' ||
        E'3. Gửi sản phẩm về địa chỉ [...] hoặc mang trực tiếp tới showroom\n' ||
        E'4. BSN kiểm tra + phản hồi trong 3-5 ngày\n\n' ||
        E'## Hỗ trợ khách hàng\n' ||
        E'- Hotline: [...] (giờ làm việc 8h-17h30, T2-T7)\n' ||
        E'- Email: support@bsn.vn\n' ||
        E'- Fanpage: facebook.com/bsn.vn',
        ARRAY['bao-hanh','ho-tro','chinh-sach']::text[]
    ),
    (
        'FAQ — Câu hỏi thường gặp về BSN',
        E'*Placeholder — Admin BSN bổ sung câu hỏi khách hay hỏi nhất.*\n\n' ||
        E'### 1. BSN có giao hàng toàn quốc không?\n' ||
        E'[Trả lời]\n\n' ||
        E'### 2. Thanh toán qua các phương thức nào?\n' ||
        E'[Trả lời — chuyển khoản, COD, thẻ tín dụng...]\n\n' ||
        E'### 3. Đổi trả trong bao lâu?\n' ||
        E'[Trả lời]\n\n' ||
        E'### 4. Sản phẩm có xuất VAT không?\n' ||
        E'[Trả lời]\n\n' ||
        E'### 5. Có khuyến mãi cho đơn hàng lớn?\n' ||
        E'[Trả lời]\n\n' ||
        E'### 6. Làm sao đăng ký làm đại lý BSN?\n' ||
        E'[Trả lời]\n\n' ||
        E'### 7. Sản phẩm có được sản xuất tại Việt Nam?\n' ||
        E'[Trả lời]',
        ARRAY['faq','cau-hoi','khach-hang']::text[]
    )
) AS t(title, content, tags)
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- 6. Attach 4 starter notes to bsn-assistant
-- ---------------------------------------------------------------------------
INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
SELECT a.id, n.id, (SELECT id FROM users WHERE email='admin@bsn.vn')
FROM agents a
JOIN tenants t ON t.id=a.tenant_id
CROSS JOIN notes n
WHERE t.code='bsn' AND a.code='bsn-assistant'
  AND n.tenant_id = t.id
  AND n.title IN (
      'Giới thiệu công ty BSN',
      'Dịch vụ / Sản phẩm chính của BSN',
      'Chính sách bảo hành & hỗ trợ khách hàng BSN',
      'FAQ — Câu hỏi thường gặp về BSN'
  )
ON CONFLICT DO NOTHING;

COMMIT;

-- ---------------------------------------------------------------------------
-- Verify
-- ---------------------------------------------------------------------------
\echo ''
\echo '=== BSN Tenant ==='
SELECT code, name, status FROM tenants WHERE code='bsn';

\echo ''
\echo '=== BSN Users + Roles ==='
SELECT u.email, u.display_name,
       (SELECT string_agg(r.name, ',') FROM user_roles ur JOIN roles r ON r.id=ur.role_id WHERE ur.user_id=u.id) AS roles,
       (SELECT role FROM tenant_members tm JOIN tenants t ON t.id=tm.tenant_id WHERE tm.user_id=u.id AND t.code='bsn') AS tenant_role
FROM users u WHERE u.email LIKE '%@bsn.vn' ORDER BY u.email;

\echo ''
\echo '=== BSN Agent ==='
SELECT a.code, a.lifecycle_state, a.action_tier,
       (SELECT email FROM users WHERE id=a.owner_user_id) AS owner,
       (SELECT COUNT(*) FROM agent_knowledge WHERE agent_id=a.id) AS notes,
       (SELECT COUNT(*) FROM agent_acl WHERE agent_id=a.id) AS acl
FROM agents a JOIN tenants t ON t.id=a.tenant_id
WHERE t.code='bsn';
