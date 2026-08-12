-- ============================================================================
-- Việt Đăng Accounting Agent Setup
--
-- Tenant : vietdang (CTCP Thiết bị Nha khoa Việt Đăng)
-- Agent  : vd-accounting — Trợ lý Kế toán chuyên ngành thiết bị nha khoa
-- Users  : admin@vietdang.vn + 3 nhân viên (password: VD@2026)
-- Notes  : 10 kiến thức kế toán chuyên ngành (không phải placeholder)
--
-- All operations idempotent. Safe to re-run.
-- ============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. Tenant
-- ---------------------------------------------------------------------------
INSERT INTO tenants (code, name, status, metadata)
VALUES ('vietdang', 'CTCP Thiết bị Nha khoa Việt Đăng', 'active',
        '{"pilot": true, "domain": "vietdang.vn", "industry": "dental-equipment", "business_type": "JSC-import-distribution"}'::jsonb)
ON CONFLICT (code) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 2. Accounts — 1 admin + 3 users (password: VD@2026)
-- Bcrypt hash of "VD@2026" (rounds=12) — independent from BSN@2026
-- ---------------------------------------------------------------------------
INSERT INTO users (email, password_hash, display_name, is_active)
VALUES
    ('admin@vietdang.vn',      '$2b$12$MUZ5bYhQ7/7RkqVWpSYMaup7MEc3HrJpMkTi9qJ9wm2RG3fWdMnti', 'Việt Đăng Admin',       TRUE),
    ('ketoan1@vietdang.vn',    '$2b$12$MUZ5bYhQ7/7RkqVWpSYMaup7MEc3HrJpMkTi9qJ9wm2RG3fWdMnti', 'Kế toán tổng hợp',      TRUE),
    ('ketoan2@vietdang.vn',    '$2b$12$MUZ5bYhQ7/7RkqVWpSYMaup7MEc3HrJpMkTi9qJ9wm2RG3fWdMnti', 'Kế toán công nợ',       TRUE),
    ('ketoan3@vietdang.vn',    '$2b$12$MUZ5bYhQ7/7RkqVWpSYMaup7MEc3HrJpMkTi9qJ9wm2RG3fWdMnti', 'Kế toán kho',           TRUE)
ON CONFLICT (email) DO NOTHING;

-- Platform-level roles
INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u CROSS JOIN roles r
WHERE u.email = 'admin@vietdang.vn' AND r.name IN ('admin', 'user')
ON CONFLICT DO NOTHING;

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u CROSS JOIN roles r
WHERE u.email IN ('ketoan1@vietdang.vn', 'ketoan2@vietdang.vn', 'ketoan3@vietdang.vn')
  AND r.name = 'user'
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- 3. Tenant memberships
-- ---------------------------------------------------------------------------
INSERT INTO tenant_members (tenant_id, user_id, role)
SELECT t.id, u.id, 'tenant_admin'
FROM tenants t, users u
WHERE t.code='vietdang' AND u.email='admin@vietdang.vn'
ON CONFLICT (tenant_id, user_id) DO NOTHING;

INSERT INTO tenant_members (tenant_id, user_id, role)
SELECT t.id, u.id, 'member'
FROM tenants t, users u
WHERE t.code='vietdang'
  AND u.email IN ('ketoan1@vietdang.vn', 'ketoan2@vietdang.vn', 'ketoan3@vietdang.vn')
ON CONFLICT (tenant_id, user_id) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 4. Agent — vd-accounting
-- ---------------------------------------------------------------------------
INSERT INTO agents (
    tenant_id, code, name, type, description,
    lifecycle_state, owner_user_id, sponsor_team, action_tier, config
)
SELECT
    (SELECT id FROM tenants WHERE code='vietdang'),
    'vd-accounting',
    'Trợ lý Kế toán Việt Đăng',
    'domain',
    'Trợ lý kế toán chuyên ngành thương mại thiết bị nha khoa — hạch toán TT200, xử lý hoá đơn VAT (5%/10%), quản lý công nợ NCC/khách hàng, tồn kho, báo cáo thuế định kỳ.',
    'production',
    (SELECT id FROM users WHERE email='admin@vietdang.vn'),
    'Kế toán Việt Đăng',
    'suggest',
    '{"skills":["accounting-vietnam","accounting-vn-style","journal-entry-formatter","tax-calculation","voucher-invoice-vat","vietnamese-style","compliance-citation","rag-citation"],"model":"claude-haiku-4-5-20251001","max_turns":8}'::jsonb
ON CONFLICT (tenant_id, code) DO NOTHING;

-- Owner ACL cho admin
INSERT INTO agent_acl (agent_id, principal_id, principal_type, access_level, granted_by)
SELECT a.id,
       (SELECT id FROM users WHERE email='admin@vietdang.vn'),
       'user', 'owner',
       (SELECT id FROM users WHERE email='admin@vietdang.vn')
FROM agents a
JOIN tenants t ON t.id=a.tenant_id
WHERE t.code='vietdang' AND a.code='vd-accounting'
ON CONFLICT (agent_id, principal_id, principal_type) DO NOTHING;

-- User ACL cho 3 kế toán viên
INSERT INTO agent_acl (agent_id, principal_id, principal_type, access_level, granted_by)
SELECT a.id, u.id, 'user', 'user',
       (SELECT id FROM users WHERE email='admin@vietdang.vn')
FROM agents a
JOIN tenants t ON t.id=a.tenant_id
CROSS JOIN users u
WHERE t.code='vietdang' AND a.code='vd-accounting'
  AND u.email IN ('ketoan1@vietdang.vn', 'ketoan2@vietdang.vn', 'ketoan3@vietdang.vn')
ON CONFLICT (agent_id, principal_id, principal_type) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 5. Starter knowledge — 10 note kế toán chuyên ngành thiết bị nha khoa
-- ---------------------------------------------------------------------------

INSERT INTO notes (user_id, tenant_id, title, content, tags)
SELECT (SELECT id FROM users WHERE email='admin@vietdang.vn'),
       (SELECT id FROM tenants WHERE code='vietdang'),
       t.title, t.content, t.tags
FROM (VALUES

-- Note 1 — Giới thiệu công ty + đặc thù ngành
(
    'Giới thiệu CTCP Thiết bị Nha khoa Việt Đăng',
    E'## Thông tin công ty\n' ||
    E'- **Loại hình:** Công ty Cổ phần\n' ||
    E'- **Ngành nghề chính:** Nhập khẩu + phân phối thiết bị nha khoa (ghế nha khoa, máy X-quang, máy Scan 3D, autoclave, tay khoan, vật tư tiêu hao)\n' ||
    E'- **Thị trường:** Phòng khám nha khoa tư nhân, bệnh viện, trường ĐH Y-Nha, đại lý cấp 2\n' ||
    E'- **Chế độ kế toán áp dụng:** Thông tư 200/2014/TT-BTC (không dùng TT133 do vốn điều lệ > 100 tỷ và loại hình JSC)\n' ||
    E'- **Kỳ kế toán:** Năm tài chính theo dương lịch (01/01 – 31/12)\n' ||
    E'- **Đơn vị tiền tệ:** VND, có phát sinh USD/EUR khi nhập khẩu → quy đổi theo tỷ giá NHTM ngày phát sinh\n\n' ||
    E'## Đặc thù kế toán ngành thiết bị nha khoa\n' ||
    E'1. **Nhập khẩu chiếm ~80%** hàng hoá → phát sinh thuế nhập khẩu (thường 0-5% cho thiết bị y tế theo Nghị định 26/2023/NĐ-CP), thuế GTGT hàng nhập khẩu (5% với đa số thiết bị y tế theo phụ lục TT 43/2021/TT-BTC), phí THC/DO/lưu kho.\n' ||
    E'2. **VAT đầu ra hỗn hợp:** thiết bị y tế 5%, vật tư tiêu hao (găng tay, khẩu trang, kim tiêm) đa số 5% hoặc 10% tuỳ mã HS, dịch vụ lắp đặt/đào tạo 10%.\n' ||
    E'3. **Bảo hành dài** (12-24-36 tháng tuỳ dòng máy) → cần trích trước chi phí bảo hành (TK 352).\n' ||
    E'4. **Chi phí đào tạo bác sĩ** khi bán máy lớn → hạch toán vào 641 (chi phí bán hàng) hoặc phân bổ nếu chương trình dài hạn.\n' ||
    E'5. **Hàng ký gửi + hàng demo** tại phòng khám → theo dõi ngoài bảng (TK 002) hoặc chi tiết trong 156.\n',
    ARRAY['gioi-thieu','viet-dang','nha-khoa','dac-thu-nganh']::text[]
),

-- Note 2 — Chart of Accounts customized
(
    'Hệ thống tài khoản kế toán áp dụng tại Việt Đăng (theo TT200)',
    E'## Nhóm tài sản ngắn hạn (loại 1)\n' ||
    E'| TK | Tên | Ghi chú Việt Đăng |\n' ||
    E'|----|-----|-------------------|\n' ||
    E'| 111 | Tiền mặt | 1111 VND, 1112 USD (thanh toán NCC nước ngoài) |\n' ||
    E'| 112 | Tiền gửi ngân hàng | 11211 VCB, 11212 BIDV, 11221 VCB-USD |\n' ||
    E'| 131 | Phải thu khách hàng | Chi tiết theo mã KH (PK-xxx cho phòng khám, BV-xxx cho bệnh viện, DL-xxx cho đại lý) |\n' ||
    E'| 133 | Thuế GTGT được khấu trừ | 1331 hàng hoá dịch vụ, 1332 TSCĐ |\n' ||
    E'| 138 | Phải thu khác | 1381 tài sản thiếu chờ xử lý, 1388 phải thu bồi thường bảo hiểm hàng NK |\n' ||
    E'| 141 | Tạm ứng | Chi tiết theo nhân viên |\n' ||
    E'| 151 | Hàng mua đang đi đường | Hàng đã có invoice NCC nước ngoài nhưng chưa về kho |\n' ||
    E'| 152 | Nguyên vật liệu | 1521 phụ tùng thay thế bảo hành |\n' ||
    E'| 156 | Hàng hoá | 1561 giá mua, 1562 chi phí thu mua (thuế NK, phí vận chuyển NK, phí THC/DO) |\n' ||
    E'| 157 | Hàng gửi bán | Hàng ký gửi tại phòng khám demo |\n\n' ||
    E'## Nhóm nợ phải trả (loại 3)\n' ||
    E'| TK | Tên | Ghi chú |\n' ||
    E'|----|-----|--------|\n' ||
    E'| 331 | Phải trả người bán | Chi tiết theo NCC (KaVo, Sirona, Planmeca, Vatech, các NCC VN) |\n' ||
    E'| 3331 | Thuế GTGT phải nộp | 33311 GTGT đầu ra, 33312 GTGT hàng NK |\n' ||
    E'| 3332 | Thuế TTĐB | (không phát sinh, thiết bị y tế không thuộc diện chịu TTĐB) |\n' ||
    E'| 3333 | Thuế NK phải nộp | Chi tiết từng tờ khai HQ |\n' ||
    E'| 3334 | Thuế TNDN phải nộp | Tạm tính quý + quyết toán năm |\n' ||
    E'| 3335 | Thuế TNCN phải nộp | Khấu trừ tại nguồn lương nhân viên |\n' ||
    E'| 3387 | Doanh thu chưa thực hiện | Phí đào tạo trọn gói trả trước, gói bảo trì dài hạn |\n' ||
    E'| 352 | Dự phòng phải trả | 3521 dự phòng bảo hành sản phẩm |\n\n' ||
    E'## Nhóm doanh thu / chi phí (loại 5, 6, 7)\n' ||
    E'| TK | Tên | Ghi chú |\n' ||
    E'|----|-----|--------|\n' ||
    E'| 511 | Doanh thu bán hàng | 5111 bán thiết bị chính, 5112 bán vật tư tiêu hao, 5113 dịch vụ lắp đặt & đào tạo |\n' ||
    E'| 521 | Các khoản giảm trừ doanh thu | 5211 chiết khấu thương mại, 5212 hàng bán bị trả lại, 5213 giảm giá hàng bán |\n' ||
    E'| 632 | Giá vốn hàng bán | Chi tiết theo dòng sản phẩm |\n' ||
    E'| 635 | Chi phí tài chính | Chênh lệch tỷ giá thanh toán NCC nước ngoài, lãi vay |\n' ||
    E'| 641 | Chi phí bán hàng | 6411 lương KD, 6414 khấu hao xe giao hàng, 6417 dịch vụ mua ngoài (vận chuyển giao KH), 6418 chi phí bảo hành, đào tạo bác sĩ |\n' ||
    E'| 642 | Chi phí QLDN | 6421 lương văn phòng, 6423 công cụ dụng cụ, 6425 thuế phí lệ phí (môn bài, kê khai) |\n' ||
    E'| 711 | Thu nhập khác | Bán phế liệu, thu bảo hiểm hàng NK bồi thường |\n' ||
    E'| 811 | Chi phí khác | Phạt vi phạm hợp đồng, thanh lý TSCĐ |\n',
    ARRAY['tai-khoan','tt200','chart-of-accounts','viet-dang']::text[]
),

-- Note 3 — Nghiệp vụ mua hàng nhập khẩu
(
    'Nghiệp vụ mua hàng nhập khẩu — hạch toán từ A đến Z',
    E'## Case chuẩn: Việt Đăng NK 1 lô ghế nha khoa từ KaVo Đức\n' ||
    E'**Giá CIF Hải Phòng:** 50.000 EUR (tỷ giá NHTM ngày mở tờ khai: 27.500 VND/EUR)\n' ||
    E'- Thuế NK: 5% × 50.000 EUR × 27.500 = 68.750.000 VND\n' ||
    E'- Thuế GTGT hàng NK: 5% × (50.000×27.500 + 68.750.000) = 71.187.500 VND\n' ||
    E'- Phí THC + D/O + lưu kho: 12.000.000 VND (VAT 10% = 1.200.000)\n' ||
    E'- Vận chuyển nội địa Hải Phòng → HCM: 8.000.000 VND (VAT 10% = 800.000)\n\n' ||
    E'## Bút toán chi tiết\n\n' ||
    E'**BT1 — Hàng về kho theo tờ khai HQ (giá CIF quy đổi):**\n' ||
    E'```\n' ||
    E'Nợ 1561      1.375.000.000  (50.000 × 27.500)\n' ||
    E'   Có 331    1.375.000.000  (chi tiết NCC KaVo)\n' ||
    E'```\n\n' ||
    E'**BT2 — Thuế NK ghi tăng giá vốn:**\n' ||
    E'```\n' ||
    E'Nợ 1562      68.750.000\n' ||
    E'   Có 3333   68.750.000\n' ||
    E'```\n\n' ||
    E'**BT3 — VAT hàng NK (khấu trừ):**\n' ||
    E'```\n' ||
    E'Nợ 1331      71.187.500\n' ||
    E'   Có 33312  71.187.500\n' ||
    E'```\n\n' ||
    E'**BT4 — Nộp thuế NK + VAT NK:**\n' ||
    E'```\n' ||
    E'Nợ 3333      68.750.000\n' ||
    E'Nợ 33312     71.187.500\n' ||
    E'   Có 11211  139.937.500\n' ||
    E'```\n\n' ||
    E'**BT5 — Phí THC/DO/lưu kho + VAT:**\n' ||
    E'```\n' ||
    E'Nợ 1562      12.000.000\n' ||
    E'Nợ 1331       1.200.000\n' ||
    E'   Có 331/111 13.200.000\n' ||
    E'```\n\n' ||
    E'**BT6 — Vận chuyển nội địa về kho HCM:**\n' ||
    E'```\n' ||
    E'Nợ 1562       8.000.000\n' ||
    E'Nợ 1331         800.000\n' ||
    E'   Có 331/111  8.800.000\n' ||
    E'```\n\n' ||
    E'**BT7 — Kết chuyển 1562 vào giá gốc hàng hoá:**\n' ||
    E'Cuối tháng phân bổ 1562 cho lô hàng theo tiêu thức giá trị:\n' ||
    E'```\n' ||
    E'Nợ 1561      88.750.000\n' ||
    E'   Có 1562   88.750.000\n' ||
    E'```\n\n' ||
    E'**BT8 — Thanh toán NCC KaVo (nếu tỷ giá thanh toán khác tỷ giá ghi nhận):**\n' ||
    E'Giả sử ngày thanh toán tỷ giá 27.700 → chênh lệch 200 × 50.000 = 10.000.000 VND\n' ||
    E'```\n' ||
    E'Nợ 331       1.375.000.000\n' ||
    E'Nợ 635          10.000.000  (lỗ chênh lệch tỷ giá)\n' ||
    E'   Có 11221   1.385.000.000\n' ||
    E'```\n\n' ||
    E'## Lưu ý áp dụng\n' ||
    E'- Nếu tỷ giá thanh toán < tỷ giá ghi nhận → lãi CLTG → **Có 515**\n' ||
    E'- TK 1562 (chi phí thu mua) tập hợp toàn bộ chi phí phát sinh trước khi hàng nhập kho, cuối kỳ phân bổ vào 1561 theo tỷ lệ giá trị hàng.\n' ||
    E'- Không được tính phí lãi vay NK vào giá vốn — hạch toán 635.\n',
    ARRAY['nhap-khau','but-toan','tt200','chenh-lech-ty-gia','thiet-bi-y-te']::text[]
),

-- Note 4 — Bán hàng
(
    'Nghiệp vụ bán hàng — phòng khám, bệnh viện, đại lý',
    E'## Case 1: Bán 1 ghế nha khoa cho Phòng khám Nha Xinh\n' ||
    E'- Giá bán chưa VAT: 350.000.000 VND\n' ||
    E'- VAT 5% (thiết bị y tế theo phụ lục TT 43/2021): 17.500.000 VND\n' ||
    E'- Thanh toán: CK 50% trước, 50% sau lắp đặt\n' ||
    E'- Giá vốn (từ 1561): 260.000.000 VND\n\n' ||
    E'**BT1 — Xuất hoá đơn:**\n' ||
    E'```\n' ||
    E'Nợ 131 (Nha Xinh)    367.500.000\n' ||
    E'   Có 5111           350.000.000\n' ||
    E'   Có 33311           17.500.000\n' ||
    E'```\n\n' ||
    E'**BT2 — Ghi nhận giá vốn:**\n' ||
    E'```\n' ||
    E'Nợ 632               260.000.000\n' ||
    E'   Có 1561           260.000.000\n' ||
    E'```\n\n' ||
    E'**BT3 — Nhận CK 50%:**\n' ||
    E'```\n' ||
    E'Nợ 11211             183.750.000\n' ||
    E'   Có 131            183.750.000\n' ||
    E'```\n\n' ||
    E'## Case 2: Bán vật tư tiêu hao (găng tay, khẩu trang, kim gây tê) — VAT 5% hoặc 10%\n' ||
    E'Tra mã HS + đối chiếu phụ lục TT 43/2021/TT-BTC để xác định 5% hay 10%.\n' ||
    E'- Găng tay khám bệnh y tế: **5%**\n' ||
    E'- Kim tiêm, xy-lanh y tế: **5%**\n' ||
    E'- Vật tư văn phòng dùng trong phòng khám (không phải VTYT): **10%**\n\n' ||
    E'## Case 3: Bán kèm dịch vụ lắp đặt + đào tạo\n' ||
    E'Tách hoá đơn 2 dòng:\n' ||
    E'- Dòng 1: Ghế nha khoa — VAT 5%\n' ||
    E'- Dòng 2: Dịch vụ lắp đặt + đào tạo — VAT 10%\n\n' ||
    E'**BT ghi nhận:**\n' ||
    E'```\n' ||
    E'Nợ 131                    tổng\n' ||
    E'   Có 5111 (thiết bị)     giá thiết bị\n' ||
    E'   Có 5113 (dịch vụ)      giá dịch vụ\n' ||
    E'   Có 33311               VAT 5% + VAT 10%\n' ||
    E'```\n\n' ||
    E'## Case 4: Bán cho đại lý cấp 2 với chiết khấu thương mại 8%\n' ||
    E'Chiết khấu ghi ngay trên hoá đơn → giảm doanh thu ghi nhận, KHÔNG dùng TK 5211.\n' ||
    E'Chiết khấu ngoài hoá đơn (thưởng doanh số quý) → mới dùng 5211.\n\n' ||
    E'## Case 5: Hàng bán bị trả lại (khách trả ghế lỗi)\n' ||
    E'```\n' ||
    E'Nợ 5212               (giá bán chưa VAT)\n' ||
    E'Nợ 33311              (VAT ghi giảm)\n' ||
    E'   Có 131 hoặc 111/112\n' ||
    E'\n' ||
    E'Nợ 1561               (nhập lại kho theo giá vốn)\n' ||
    E'   Có 632\n' ||
    E'```\n' ||
    E'Cuối kỳ kết chuyển 5212 giảm doanh thu: `Nợ 5111 / Có 5212`.\n',
    ARRAY['ban-hang','but-toan','vat-5','tt43-2021','viet-dang']::text[]
),

-- Note 5 — Bảo hành
(
    'Quản lý chi phí bảo hành thiết bị nha khoa (TK 352 & 641)',
    E'## Nguyên tắc\n' ||
    E'Theo VAS 18 (Provisions), khi bán thiết bị có bảo hành, DN phải **trích trước chi phí bảo hành** vào giá vốn kỳ bán hàng, KHÔNG đợi lúc phát sinh mới hạch toán.\n\n' ||
    E'## Tỷ lệ trích ước tính tại Việt Đăng (dựa lịch sử 2020-2025)\n' ||
    E'| Dòng sản phẩm | Thời gian BH | Tỷ lệ trích trên doanh thu |\n' ||
    E'|---------------|--------------|----------------------------|\n' ||
    E'| Ghế nha khoa cao cấp (KaVo, Sirona) | 24 tháng | 1.5% |\n' ||
    E'| Ghế nha khoa phổ thông (VN, TQ) | 12 tháng | 3.0% |\n' ||
    E'| Máy X-quang, Scan 3D | 12 tháng | 2.5% |\n' ||
    E'| Tay khoan, autoclave | 12 tháng | 4.0% |\n' ||
    E'| Vật tư tiêu hao | Không BH | 0% |\n\n' ||
    E'## Bút toán khi bán hàng (đồng thời với BT bán hàng)\n' ||
    E'Ví dụ bán ghế KaVo 350tr → trích BH 1.5% = 5.250.000\n' ||
    E'```\n' ||
    E'Nợ 641 (6418)     5.250.000\n' ||
    E'   Có 352 (3521)  5.250.000\n' ||
    E'```\n\n' ||
    E'## Bút toán khi thực tế phát sinh BH\n' ||
    E'Xuất linh kiện thay thế 2tr + công thợ 500k:\n' ||
    E'```\n' ||
    E'Nợ 352            2.500.000\n' ||
    E'   Có 152         2.000.000  (linh kiện)\n' ||
    E'   Có 334/111       500.000  (nhân công)\n' ||
    E'```\n\n' ||
    E'## Cuối năm — điều chỉnh dự phòng\n' ||
    E'- Nếu 352 dư > mức BH ước tính cần thiết cho các hợp đồng còn hiệu lực → hoàn nhập: `Nợ 352 / Có 641`\n' ||
    E'- Nếu thiếu → trích bổ sung: `Nợ 641 / Có 352`\n\n' ||
    E'## Theo dõi chi tiết\n' ||
    E'Mở sổ 352 chi tiết theo lô hàng + serial number → giúp đối chiếu chi phí BH thực tế vs trích trước theo từng dòng sản phẩm để hiệu chỉnh tỷ lệ trích năm sau.\n',
    ARRAY['bao-hanh','tk-352','du-phong','vas-18','viet-dang']::text[]
),

-- Note 6 — Công nợ
(
    'Quản lý công nợ phải thu khách hàng (TK 131) & phải trả NCC (TK 331)',
    E'## Chính sách công nợ Việt Đăng\n' ||
    E'### Phải thu (131) — chính sách bán chịu\n' ||
    E'| Loại khách | Hạn mức tín dụng | Kỳ hạn thanh toán |\n' ||
    E'|-----------|-----------------|--------------------|\n' ||
    E'| Phòng khám tư mới | 0đ (thanh toán trước 100%) | N/A |\n' ||
    E'| Phòng khám tư quen (>1 năm) | 500tr | Net 30 |\n' ||
    E'| Bệnh viện công | 2 tỷ | Net 60-90 (theo thoả thuận đấu thầu) |\n' ||
    E'| Đại lý cấp 2 | 1 tỷ | Net 45 |\n' ||
    E'| Trường Y-Nha | 300tr | Net 30 |\n\n' ||
    E'### Phải trả (331) — chính sách với NCC\n' ||
    E'| NCC | Kỳ hạn | Hình thức |\n' ||
    E'|-----|--------|-----------|\n' ||
    E'| KaVo, Sirona, Planmeca (Đức, Nhật) | T/T 30% deposit + 70% before shipment | LC hoặc T/T EUR |\n' ||
    E'| NCC Trung Quốc | 30% deposit + 70% B/L copy | T/T USD |\n' ||
    E'| NCC nội địa VN | Net 30 | CK VND |\n\n' ||
    E'## Quy trình đối chiếu công nợ\n' ||
    E'1. **Hàng tháng** (ngày 25): Kế toán công nợ lập biên bản đối chiếu gửi cho từng khách có phát sinh trong tháng\n' ||
    E'2. **Hàng quý** (cuối Q1/Q2/Q3): Đối chiếu chi tiết + xác nhận số dư với top 20 khách + top 10 NCC\n' ||
    E'3. **Cuối năm** (31/12): Bắt buộc đối chiếu 100% khách/NCC có số dư ≥ 100tr, thu về biên bản có chữ ký/con dấu 2 bên → phục vụ kiểm toán\n\n' ||
    E'## Dự phòng phải thu khó đòi (TK 2293)\n' ||
    E'Theo TT 48/2019/TT-BTC:\n' ||
    E'- Nợ quá hạn 6-12 tháng: trích 30%\n' ||
    E'- Nợ quá hạn 1-2 năm: trích 50%\n' ||
    E'- Nợ quá hạn 2-3 năm: trích 70%\n' ||
    E'- Nợ quá hạn >3 năm: trích 100%\n\n' ||
    E'**BT trích dự phòng cuối năm:**\n' ||
    E'```\n' ||
    E'Nợ 642 (6426)    số cần trích\n' ||
    E'   Có 2293       số cần trích\n' ||
    E'```\n' ||
    E'Năm sau nếu số cần trích < số đã trích → hoàn nhập: `Nợ 2293 / Có 642`.\n' ||
    E'Nếu xoá sổ khoản nợ không đòi được: `Nợ 2293 (phần đã trích), Nợ 642 (phần chưa trích) / Có 131`, đồng thời ghi Nợ TK 004 (nợ khó đòi đã xử lý) để tiếp tục theo dõi ngoài bảng.\n',
    ARRAY['cong-no','131','331','du-phong-kho-doi','tt48-2019']::text[]
),

-- Note 7 — Kho
(
    'Quản lý tồn kho & định giá xuất kho (TK 156)',
    E'## Phương pháp áp dụng tại Việt Đăng\n' ||
    E'- **Phương pháp hạch toán:** Kê khai thường xuyên (không dùng kiểm kê định kỳ)\n' ||
    E'- **Phương pháp tính giá xuất kho:** Bình quân gia quyền cuối kỳ (tính lại giá bình quân sau mỗi lần nhập)\n' ||
    E'- **Đơn vị theo dõi:** theo từng SKU + serial number (do thiết bị nha khoa có giá trị cao + phải trace nguồn gốc bảo hành)\n\n' ||
    E'## Kiểm kê định kỳ\n' ||
    E'| Loại kho | Tần suất | Ai làm |\n' ||
    E'|---------|---------|--------|\n' ||
    E'| Kho thiết bị chính | 3 tháng/lần | KT kho + Trưởng kho + đại diện KD |\n' ||
    E'| Kho vật tư tiêu hao | Hàng tháng | KT kho + Thủ kho |\n' ||
    E'| Kho phụ tùng bảo hành (152) | 6 tháng/lần | KT kho + Kỹ thuật viên trưởng |\n' ||
    E'| Toàn kho cuối năm | 31/12 | Ban kiểm kê + kiểm toán độc lập |\n\n' ||
    E'## Xử lý chênh lệch kiểm kê\n\n' ||
    E'**Thừa kho (kiểm kê > sổ):**\n' ||
    E'```\n' ||
    E'Nợ 156     giá trị thừa\n' ||
    E'   Có 3381 tài sản thừa chờ xử lý\n' ||
    E'```\n' ||
    E'Sau khi tìm nguyên nhân:\n' ||
    E'- Do quên ghi sổ nhập → điều chỉnh về đúng nguồn (Có 331/111...)\n' ||
    E'- Không tìm được nguyên nhân → `Nợ 3381 / Có 711`\n\n' ||
    E'**Thiếu kho (kiểm kê < sổ):**\n' ||
    E'```\n' ||
    E'Nợ 1381    tài sản thiếu chờ xử lý\n' ||
    E'   Có 156\n' ||
    E'```\n' ||
    E'Sau khi xử lý:\n' ||
    E'- Do cá nhân → yêu cầu bồi thường: `Nợ 1388 hoặc 334 / Có 1381`\n' ||
    E'- Do khách quan (bão, cháy, ẩm mốc thiết bị y tế nhạy) → `Nợ 632 hoặc 811 / Có 1381`\n\n' ||
    E'## Hàng ký gửi tại phòng khám (hàng demo)\n' ||
    E'Chưa chuyển quyền sở hữu → theo dõi TK 157 hoặc chi tiết 156-KyGui:\n' ||
    E'```\n' ||
    E'Nợ 157       giá vốn hàng ký gửi\n' ||
    E'   Có 156\n' ||
    E'```\n' ||
    E'Khi phòng khám quyết định mua → xuất hoá đơn:\n' ||
    E'```\n' ||
    E'Nợ 131 / Có 5111 + 33311\n' ||
    E'Nợ 632 / Có 157\n' ||
    E'```\n' ||
    E'Nếu trả lại → chuyển ngược `Nợ 156 / Có 157`.\n\n' ||
    E'## Hàng chậm luân chuyển / lỗi thời\n' ||
    E'Cuối năm rà soát: hàng tồn > 12 tháng chưa bán → trích dự phòng giảm giá TK 2294:\n' ||
    E'```\n' ||
    E'Nợ 632 / Có 2294  (chênh giá thị trường - giá gốc, phần âm)\n' ||
    E'```\n',
    ARRAY['ton-kho','156','kiem-ke','du-phong-giam-gia','viet-dang']::text[]
),

-- Note 8 — Thuế
(
    'Lịch báo cáo thuế & bảo hiểm định kỳ Việt Đăng',
    E'## Báo cáo hàng tháng (chậm nhất ngày 20 tháng sau)\n' ||
    E'| Báo cáo | Mẫu | Ghi chú |\n' ||
    E'|--------|-----|--------|\n' ||
    E'| Tờ khai thuế GTGT | Mẫu 01/GTGT | Kê khai theo phương pháp khấu trừ, chu kỳ THÁNG (do doanh thu > 50 tỷ/năm) |\n' ||
    E'| Tờ khai thuế TNCN khấu trừ tại nguồn | Mẫu 05/KK-TNCN | Nếu số tiền khấu trừ ≥ 50tr/tháng thì kê khai tháng, dưới thì quý |\n' ||
    E'| Báo cáo tình hình sử dụng hoá đơn | BC26/AC | Với hoá đơn điện tử hầu như tự động qua eInvoice của TCT |\n' ||
    E'| Bảo hiểm XH-YT-TN | Trực tuyến qua VNPost/Viettel | Cùng thời hạn |\n\n' ||
    E'## Báo cáo hàng quý (chậm nhất ngày 30 tháng đầu quý sau)\n' ||
    E'| Báo cáo | Mẫu | Ghi chú |\n' ||
    E'|--------|-----|--------|\n' ||
    E'| Tạm tính TNDN quý | (không nộp tờ khai, chỉ nộp tiền tạm nộp) | Tạm nộp ≥ 80% số quyết toán năm để không bị phạt |\n' ||
    E'| Báo cáo tình hình sử dụng hoá đơn quý | BC26/AC | Nếu áp dụng theo quý |\n\n' ||
    E'## Báo cáo hàng năm (chậm nhất 90 ngày sau kết thúc năm tài chính)\n' ||
    E'| Báo cáo | Mẫu | Hạn nộp |\n' ||
    E'|--------|-----|---------|\n' ||
    E'| Quyết toán TNDN | Mẫu 03/TNDN + phụ lục | 31/03 năm sau |\n' ||
    E'| Quyết toán TNCN | Mẫu 05/QTT-TNCN | 31/03 năm sau |\n' ||
    E'| Báo cáo tài chính | Mẫu B01-DN, B02-DN, B03-DN, B09-DN theo TT200 | 31/03 (nộp cho CQT + Sở KHĐT + Thống kê) |\n' ||
    E'| Phí môn bài | Không cần nộp tờ khai từ 2021, chỉ nộp tiền | 30/01 hàng năm |\n\n' ||
    E'## Đặc thù thiết bị y tế\n' ||
    E'- **Không thuộc diện chịu thuế TTĐB** → không phát sinh TK 3332\n' ||
    E'- **VAT đầu vào NK 5%** — cần đối chiếu chặt chẽ với tờ khai HQ, tránh sai lệch chỉ tiêu 25 tờ khai GTGT\n' ||
    E'- **Chuyển giá NCC nước ngoài liên kết:** nếu có mua từ công ty mẹ/liên kết ở nước ngoài → phải khai phụ lục GDLK theo NĐ 132/2020/NĐ-CP (mẫu 01-GDLK khi quyết toán năm)\n\n' ||
    E'## Checklist tuân thủ mỗi kỳ\n' ||
    E'- [ ] Đối chiếu số VAT khấu trừ (1331) trên sổ vs tờ khai\n' ||
    E'- [ ] Kiểm tra 100% hoá đơn > 20 triệu có thanh toán không tiền mặt (điều kiện khấu trừ VAT)\n' ||
    E'- [ ] Rà soát hoá đơn NCC nước ngoài có nộp thay nhà thầu (VAT + TNDN nhà thầu) — chú ý dịch vụ đào tạo, hỗ trợ kỹ thuật online từ hãng\n' ||
    E'- [ ] Xác nhận số dư 131/331 top 20 KH/NCC\n' ||
    E'- [ ] Backup toàn bộ sổ + báo cáo lên cloud + in bản cứng đóng dấu\n',
    ARRAY['bao-cao-thue','tt200','tncn','tndn','vat','viet-dang']::text[]
),

-- Note 9 — Đóng sổ
(
    'Quy trình đóng sổ tháng / quý / năm',
    E'## Đóng sổ tháng (hoàn tất trước ngày 10 tháng sau)\n' ||
    E'### Ngày 1-3:\n' ||
    E'1. Kế toán các phần hành nộp toàn bộ chứng từ tháng trước\n' ||
    E'2. Kế toán tổng hợp check phân loại + hạch toán bổ sung nếu thiếu\n\n' ||
    E'### Ngày 4-6:\n' ||
    E'1. Đối chiếu 111/112 với sổ phụ ngân hàng, biên bản kiểm quỹ\n' ||
    E'2. Đối chiếu 131/331 với sổ chi tiết công nợ\n' ||
    E'3. Đối chiếu 156 với báo cáo NXT của bộ phận kho\n' ||
    E'4. Đối chiếu 1331 với bảng kê hoá đơn đầu vào\n' ||
    E'5. Đối chiếu 33311 với bảng kê hoá đơn đầu ra\n\n' ||
    E'### Ngày 7-8:\n' ||
    E'1. Bút toán kết chuyển doanh thu — chi phí:\n' ||
    E'```\n' ||
    E'-- Kết chuyển các khoản giảm trừ về 511\n' ||
    E'Nợ 5111 / Có 5211, 5212, 5213\n' ||
    E'\n' ||
    E'-- Kết chuyển doanh thu thuần vào xác định KQKD\n' ||
    E'Nợ 5111 / Có 911\n' ||
    E'Nợ 5113 / Có 911\n' ||
    E'\n' ||
    E'-- Kết chuyển giá vốn + chi phí\n' ||
    E'Nợ 911 / Có 632, 635, 641, 642, 811\n' ||
    E'\n' ||
    E'-- Kết chuyển thu nhập khác\n' ||
    E'Nợ 711 / Có 911\n' ||
    E'Nợ 515 / Có 911\n' ||
    E'\n' ||
    E'-- Xác định lãi/lỗ và chuyển 421\n' ||
    E'Nợ 911 / Có 4212  (nếu lãi)\n' ||
    E'Nợ 4212 / Có 911  (nếu lỗ)\n' ||
    E'```\n' ||
    E'2. Trích TNDN tạm nộp quý (nếu tháng cuối quý)\n' ||
    E'3. In sổ nhật ký chung + sổ cái + sổ chi tiết\n\n' ||
    E'### Ngày 9-10:\n' ||
    E'1. Nộp tờ khai VAT + TNCN\n' ||
    E'2. Kế toán trưởng review + ký báo cáo\n' ||
    E'3. Khoá kỳ trên phần mềm — không cho phép sửa lùi\n\n' ||
    E'## Đóng sổ quý — thêm việc\n' ||
    E'- Nộp tiền TNDN tạm nộp\n' ||
    E'- Đối chiếu công nợ top 20 KH + top 10 NCC (biên bản có ký)\n' ||
    E'- Rà soát hàng tồn > 6 tháng (dự phòng giảm giá nếu cần)\n\n' ||
    E'## Đóng sổ năm — checklist chi tiết\n' ||
    E'1. **Kiểm kê toàn bộ TSCĐ + kho + tiền mặt** (31/12) — biên bản có kiểm toán độc lập chứng kiến\n' ||
    E'2. **Đối chiếu 100% công nợ** ≥ 100tr\n' ||
    E'3. **Xử lý chênh lệch tỷ giá cuối năm** (đánh giá lại 112 USD/EUR, 131 gốc ngoại tệ, 331 gốc ngoại tệ):\n' ||
    E'   - Lãi CLTG chưa thực hiện → 413\n' ||
    E'   - Cuối năm kết chuyển 413 → 515 hoặc 635\n' ||
    E'4. **Trích dự phòng:**\n' ||
    E'   - Dự phòng bảo hành (352) — theo tỷ lệ trên doanh thu còn hiệu lực bảo hành\n' ||
    E'   - Dự phòng nợ khó đòi (2293) — TT 48/2019\n' ||
    E'   - Dự phòng giảm giá hàng tồn (2294) — TT 48/2019\n' ||
    E'   - Dự phòng giảm giá đầu tư (nếu có) (2291/2292)\n' ||
    E'5. **Trích khấu hao TSCĐ tháng 12** đầy đủ\n' ||
    E'6. **Kết chuyển 421 (LNCPP) sang các quỹ** theo NQ ĐHĐCĐ:\n' ||
    E'```\n' ||
    E'Nợ 421 / Có 353, 418, 414, 415\n' ||
    E'```\n' ||
    E'7. **Xuất Báo cáo tài chính** (4 biểu) → in + đóng dấu + ký kế toán trưởng + Giám đốc + gửi CQT/Sở KHĐT/Thống kê\n' ||
    E'8. **Lưu trữ chứng từ:** 5 năm hoặc 10 năm tuỳ loại theo Luật Kế toán 2015 điều 41\n',
    ARRAY['dong-so','ket-chuyen','cuoi-nam','viet-dang','tt200']::text[]
),

-- Note 10 — TSCĐ + khấu hao
(
    'Tài sản cố định — xe giao hàng, phòng showroom, kho, thiết bị demo',
    E'## Danh mục TSCĐ điển hình tại Việt Đăng\n' ||
    E'| Loại | Nhóm TK | Thời gian KH (theo TT 45/2013/TT-BTC) |\n' ||
    E'|-----|---------|---------------------------------------|\n' ||
    E'| Nhà kho, showroom (thuê dài hạn/tự xây) | 2111 | 25-50 năm |\n' ||
    E'| Xe tải giao hàng, xe ô tô KD | 2113 | 6-10 năm |\n' ||
    E'| Máy tính, máy in văn phòng | 2114 | 3-8 năm |\n' ||
    E'| Ghế nha khoa dùng làm demo lâu dài (không bán) | 2118 | 8-15 năm |\n' ||
    E'| Phần mềm kế toán, CRM | 2113 (TSCĐ vô hình) | 3-10 năm |\n\n' ||
    E'## Nguyên tắc ghi nhận TSCĐ\n' ||
    E'- Nguyên giá ≥ 30.000.000 VND (theo TT 45/2013)\n' ||
    E'- Thời gian sử dụng > 1 năm\n' ||
    E'- Chắc chắn thu được lợi ích kinh tế trong tương lai\n\n' ||
    E'Không đủ điều kiện → hạch toán TK 242 (chi phí trả trước) và phân bổ ≤ 3 năm.\n\n' ||
    E'## Bút toán mua TSCĐ (mua xe tải 800tr + VAT 10% + phí trước bạ 2% + phí đăng ký 1tr)\n' ||
    E'```\n' ||
    E'Nợ 2113        816.000.000  (800tr + 16tr phí trước bạ)\n' ||
    E'Nợ 1332         80.000.000  (VAT khấu trừ TSCĐ)\n' ||
    E'   Có 331      880.000.000\n' ||
    E'   Có 3339      16.000.000  (phí trước bạ phải nộp)\n' ||
    E'\n' ||
    E'Nợ 2113          1.000.000  (phí đăng ký, biển số)\n' ||
    E'   Có 111        1.000.000\n' ||
    E'```\n\n' ||
    E'## Bút toán khấu hao hàng tháng\n' ||
    E'Xe tải 816tr, KH 8 năm → 816/(8×12) = 8.500.000/tháng\n' ||
    E'```\n' ||
    E'Nợ 6414 (KH xe bán hàng)  8.500.000\n' ||
    E'   Có 2141                8.500.000\n' ||
    E'```\n' ||
    E'Nếu xe dùng cho QLDN thì hạch toán vào 6424 thay vì 6414.\n\n' ||
    E'## Thanh lý TSCĐ (xe cũ)\n' ||
    E'Xe nguyên giá 500tr, đã KH 400tr, còn 100tr. Bán 150tr (chưa VAT):\n' ||
    E'```\n' ||
    E'-- Xoá TSCĐ khỏi sổ\n' ||
    E'Nợ 2141        400.000.000  (KH luỹ kế)\n' ||
    E'Nợ 811         100.000.000  (giá trị còn lại)\n' ||
    E'   Có 2113     500.000.000\n' ||
    E'\n' ||
    E'-- Thu bán\n' ||
    E'Nợ 131 hoặc 111/112   165.000.000\n' ||
    E'   Có 711             150.000.000\n' ||
    E'   Có 33311            15.000.000\n' ||
    E'```\n\n' ||
    E'## Đặc thù hàng demo tại phòng khám ĐỐI TÁC\n' ||
    E'Thiết bị đặt tại phòng khám demo dài hạn (≥ 12 tháng) mà **Việt Đăng vẫn giữ quyền sở hữu** → ghi nhận là TSCĐ (2118) và khấu hao dần, KHÔNG để trong 156 (hàng hoá) vì không có ý định bán ngay.\n' ||
    E'Nếu ≤ 12 tháng và có ý định bán sau khi hết demo → giữ trong 157 (hàng gửi bán) và không trích khấu hao, chỉ ghi giảm giá trị nếu phát sinh hư hỏng.\n',
    ARRAY['tscd','khau-hao','tt45-2013','xe-giao-hang','demo']::text[]
)

) AS t(title, content, tags)
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- 6. Attach 10 note vào vd-accounting
-- ---------------------------------------------------------------------------
INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
SELECT a.id, n.id, (SELECT id FROM users WHERE email='admin@vietdang.vn')
FROM agents a
JOIN tenants t ON t.id=a.tenant_id
CROSS JOIN notes n
WHERE t.code='vietdang' AND a.code='vd-accounting'
  AND n.tenant_id = t.id
  AND n.title IN (
      'Giới thiệu CTCP Thiết bị Nha khoa Việt Đăng',
      'Hệ thống tài khoản kế toán áp dụng tại Việt Đăng (theo TT200)',
      'Nghiệp vụ mua hàng nhập khẩu — hạch toán từ A đến Z',
      'Nghiệp vụ bán hàng — phòng khám, bệnh viện, đại lý',
      'Quản lý chi phí bảo hành thiết bị nha khoa (TK 352 & 641)',
      'Quản lý công nợ phải thu khách hàng (TK 131) & phải trả NCC (TK 331)',
      'Quản lý tồn kho & định giá xuất kho (TK 156)',
      'Lịch báo cáo thuế & bảo hiểm định kỳ Việt Đăng',
      'Quy trình đóng sổ tháng / quý / năm',
      'Tài sản cố định — xe giao hàng, phòng showroom, kho, thiết bị demo'
  )
ON CONFLICT DO NOTHING;

COMMIT;

-- ---------------------------------------------------------------------------
-- Verify
-- ---------------------------------------------------------------------------
\echo ''
\echo '=== Việt Đăng Tenant ==='
SELECT code, name, status FROM tenants WHERE code='vietdang';

\echo ''
\echo '=== Việt Đăng Users + Roles ==='
SELECT u.email, u.display_name,
       (SELECT string_agg(r.name, ',') FROM user_roles ur JOIN roles r ON r.id=ur.role_id WHERE ur.user_id=u.id) AS roles,
       (SELECT role FROM tenant_members tm JOIN tenants t ON t.id=tm.tenant_id WHERE tm.user_id=u.id AND t.code='vietdang') AS tenant_role
FROM users u WHERE u.email LIKE '%@vietdang.vn' ORDER BY u.email;

\echo ''
\echo '=== Việt Đăng Agent ==='
SELECT a.code, a.name, a.lifecycle_state, a.action_tier,
       (SELECT email FROM users WHERE id=a.owner_user_id) AS owner,
       (SELECT COUNT(*) FROM agent_knowledge WHERE agent_id=a.id) AS notes_attached,
       (SELECT COUNT(*) FROM agent_acl WHERE agent_id=a.id) AS acl_grants,
       a.config->>'model' AS model,
       a.config->'skills' AS skills
FROM agents a JOIN tenants t ON t.id=a.tenant_id
WHERE t.code='vietdang';

\echo ''
\echo '=== Việt Đăng Notes ==='
SELECT n.title, array_length(n.tags, 1) AS tag_count, length(n.content) AS content_length
FROM notes n
JOIN tenants t ON t.id=n.tenant_id
WHERE t.code='vietdang'
ORDER BY n.created_at;
