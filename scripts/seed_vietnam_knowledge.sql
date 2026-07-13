-- ============================================================================
-- Seed realistic Vietnam-native knowledge for 3 domain agents.
-- Idempotent: uses INSERT ... WHERE NOT EXISTS on notes.title (per tenant),
-- and ON CONFLICT DO NOTHING for agent_knowledge attach mappings.
--
-- Sources: content synthesized from public Vietnamese legal documents
-- (Luật LĐ 45/2019/QH14, TT 200/2014/TT-BTC, NĐ 74/2024/NĐ-CP, Luật thuế
-- GTGT/TNCN/TNDN, NĐ 123/2020/NĐ-CP về hoá đơn điện tử). For production
-- use, verify against thuvienphapluat.vn or official gazette.
-- ============================================================================

BEGIN;

-- Set defaults for the seed session so INSERTs don't need to repeat the ids
\set author '(SELECT id FROM users WHERE email=''tuong247@gmail.com'')'
\set tenant '(SELECT id FROM tenants WHERE code=''default'')'

-- Helper: insert note if title doesn't already exist for this tenant/user
CREATE OR REPLACE FUNCTION _seed_note(
    p_title TEXT,
    p_content TEXT,
    p_tags TEXT[]
) RETURNS UUID AS $$
DECLARE
    v_note_id UUID;
    v_user_id UUID;
    v_tenant_id UUID;
BEGIN
    SELECT id INTO v_user_id FROM users WHERE email='tuong247@gmail.com';
    SELECT id INTO v_tenant_id FROM tenants WHERE code='default';

    -- Return existing note id if title matches
    SELECT id INTO v_note_id
    FROM notes
    WHERE tenant_id = v_tenant_id AND user_id = v_user_id AND title = p_title
    LIMIT 1;

    IF v_note_id IS NULL THEN
        INSERT INTO notes (user_id, tenant_id, title, content, tags)
        VALUES (v_user_id, v_tenant_id, p_title, p_content, p_tags)
        RETURNING id INTO v_note_id;
    END IF;

    RETURN v_note_id;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- BỘ 2 — KẾ TOÁN (attach vào kt-bookkeeper)
-- ============================================================================

DO $$
DECLARE
    v_agent_id UUID;
    v_note_id UUID;
BEGIN
    SELECT a.id INTO v_agent_id FROM agents a
    JOIN tenants t ON t.id=a.tenant_id
    WHERE t.code='default' AND a.code='kt-bookkeeper';

    -- 1. Chart of accounts TT 200
    v_note_id := _seed_note(
        'Hệ thống tài khoản kế toán theo TT 200/2014/TT-BTC',
        E'Thông tư 200/2014/TT-BTC quy định hệ thống tài khoản DN, chia 9 nhóm chính:\n\n' ||
        E'**Loại 1 — Tài sản ngắn hạn:**\n' ||
        E'- 111 Tiền mặt · 112 Tiền gửi ngân hàng · 113 Tiền đang chuyển\n' ||
        E'- 121 Chứng khoán KD · 128 Đầu tư ngắn hạn khác\n' ||
        E'- 131 Phải thu KH · 133 Thuế GTGT được khấu trừ · 138 Phải thu khác\n' ||
        E'- 141 Tạm ứng · 152 Nguyên vật liệu · 153 Công cụ dụng cụ · 154 CP SXKD dở dang\n' ||
        E'- 155 Thành phẩm · 156 Hàng hoá · 157 Hàng gửi bán\n\n' ||
        E'**Loại 2 — Tài sản dài hạn:**\n' ||
        E'- 211 TSCĐ hữu hình · 213 TSCĐ vô hình · 214 Hao mòn TSCĐ (luỹ kế)\n' ||
        E'- 217 Bất động sản đầu tư · 221 Đầu tư vào công ty con · 222 Đầu tư liên doanh\n' ||
        E'- 242 Chi phí trả trước dài hạn · 244 Cầm cố ký quỹ ký cược dài hạn\n\n' ||
        E'**Loại 3 — Nợ phải trả:**\n' ||
        E'- 331 Phải trả người bán · 333 Thuế và các khoản phải nộp NN\n' ||
        E'  - 33311 VAT đầu ra · 3334 Thuế TNDN · 3335 Thuế TNCN · 3336 Thuế tài nguyên\n' ||
        E'- 334 Phải trả NLĐ · 335 Chi phí phải trả · 336 Phải trả nội bộ\n' ||
        E'- 338 Phải trả phải nộp khác (3383 BHXH, 3384 BHYT, 3386 BHTN, 3388 KPCĐ)\n' ||
        E'- 341 Vay và nợ thuê tài chính · 353 Quỹ khen thưởng phúc lợi\n\n' ||
        E'**Loại 4 — Vốn chủ sở hữu:**\n' ||
        E'- 411 Vốn góp · 412 Chênh lệch đánh giá lại TS · 413 Chênh lệch tỷ giá HĐ\n' ||
        E'- 414 Quỹ đầu tư phát triển · 421 LNST chưa phân phối\n\n' ||
        E'**Loại 5 — Doanh thu:** 511 Bán hàng · 515 Doanh thu TC · 521 Các khoản giảm trừ DT\n\n' ||
        E'**Loại 6 — Chi phí SXKD:** 621/622/627 CP sản xuất · 632 Giá vốn · 635 CP tài chính · 641 CP bán hàng · 642 CP quản lý DN\n\n' ||
        E'**Loại 7 — Thu nhập khác:** 711\n\n' ||
        E'**Loại 8 — Chi phí khác:** 811 CP khác · 821 CP thuế TNDN\n\n' ||
        E'**Loại 9 — Xác định KQKD:** 911',
        ARRAY['ke-toan','tt200','chart-of-accounts','vas']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 2. Bút toán bán hàng có VAT
    v_note_id := _seed_note(
        'Bút toán bán hàng có VAT — chuẩn TT 200',
        E'**Bán hàng chưa thu tiền (khách nợ), VAT 10%:**\n' ||
        E'Ví dụ: bán hàng 100tr chưa VAT, VAT 10%.\n\n' ||
        E'```\n' ||
        E'Nợ TK 131 - Phải thu KH:            110.000.000\n' ||
        E'    Có TK 511 - Doanh thu bán hàng:  100.000.000\n' ||
        E'    Có TK 33311 - VAT đầu ra:         10.000.000\n' ||
        E'```\n\n' ||
        E'**Đồng thời ghi giá vốn:**\n' ||
        E'```\n' ||
        E'Nợ TK 632 - Giá vốn hàng bán:        70.000.000\n' ||
        E'    Có TK 156 - Hàng hoá:            70.000.000\n' ||
        E'```\n\n' ||
        E'**Bán hàng thu tiền ngay:**\n' ||
        E'Thay TK 131 bằng TK 111 (tiền mặt) hoặc TK 112 (tiền gửi NH).\n\n' ||
        E'**Lưu ý:** VAT thuộc TK 33311 (đầu ra), không phải TK 511. Nhầm chỗ này làm sai cả doanh thu và số thuế phải nộp.',
        ARRAY['bao-toan','vat','doanh-thu','tt200']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 3. Bút toán mua hàng có VAT
    v_note_id := _seed_note(
        'Bút toán mua nguyên vật liệu có VAT đầu vào',
        E'Ví dụ: mua NVL 50tr chưa VAT, VAT 10%, trả tiền ngay.\n\n' ||
        E'```\n' ||
        E'Nợ TK 152 - Nguyên vật liệu:         50.000.000\n' ||
        E'Nợ TK 1331 - VAT đầu vào KT:          5.000.000\n' ||
        E'    Có TK 111 - Tiền mặt:            55.000.000\n' ||
        E'```\n\n' ||
        E'**Nếu mua chịu:** thay TK 111 bằng TK 331 (Phải trả người bán).\n\n' ||
        E'**Cảnh báo quan trọng — TT 78/2014 Đ.6:**\n' ||
        E'Nếu hoá đơn > 20 triệu và thanh toán bằng **tiền mặt** → **VAT KHÔNG được khấu trừ**. Bắt buộc chuyển khoản qua NH có chứng từ.\n\n' ||
        E'**Phân biệt TK 1331 và TK 1332:**\n' ||
        E'- 1331 = VAT được khấu trừ (hoá đơn hợp lệ)\n' ||
        E'- 1332 = VAT không được khấu trừ (thiếu HĐ, DN nộp thuế trực tiếp)',
        ARRAY['bao-toan','vat','nvl','tt200']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 4. Tính lương + BHXH
    v_note_id := _seed_note(
        'Bút toán tính lương + BHXH/BHYT/BHTN 2026',
        E'**Tỷ lệ trích 2026 (theo NĐ 143/2018 và các văn bản liên quan):**\n\n' ||
        E'| Loại | NLĐ trả | DN trả | Tổng |\n' ||
        E'|------|---------|--------|------|\n' ||
        E'| BHXH | 8% | 17% | 25% |\n' ||
        E'| BHYT | 1.5% | 3% | 4.5% |\n' ||
        E'| BHTN | 1% | 1% | 2% |\n' ||
        E'| TNLĐ-BNN | — | 0.5% | 0.5% |\n' ||
        E'| **Tổng** | **10.5%** | **21.5%** | **32%** |\n\n' ||
        E'**Ví dụ:** lương gross 30tr, tính lương tháng:\n\n' ||
        E'**Bước 1 — Ghi nhận chi phí lương:**\n' ||
        E'```\n' ||
        E'Nợ TK 642 - CP quản lý DN:      30.000.000  (lương gross)\n' ||
        E'    Có TK 334 - Phải trả NLĐ:   30.000.000\n' ||
        E'```\n\n' ||
        E'**Bước 2 — Trích BHXH/BHYT/BHTN DN chịu (21.5%):**\n' ||
        E'```\n' ||
        E'Nợ TK 642 - CP quản lý DN:       6.450.000\n' ||
        E'    Có TK 3383 - BHXH:           5.100.000  (17%)\n' ||
        E'    Có TK 3384 - BHYT:             900.000  (3%)\n' ||
        E'    Có TK 3386 - BHTN:             300.000  (1%)\n' ||
        E'    Có TK 3388 - TNLĐ-BNN:         150.000  (0.5%)\n' ||
        E'```\n\n' ||
        E'**Bước 3 — Khấu trừ BHXH NLĐ (10.5%) + TNCN, thanh toán:**\n' ||
        E'```\n' ||
        E'Nợ TK 334:                      30.000.000\n' ||
        E'    Có TK 3383 - BHXH NLĐ:       2.400.000  (8%)\n' ||
        E'    Có TK 3384 - BHYT NLĐ:         450.000  (1.5%)\n' ||
        E'    Có TK 3386 - BHTN NLĐ:         300.000  (1%)\n' ||
        E'    Có TK 3335 - TNCN:           <tính riêng>\n' ||
        E'    Có TK 111 - Tiền mặt:       <phần net>\n' ||
        E'```\n\n' ||
        E'**Trần lương đóng BHXH:** 20 × lương cơ sở (2026: 2.340.000) = 46.800.000/tháng.',
        ARRAY['luong','bhxh','bhyt','bhtn','tt200']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 5. Khấu hao TSCĐ
    v_note_id := _seed_note(
        'Khấu hao TSCĐ — phương pháp đường thẳng (TT 45/2013)',
        E'**Điều kiện ghi nhận TSCĐ (theo TT 45/2013/TT-BTC):**\n' ||
        E'1. Chắc chắn thu được lợi ích kinh tế tương lai\n' ||
        E'2. Thời gian sử dụng > 1 năm\n' ||
        E'3. Nguyên giá > 30 triệu VNĐ\n\n' ||
        E'**Phương pháp đường thẳng:**\n' ||
        E'```\n' ||
        E'Khấu hao/tháng = Nguyên giá / (Số năm × 12)\n' ||
        E'```\n\n' ||
        E'**Khung thời gian khấu hao (Phụ lục 1 TT 45/2013):**\n' ||
        E'| Loại TSCĐ | Min | Max |\n' ||
        E'|-----------|-----|-----|\n' ||
        E'| Nhà xưởng, kiến trúc | 5 năm | 50 năm |\n' ||
        E'| Máy móc thiết bị SX | 5 năm | 15 năm |\n' ||
        E'| Phương tiện vận tải | 6 năm | 15 năm |\n' ||
        E'| Thiết bị QL (máy tính, máy in) | 3 năm | 10 năm |\n' ||
        E'| TSCĐ vô hình (phần mềm) | 3 năm | 20 năm |\n\n' ||
        E'**Bút toán trích khấu hao hàng tháng:**\n' ||
        E'```\n' ||
        E'Nợ TK 627/641/642 - Chi phí:    <mức khấu hao>\n' ||
        E'    Có TK 214 - HM luỹ kế TSCĐ: <mức khấu hao>\n' ||
        E'```\n\n' ||
        E'Chọn TK chi phí theo mục đích: 627 nếu dùng SX, 641 nếu BH, 642 nếu QL.',
        ARRAY['tscd','khau-hao','tt45','tt200']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 6. VAT rates
    v_note_id := _seed_note(
        'Thuế suất VAT (GTGT) 2026 và cách áp dụng',
        E'**Bốn mức thuế suất VAT hiện hành:**\n\n' ||
        E'| Mức | Đối tượng áp dụng |\n' ||
        E'|-----|-------------------|\n' ||
        E'| **0%** | Hàng xuất khẩu, dịch vụ XK, vận tải quốc tế |\n' ||
        E'| **5%** | Nước sạch sinh hoạt, thuốc + thiết bị y tế, sách giáo khoa, nông sản sơ chế, dịch vụ khoa học kỹ thuật |\n' ||
        E'| **8%** | Theo Nghị quyết giảm 2% cho một số ngành (áp dụng có thời hạn — kiểm tra Nghị quyết mới nhất) |\n' ||
        E'| **10%** | Mặc định cho phần lớn hàng hoá + dịch vụ khác |\n\n' ||
        E'**Cách tính:**\n\n' ||
        E'*VAT đã bao gồm trong giá:*\n' ||
        E'```\n' ||
        E'VAT = Giá thanh toán × Thuế suất / (1 + Thuế suất)\n' ||
        E'Ví dụ HĐ 110tr đã gồm 10%: VAT = 110 × 10/110 = 10tr\n' ||
        E'```\n\n' ||
        E'*VAT chưa gồm trong giá:*\n' ||
        E'```\n' ||
        E'VAT = Giá chưa VAT × Thuế suất\n' ||
        E'Ví dụ giá 100tr + VAT 10% = 10tr → tổng 110tr\n' ||
        E'```\n\n' ||
        E'**VAT phải nộp cuối kỳ:**\n' ||
        E'```\n' ||
        E'VAT phải nộp = VAT đầu ra - VAT đầu vào được khấu trừ\n' ||
        E'```\n' ||
        E'- Số dương → DN nộp NSNN\n' ||
        E'- Số âm → khấu trừ kỳ sau; xin hoàn nếu đủ điều kiện (đầu vào tồn > 300tr trong 12 tháng, hoặc xuất khẩu)',
        ARRAY['vat','gtgt','thue-suat']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 7. TNCN lũy tiến
    v_note_id := _seed_note(
        'Thuế TNCN — biểu lũy tiến từng phần + giảm trừ 2026',
        E'**Biểu thuế TNCN lũy tiến từng phần (áp dụng cho lương, tiền công thường xuyên):**\n\n' ||
        E'| Bậc | Thu nhập tính thuế/tháng | Thuế suất |\n' ||
        E'|-----|--------------------------|-----------|\n' ||
        E'| 1 | đến 5 triệu | 5% |\n' ||
        E'| 2 | 5 → 10 triệu | 10% |\n' ||
        E'| 3 | 10 → 18 triệu | 15% |\n' ||
        E'| 4 | 18 → 32 triệu | 20% |\n' ||
        E'| 5 | 32 → 52 triệu | 25% |\n' ||
        E'| 6 | 52 → 80 triệu | 30% |\n' ||
        E'| 7 | trên 80 triệu | 35% |\n\n' ||
        E'**Giảm trừ (theo Nghị quyết 954/2020/UBTVQH14):**\n' ||
        E'- Bản thân: **11.000.000 VNĐ/tháng** (132tr/năm)\n' ||
        E'- Người phụ thuộc: **4.400.000 VNĐ/người/tháng** — phải đăng ký NPT\n' ||
        E'- BHXH/BHYT/BHTN NLĐ trả (10.5%): trừ trước khi tính TNTT\n\n' ||
        E'**Công thức:**\n' ||
        E'```\n' ||
        E'TNTT = Lương gross - BH bắt buộc - 11tr - (4.4tr × NPT)\n' ||
        E'TNCN = tính lũy tiến từ TNTT\n' ||
        E'```\n\n' ||
        E'**Ví dụ TNTT = 25tr → tính lũy tiến:**\n' ||
        E'- Bậc 1: 5tr × 5% = 250.000\n' ||
        E'- Bậc 2: 5tr × 10% = 500.000\n' ||
        E'- Bậc 3: 8tr × 15% = 1.200.000\n' ||
        E'- Bậc 4: 7tr × 20% = 1.400.000\n' ||
        E'- **Tổng TNCN = 3.350.000 VNĐ**',
        ARRAY['tncn','luong','giam-tru','luy-tien']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 8. TNDN
    v_note_id := _seed_note(
        'Thuế TNDN 2026 — thuế suất, chi phí được trừ',
        E'**Thuế suất TNDN:**\n' ||
        E'| Mức | Đối tượng |\n' ||
        E'|-----|-----------|\n' ||
        E'| 20% | Mặc định |\n' ||
        E'| 17% | DN nhỏ đủ điều kiện (theo NQ 43/2022 kéo dài) |\n' ||
        E'| 15% | DN siêu nhỏ |\n' ||
        E'| 10% | Khu công nghệ cao, dự án ưu đãi đặc biệt |\n' ||
        E'| 5-10% | Địa bàn KKT, miền núi khó khăn |\n' ||
        E'| Miễn 4 năm + giảm 50% trong 9 năm | DN mới tại địa bàn đặc biệt khó khăn |\n\n' ||
        E'**Công thức:**\n' ||
        E'```\n' ||
        E'Thu nhập tính thuế = Doanh thu - CP được trừ - Lỗ kết chuyển - TN miễn thuế\n' ||
        E'Thuế TNDN = Thu nhập tính thuế × Thuế suất\n' ||
        E'```\n\n' ||
        E'**Chi phí KHÔNG được trừ (loại phổ biến):**\n' ||
        E'1. Chi phí không có HĐ hợp lệ\n' ||
        E'2. Chi phí > 20tr thanh toán tiền mặt\n' ||
        E'3. Chi trả cho lao động thuê ngoài không có HĐLĐ + BHXH\n' ||
        E'4. Tiền phạt hành chính, phạt vi phạm hợp đồng > mức luật cho phép\n' ||
        E'5. Chi phí không phục vụ SXKD (mua rượu, quà biếu cá nhân...)\n' ||
        E'6. Trích trước CP không có căn cứ\n' ||
        E'7. Khấu hao TSCĐ vượt mức thời gian theo TT 45/2013\n\n' ||
        E'**Hạn nộp quyết toán TNDN:** ngày cuối tháng thứ 3 sau năm tài chính (tức 31/3 hoặc 31/N+3).',
        ARRAY['tndn','thue-suat','chi-phi-duoc-tru']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 9. Hạn nộp báo cáo
    v_note_id := _seed_note(
        'Hạn nộp báo cáo thuế 2026 — bảng tra nhanh',
        E'| Báo cáo | Hạn nộp | Ghi chú |\n' ||
        E'|---------|---------|--------|\n' ||
        E'| Tờ khai VAT tháng | Ngày 20 tháng sau | DN kê khai tháng (DT ≥ 50 tỷ/năm) |\n' ||
        E'| Tờ khai VAT quý | Ngày 30 tháng đầu quý sau | DN kê khai quý (DT < 50 tỷ) |\n' ||
        E'| Tờ khai TNCN tháng | Ngày 20 tháng sau | Có phát sinh khấu trừ |\n' ||
        E'| Tờ khai TNCN quý | Ngày cuối tháng đầu quý sau | Doanh nghiệp nhỏ |\n' ||
        E'| Quyết toán TNCN năm — cá nhân | 31/3 năm sau | Tự quyết toán |\n' ||
        E'| Quyết toán TNCN năm — DN | 31/3 năm sau (90 ngày) | DN khấu trừ cho NLĐ |\n' ||
        E'| Quyết toán TNDN năm | Ngày cuối tháng 3 năm sau | 90 ngày sau kết thúc năm TC |\n' ||
        E'| Nộp thuế TNDN tạm quý | Ngày 30 tháng đầu quý sau | 4 lần/năm |\n' ||
        E'| BCTC năm | 90 ngày sau kết thúc năm TC | Nộp cùng QT TNDN |\n' ||
        E'| BHXH/BHYT/BHTN tháng | Ngày cuối tháng đó | Trích + nộp |\n' ||
        E'| Hoá đơn điện tử — báo cáo | Kê khai cùng VAT | Theo NĐ 123/2020 |\n\n' ||
        E'**Cảnh báo:** Nếu ngày cuối kỳ rơi vào **thứ 7/chủ nhật/lễ**, hạn được lùi đến ngày làm việc tiếp theo (theo Luật Quản lý thuế Điều 45).',
        ARRAY['han-nop','bao-cao','deadline','thue']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 10. Đóng sổ cuối kỳ
    v_note_id := _seed_note(
        'Checklist đóng sổ cuối tháng/quý/năm',
        E'**Bắt buộc chạy đúng thứ tự:**\n\n' ||
        E'☐ 1. Đối chiếu tồn quỹ TK 111 với sổ quỹ vật lý\n' ||
        E'☐ 2. Đối chiếu sổ phụ NH với TK 112 (từng tài khoản)\n' ||
        E'☐ 3. Gửi biên bản đối chiếu công nợ TK 131 (khách hàng) + TK 331 (NCC) — có xác nhận\n' ||
        E'☐ 4. Kiểm kê tồn kho vật lý — đối chiếu TK 152/153/156\n' ||
        E'☐ 5. Trích khấu hao TSCĐ — bảng tính khấu hao theo TT 45/2013\n' ||
        E'☐ 6. Phân bổ CCDC + CP trả trước (TK 242 → 641/642/627)\n' ||
        E'☐ 7. Trích lập dự phòng (nợ khó đòi TK 229, giảm giá tồn kho TK 2294)\n' ||
        E'☐ 8. Đánh giá chênh lệch tỷ giá (nếu có ngoại tệ) — TK 413\n' ||
        E'☐ 9. Kết chuyển VAT: 33311 ↔ 1331\n' ||
        E'☐ 10. Kết chuyển doanh thu → TK 911: 511/515/711 → 911\n' ||
        E'☐ 11. Kết chuyển chi phí → TK 911: 632/635/641/642/811/821 → 911\n' ||
        E'☐ 12. Xác định lãi/lỗ: nếu lãi Nợ 911/Có 421, nếu lỗ Nợ 421/Có 911\n' ||
        E'☐ 13. Lập BCTC:\n' ||
        E'   - BCĐKT (B01-DN)\n' ||
        E'   - BCKQHĐKD (B02-DN)\n' ||
        E'   - BCLCTT (B03-DN)\n' ||
        E'   - Thuyết minh BCTC (B09-DN)\n' ||
        E'☐ 14. Đối chiếu số liệu BCTC với sổ cái các TK\n' ||
        E'☐ 15. In sổ + lưu chứng từ theo Luật Kế toán (lưu 10 năm với chứng từ, 20+ với BCTC)',
        ARRAY['dong-so','checklist','bctc','quy-trinh']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 11-15: shortened for brevity, more notes here...
    v_note_id := _seed_note(
        'Hoá đơn điện tử theo NĐ 123/2020/NĐ-CP',
        E'**Bắt buộc từ 1/7/2022** tất cả DN dùng hoá đơn điện tử (không dùng HĐ giấy tự in nữa).\n\n' ||
        E'**Hai loại:**\n' ||
        E'1. **HĐĐT có mã CQT** — DN nhỏ, DN mới thành lập\n' ||
        E'2. **HĐĐT không có mã CQT** — DN đủ điều kiện (kê khai VAT theo phương pháp khấu trừ, có hệ thống CNTT đạt chuẩn)\n\n' ||
        E'**Thời điểm xuất HĐĐT:**\n' ||
        E'- Bán hàng: khi chuyển giao quyền sở hữu\n' ||
        E'- Dịch vụ: khi hoàn thành cung cấp\n' ||
        E'- Xây dựng lắp đặt: theo tiến độ nghiệm thu\n\n' ||
        E'**Xử lý HĐ sai sót (Đ.19 NĐ 123):**\n' ||
        E'- Chưa gửi cho bên mua → huỷ, xuất lại\n' ||
        E'- Đã gửi + chưa kê khai → thoả thuận với bên mua, huỷ + xuất lại\n' ||
        E'- Đã kê khai → xuất **HĐ điều chỉnh** (không huỷ HĐ gốc)\n\n' ||
        E'**Nhà cung cấp phổ biến VN:** MISA MeInvoice, Viettel Invoice, VNPT-Invoice, EasyInvoice, BKAV eHoaDon.',
        ARRAY['hoa-don','hddt','nd123','vat']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    v_note_id := _seed_note(
        'Các sai sót kế toán phổ biến — checklist tránh',
        E'☒ **1. Nhầm TK VAT** — ghi VAT vào TK 511 thay vì 33311, hoặc TK 156 thay vì 1331. Làm sai cả doanh thu + số thuế.\n\n' ||
        E'☒ **2. Bỏ quên bút toán giá vốn** — chỉ ghi doanh thu, không kết chuyển 632 → lãi ảo, hết năm quyết toán sai TNDN.\n\n' ||
        E'☒ **3. Thanh toán > 20tr bằng tiền mặt** — VAT không được khấu trừ, chi phí bị loại khi quyết toán TNDN (TT 78/2014 Đ.6).\n\n' ||
        E'☒ **4. Không có HĐ đầu vào cho chi phí** — không được khấu trừ VAT, bị loại chi phí.\n\n' ||
        E'☒ **5. Vào sổ sai tháng** — HĐ cuối tháng 3 nhưng vào tháng 4 → sai kỳ VAT + kết chuyển. Nhìn ngày phát hành HĐ, không phải ngày nhận.\n\n' ||
        E'☒ **6. Quên đăng ký NPT** — NLĐ mất 4.4tr/người giảm trừ TNCN. Đăng ký trong 3 tháng đầu năm hoặc khi có sự kiện.\n\n' ||
        E'☒ **7. Trích trước chi phí không có căn cứ** — TK 335 phải có dự toán/hợp đồng cụ thể, không được trích tuỳ ý.\n\n' ||
        E'☒ **8. Không lập bảng phân bổ CCDC / CP trả trước** — TK 242 phải phân bổ đều, không ghi hết 1 lần.\n\n' ||
        E'☒ **9. Khấu hao vượt khung TT 45/2013** — VD khấu hao ô tô 3 năm thay vì tối thiểu 6 năm → phần vượt bị loại.\n\n' ||
        E'☒ **10. Đối chiếu công nợ thiếu** — cuối kỳ không gửi biên bản đối chiếu KH/NCC → khi kiểm toán bị coi là số dư không tin cậy.',
        ARRAY['sai-sot','kiem-tra','checklist','tt78']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

END $$;

-- ============================================================================
-- BỘ 3 — HR / BLLĐ 2019 (attach vào hr-assistant)
-- ============================================================================

DO $$
DECLARE
    v_agent_id UUID;
    v_note_id UUID;
BEGIN
    SELECT a.id INTO v_agent_id FROM agents a
    JOIN tenants t ON t.id=a.tenant_id
    WHERE t.code='default' AND a.code='hr-assistant';

    -- 1. Types of employment contracts
    v_note_id := _seed_note(
        'Hợp đồng lao động — 2 loại theo BLLĐ 2019',
        E'**Điều 20 BLLĐ 2019 quy định 2 loại HĐLĐ:**\n\n' ||
        E'1. **HĐLĐ không xác định thời hạn** — không thỏa thuận thời hạn.\n' ||
        E'2. **HĐLĐ xác định thời hạn** — không quá **36 tháng** kể từ ngày có hiệu lực.\n\n' ||
        E'**⚠ Quy tắc quan trọng (Đ.20 khoản 2):**\n' ||
        E'- Khi HĐ xác định thời hạn hết hạn mà NLĐ vẫn làm việc → trong 30 ngày phải ký HĐ mới.\n' ||
        E'- Nếu ký HĐ xác định thời hạn lần **thứ 2** → **lần 3 phải ký HĐ không xác định thời hạn** (không được ký thêm HĐ có thời hạn).\n' ||
        E'- Không ký HĐ mới trong 30 ngày → tự động chuyển thành HĐ **không xác định thời hạn**.\n\n' ||
        E'**Nội dung bắt buộc HĐLĐ (Đ.21):**\n' ||
        E'1. Tên, địa chỉ NSDLĐ + họ tên chức danh người ký\n' ||
        E'2. Họ tên, ngày sinh, giới tính, nơi cư trú, số CCCD của NLĐ\n' ||
        E'3. Công việc + địa điểm làm việc\n' ||
        E'4. Thời hạn HĐLĐ\n' ||
        E'5. Mức lương, hình thức trả lương, kỳ hạn trả lương, phụ cấp, khoản bổ sung\n' ||
        E'6. Chế độ nâng bậc, nâng lương\n' ||
        E'7. Thời giờ làm việc, thời giờ nghỉ ngơi\n' ||
        E'8. Trang bị BHLĐ cho NLĐ\n' ||
        E'9. BHXH, BHYT, BHTN\n' ||
        E'10. Đào tạo, bồi dưỡng, nâng cao trình độ',
        ARRAY['blld','hdld','dieu-20','dieu-21']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 2. Probation
    v_note_id := _seed_note(
        'Thời gian thử việc (BLLĐ 2019 Đ.25)',
        E'**Không quá 1 lần cho 1 công việc**, thời gian tối đa:\n\n' ||
        E'| Loại công việc | Tối đa |\n' ||
        E'|----------------|--------|\n' ||
        E'| Quản lý DN | **180 ngày** |\n' ||
        E'| Cần trình độ CĐ trở lên | **60 ngày** |\n' ||
        E'| Cần trình độ trung cấp, kỹ thuật, chuyên môn | **30 ngày** |\n' ||
        E'| Công việc khác | **6 ngày** làm việc |\n\n' ||
        E'**Lương thử việc (Đ.26):** Ít nhất **85%** mức lương của công việc đó.\n\n' ||
        E'**Kết thúc thử việc:**\n' ||
        E'- Trước 3 ngày (với thử việc ≥ 30 ngày) → NSDLĐ phải thông báo kết quả cho NLĐ.\n' ||
        E'- Đạt → ký HĐLĐ chính thức.\n' ||
        E'- Không đạt → chấm dứt (không cần bồi thường).\n\n' ||
        E'**Không được thử việc lần 2 cho cùng công việc.** Nếu vi phạm → HĐLĐ vô hiệu, phải bồi thường theo quy định.',
        ARRAY['blld','thu-viec','dieu-25','dieu-26']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 3. Working hours + overtime
    v_note_id := _seed_note(
        'Thời giờ làm việc + tăng ca (BLLĐ 2019)',
        E'**Thời giờ làm việc bình thường (Đ.105):**\n' ||
        E'- Không quá **8 giờ/ngày** và **48 giờ/tuần**\n' ||
        E'- Nhà nước khuyến khích tuần 40 giờ\n' ||
        E'- Có thể quy định giờ làm theo tuần nhưng ngày không quá 10h\n\n' ||
        E'**Tăng ca (Đ.107):**\n' ||
        E'- Phải có **đồng ý của NLĐ**\n' ||
        E'- Số giờ tăng ca:\n' ||
        E'  - Không quá **50%** giờ làm bình thường/ngày\n' ||
        E'  - Không quá **40 giờ/tháng**\n' ||
        E'  - **Không quá 200 giờ/năm** (mặc định)\n' ||
        E'  - **300 giờ/năm** với một số ngành đặc thù (dệt may, da giày, thủy sản, XD điện tử, chế biến nông sản...)\n\n' ||
        E'**Lương tăng ca (Đ.98):**\n' ||
        E'| Trường hợp | Mức tối thiểu |\n' ||
        E'|------------|----------------|\n' ||
        E'| Ngày thường | **150%** lương giờ bình thường |\n' ||
        E'| Ngày nghỉ hàng tuần | **200%** |\n' ||
        E'| Ngày lễ, tết, ngày nghỉ có hưởng lương | **300%** (chưa kể lương ngày đó) |\n' ||
        E'| Tăng ca ban đêm (22h-06h) | Cộng thêm 20% lương ban ngày |\n' ||
        E'| Tăng ca ban đêm ngày lễ | Ít nhất **390%** |\n\n' ||
        E'**Nghỉ giữa giờ (Đ.109):**\n' ||
        E'- Làm 6+ giờ liên tục → nghỉ ít nhất **30 phút** giữa giờ (tính vào giờ làm việc)\n' ||
        E'- Làm ban đêm → nghỉ ít nhất **45 phút**',
        ARRAY['gio-lam','tang-ca','luong','blld','dieu-105','dieu-107','dieu-98']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 4. Annual leave
    v_note_id := _seed_note(
        'Nghỉ phép năm (BLLĐ 2019 Đ.113 + Đ.114)',
        E'**Số ngày phép năm (Đ.113):**\n' ||
        E'| Điều kiện | Ngày phép/năm |\n' ||
        E'|-----------|----------------|\n' ||
        E'| Làm việc bình thường đủ 12 tháng | **12 ngày** |\n' ||
        E'| Nặng nhọc, độc hại, nguy hiểm | **14 ngày** |\n' ||
        E'| Đặc biệt nặng nhọc, độc hại | **16 ngày** |\n' ||
        E'| Người khuyết tật, chưa thành niên | **14 ngày** |\n\n' ||
        E'**Thâm niên (Đ.114):** Cứ đủ **5 năm** làm cho 1 NSDLĐ → thêm **1 ngày phép**.\n\n' ||
        E'**Chưa đủ 12 tháng:** Tính theo tỷ lệ tương ứng (12 tháng ~ đủ, X tháng ~ X/12 × 12 ngày).\n\n' ||
        E'**Nghỉ phép chưa dùng hết (Đ.113 K3):**\n' ||
        E'- Do thoả thuận, có thể **cộng dồn qua năm** — nhưng **không quá 3 năm**\n' ||
        E'- Khi thôi việc mà chưa nghỉ hết → **NSDLĐ trả tiền** cho những ngày chưa nghỉ\n\n' ||
        E'**Nghỉ lễ tết (Đ.112) — 11 ngày/năm được hưởng nguyên lương:**\n' ||
        E'- Tết Dương lịch: 1 ngày (1/1)\n' ||
        E'- Tết Nguyên đán: **5 ngày** (theo lịch)\n' ||
        E'- Giỗ Tổ Hùng Vương (10/3 âm): 1 ngày\n' ||
        E'- 30/4 + 1/5: 2 ngày\n' ||
        E'- Quốc khánh 2/9 + 1/9 hoặc 3/9: 2 ngày\n\n' ||
        E'**Nghỉ việc riêng có hưởng lương (Đ.115):**\n' ||
        E'- Kết hôn: 3 ngày · Con kết hôn: 1 ngày\n' ||
        E'- Bố/mẹ, bố/mẹ vợ hoặc chồng, vợ/chồng, con chết: 3 ngày',
        ARRAY['nghi-phep','nghi-le','blld','dieu-113','dieu-115']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 5. Maternity leave
    v_note_id := _seed_note(
        'Thai sản + nghỉ chăm con (Luật BHXH 2014 + BLLĐ 2019)',
        E'**Mẹ nghỉ thai sản (Luật BHXH Đ.34):**\n' ||
        E'- **6 tháng** trước và sau khi sinh (có thể nghỉ trước không quá 2 tháng)\n' ||
        E'- Sinh đôi trở lên → thêm 1 tháng cho mỗi con từ con thứ 2\n' ||
        E'- Hưởng **100% mức bình quân tiền lương 6 tháng liền kề** đóng BHXH\n\n' ||
        E'**Bố nghỉ khi vợ sinh (Luật BHXH Đ.34 K7):**\n' ||
        E'- Sinh thường: **5 ngày** làm việc\n' ||
        E'- Sinh mổ, sinh dưới 32 tuần: **7 ngày**\n' ||
        E'- Sinh đôi: **10 ngày**; sinh đôi mổ: **14 ngày**\n' ||
        E'- Sinh ba trở lên: thêm 3 ngày/mỗi con\n' ||
        E'- Điều kiện: đã đóng BHXH đủ 6 tháng trong 12 tháng trước sinh\n\n' ||
        E'**Nghỉ chăm con dưới 7 tuổi ốm (Luật BHXH Đ.24):**\n' ||
        E'- Con dưới 3 tuổi: tối đa **20 ngày/năm**/con\n' ||
        E'- Con 3-7 tuổi: tối đa **15 ngày/năm**/con\n' ||
        E'- Hưởng 75% lương\n\n' ||
        E'**Nghỉ dưỡng sức sau thai sản (Luật BHXH Đ.41):**\n' ||
        E'- Trong 30 ngày đầu đi làm sau thai sản, nếu sức khoẻ chưa phục hồi → nghỉ 5-10 ngày\n' ||
        E'- Hưởng **30% lương cơ sở/ngày**',
        ARRAY['thai-san','nghi-chame','bhxh','blld','dieu-34']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 6. Discipline
    v_note_id := _seed_note(
        'Kỷ luật lao động — 4 hình thức + quy trình (BLLĐ Đ.124-127)',
        E'**4 hình thức kỷ luật (Đ.124):**\n' ||
        E'1. **Khiển trách** — nhẹ nhất, ghi vào hồ sơ\n' ||
        E'2. **Kéo dài thời hạn nâng lương không quá 6 tháng**\n' ||
        E'3. **Cách chức**\n' ||
        E'4. **Sa thải** — nặng nhất\n\n' ||
        E'**Trường hợp áp dụng sa thải (Đ.125):**\n' ||
        E'a) NLĐ trộm cắp, tham ô, đánh bạc, cố ý gây thương tích, dùng ma tuý tại nơi làm việc\n' ||
        E'b) Tiết lộ bí mật KD, bí mật CN, xâm phạm sở hữu trí tuệ, gây thiệt hại nghiêm trọng\n' ||
        E'c) Tái phạm khi đang bị KL kéo dài lương\n' ||
        E'd) Tự ý bỏ việc **5 ngày cộng dồn/30 ngày** hoặc **20 ngày cộng dồn/365 ngày** không có lý do chính đáng\n\n' ||
        E'**Quy trình xử lý kỷ luật (Đ.122 + NĐ 145/2020 hướng dẫn):**\n' ||
        E'1. Phát hiện vi phạm → lập biên bản trong 60 ngày (hoặc 90 ngày với vi phạm phức tạp)\n' ||
        E'2. NSDLĐ **gửi thông báo họp KLLĐ** cho NLĐ + Công đoàn (ít nhất 5 ngày làm việc trước)\n' ||
        E'3. **Họp xử lý KLLĐ:**\n' ||
        E'   - Bắt buộc có mặt NLĐ + đại diện Công đoàn\n' ||
        E'   - NLĐ được nhận thông báo lỗi cụ thể\n' ||
        E'   - Được tự bào chữa hoặc nhờ luật sư\n' ||
        E'4. Lập **biên bản họp** đầy đủ chữ ký các bên\n' ||
        E'5. Ra **quyết định kỷ luật** trong thời hạn thẩm quyền\n' ||
        E'6. Gửi QĐ cho NLĐ + Công đoàn + cơ quan lao động\n\n' ||
        E'**⚠ Nếu bỏ sót bước:** Quyết định vô hiệu, NLĐ có thể khiếu nại → bồi thường + nhận lại việc.',
        ARRAY['ky-luat','sa-thai','blld','dieu-124','dieu-125','quy-trinh']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 7. Minimum wage
    v_note_id := _seed_note(
        'Lương tối thiểu vùng 2026 (NĐ 74/2024/NĐ-CP)',
        E'**Nghị định 74/2024/NĐ-CP** áp dụng từ **1/7/2024**:\n\n' ||
        E'| Vùng | Lương tối thiểu tháng | Lương tối thiểu giờ |\n' ||
        E'|------|-----------------------|---------------------|\n' ||
        E'| I | **4.960.000 VNĐ** | 23.800 VNĐ |\n' ||
        E'| II | **4.410.000 VNĐ** | 21.200 VNĐ |\n' ||
        E'| III | **3.860.000 VNĐ** | 18.600 VNĐ |\n' ||
        E'| IV | **3.450.000 VNĐ** | 16.600 VNĐ |\n\n' ||
        E'**Phân vùng — quận huyện chính:**\n' ||
        E'- **Vùng I:** Hà Nội, TP.HCM (các quận nội thành), Hải Phòng (nội thành), Vũng Tàu, Bình Dương (TP Thủ Dầu Một), Đồng Nai (Biên Hoà)...\n' ||
        E'- **Vùng II:** Ngoại thành TP lớn, các TP tỉnh miền Nam-Đông Nam Bộ, Đà Nẵng ngoại thành...\n' ||
        E'- **Vùng III:** Các huyện thị đồng bằng, TP nhỏ tỉnh miền Bắc-Trung...\n' ||
        E'- **Vùng IV:** Các huyện miền núi, còn lại.\n\n' ||
        E'**Quy tắc áp dụng:**\n' ||
        E'- DN có nhiều địa điểm → **áp dụng vùng của nơi NLĐ làm việc**\n' ||
        E'- Nếu 1 địa bàn thay đổi vùng → áp dụng vùng có lương cao hơn\n' ||
        E'- **Không được trả lương thấp hơn** mức tối thiểu vùng (kể cả có thoả thuận với NLĐ)\n' ||
        E'- Đã cộng phụ cấp cứng → vẫn tính lương tối thiểu là lương cơ bản, không tính phụ cấp',
        ARRAY['luong-toi-thieu','vung','nd74','2026']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 8. BHXH BHYT BHTN
    v_note_id := _seed_note(
        'BHXH/BHYT/BHTN 2026 — đối tượng + tỷ lệ + mức đóng',
        E'**Đối tượng bắt buộc tham gia BHXH:**\n' ||
        E'- NLĐ có HĐLĐ từ **1 tháng trở lên**\n' ||
        E'- Cán bộ công chức viên chức\n' ||
        E'- Người quản lý DN có hưởng lương\n' ||
        E'- Người làm việc ở nước ngoài theo HĐ (một số điều kiện)\n\n' ||
        E'**Tỷ lệ trích (áp dụng 2026):**\n\n' ||
        E'| Loại BH | NLĐ trả | NSDLĐ trả | Tổng |\n' ||
        E'|---------|---------|-----------|------|\n' ||
        E'| BHXH (hưu trí + tử tuất) | **8%** | **17%** | 25% |\n' ||
        E'| BHXH (ốm đau + thai sản) | — | **3%** | 3% |\n' ||
        E'| BHYT | **1.5%** | **3%** | 4.5% |\n' ||
        E'| BHTN | **1%** | **1%** | 2% |\n' ||
        E'| Bảo hiểm TNLĐ-BNN | — | **0.5%** | 0.5% |\n' ||
        E'| **TỔNG (NLĐ)** | **10.5%** | **21.5%** | **32%** |\n\n' ||
        E'**Mức lương làm căn cứ đóng:**\n' ||
        E'- **Sàn:** Lương tối thiểu vùng (2026 vùng I: 4.960.000)\n' ||
        E'- **Trần:** **20 lần lương cơ sở** = 2.340.000 × 20 = **46.800.000 VNĐ/tháng**\n\n' ||
        E'**Điều kiện hưởng BHTN (Luật Việc làm Đ.49):**\n' ||
        E'- Đóng BHTN **đủ 12 tháng trở lên trong 24 tháng** trước khi thất nghiệp\n' ||
        E'- **Đã đăng ký thất nghiệp** tại Trung tâm DVVL\n' ||
        E'- Chưa tìm được việc làm sau 15 ngày kể từ ngày đăng ký\n\n' ||
        E'**Mức trợ cấp thất nghiệp:** 60% mức bình quân tiền lương 6 tháng liền kề đóng BHTN, thời gian hưởng tuỳ số năm đóng (đóng đủ 12-36 tháng: hưởng 3 tháng; thêm 12 tháng: thêm 1 tháng; tối đa 12 tháng).',
        ARRAY['bhxh','bhyt','bhtn','2026','ty-le','muc-dong']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 9. Termination notice
    v_note_id := _seed_note(
        'Đơn phương chấm dứt HĐLĐ — thời hạn báo trước (BLLĐ Đ.35 + Đ.36)',
        E'**NLĐ đơn phương chấm dứt (Đ.35 K1) — báo trước:**\n' ||
        E'| Loại HĐ | Báo trước |\n' ||
        E'|---------|-----------|\n' ||
        E'| HĐLĐ không xác định thời hạn | **45 ngày** |\n' ||
        E'| HĐLĐ xác định thời hạn (12-36 tháng) | **30 ngày** |\n' ||
        E'| HĐLĐ dưới 12 tháng | **3 ngày làm việc** |\n' ||
        E'| Một số ngành đặc thù | Theo quy định riêng, không quá 120 ngày |\n\n' ||
        E'**NLĐ được nghỉ ngay không cần báo trước (Đ.35 K2):**\n' ||
        E'a) Không được bố trí công việc, địa điểm hoặc điều kiện đã thoả thuận\n' ||
        E'b) Không được trả đủ lương hoặc trả không đúng hạn\n' ||
        E'c) Bị NSDLĐ ngược đãi, đánh đập, có lời nói xúc phạm nhân phẩm, cưỡng bức lao động\n' ||
        E'd) Bị quấy rối tình dục nơi làm việc\n' ||
        E'e) LĐ nữ mang thai phải nghỉ theo chỉ định của bác sĩ\n' ||
        E'g) Đủ tuổi nghỉ hưu\n\n' ||
        E'**NSDLĐ đơn phương chấm dứt (Đ.36):**\n' ||
        E'| Loại HĐ | Báo trước |\n' ||
        E'|---------|-----------|\n' ||
        E'| Không xác định thời hạn | **45 ngày** |\n' ||
        E'| Xác định thời hạn (12-36 tháng) | **30 ngày** |\n' ||
        E'| Dưới 12 tháng | **3 ngày làm việc** |\n\n' ||
        E'**Chỉ được chấm dứt khi (Đ.36 K1):**\n' ||
        E'a) NLĐ thường xuyên không hoàn thành công việc (có tiêu chí đánh giá + đã cảnh báo)\n' ||
        E'b) NLĐ ốm đau tai nạn kéo dài (12 tháng liên tục với HĐ KXĐTH, 6 tháng với HĐ XĐTH)\n' ||
        E'c) Do thiên tai, hoả hoạn, dịch bệnh, di dời địa điểm KD → không thoả thuận được\n' ||
        E'd) NLĐ không có mặt trở lại sau ngừng việc quá 15 ngày\n' ||
        E'e) NLĐ đủ tuổi nghỉ hưu\n' ||
        E'g) NLĐ tự ý bỏ việc **5 ngày cộng dồn/30 ngày** không có lý do chính đáng\n\n' ||
        E'**Bồi thường nếu vi phạm quy định báo trước:**\n' ||
        E'- Bồi thường tiền lương những ngày không báo trước\n' ||
        E'- Không được hưởng trợ cấp thôi việc',
        ARRAY['cham-dut','don-phuong','bao-truoc','blld','dieu-35','dieu-36']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 10. Severance
    v_note_id := _seed_note(
        'Trợ cấp thôi việc + mất việc (BLLĐ Đ.46 + Đ.47)',
        E'**Trợ cấp thôi việc (Đ.46) — dành cho:**\n' ||
        E'- NLĐ chấm dứt HĐ theo Đ.34 K1-K7 (thoả thuận, hết hạn HĐ, hoàn thành công việc, bị cầm cố, chết, đủ tuổi hưu, chấm dứt do NLĐ đơn phương hợp pháp)\n' ||
        E'- Điều kiện: đã làm việc **thường xuyên đủ 12 tháng trở lên**\n\n' ||
        E'**Cách tính (Đ.46 K2):**\n' ||
        E'```\n' ||
        E'Trợ cấp = 1/2 tháng lương × (Số năm làm việc)\n' ||
        E'```\n' ||
        E'- Mỗi năm = 1/2 tháng lương\n' ||
        E'- Số năm làm việc = Tổng thời gian làm - Thời gian đã tham gia BHTN - Thời gian NSDLĐ đã trả trợ cấp thôi việc\n' ||
        E'- Lương làm căn cứ = **bình quân tiền lương 6 tháng liền kề** trước khi thôi việc\n\n' ||
        E'**Trợ cấp mất việc làm (Đ.47) — dành cho:**\n' ||
        E'- NLĐ bị cho thôi việc do:\n' ||
        E'  - Thay đổi cơ cấu, công nghệ (Đ.42)\n' ||
        E'  - Vì lý do kinh tế (Đ.42)\n' ||
        E'  - Sáp nhập, chia tách DN (Đ.43)\n' ||
        E'- Điều kiện: đã làm việc **đủ 12 tháng trở lên**\n\n' ||
        E'**Cách tính (Đ.47 K2):**\n' ||
        E'```\n' ||
        E'Trợ cấp = 1 tháng lương × (Số năm làm việc)\n' ||
        E'```\n' ||
        E'- Tối thiểu 2 tháng lương\n' ||
        E'- Mỗi năm = 1 tháng lương (cao gấp đôi trợ cấp thôi việc)\n\n' ||
        E'**Ví dụ:** NLĐ làm 5 năm, bình quân lương 6 tháng = 15tr.\n' ||
        E'- Thôi việc bình thường: 5 × 15tr × 1/2 = **37.5tr**\n' ||
        E'- Mất việc do tái cơ cấu: 5 × 15tr × 1 = **75tr**',
        ARRAY['tro-cap','thoi-viec','mat-viec','blld','dieu-46','dieu-47']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

END $$;

-- ============================================================================
-- BỘ 4 — HỢP ĐỒNG MẪU (attach vào contract-monitor)
-- ============================================================================

DO $$
DECLARE
    v_agent_id UUID;
    v_note_id UUID;
BEGIN
    SELECT a.id INTO v_agent_id FROM agents a
    JOIN tenants t ON t.id=a.tenant_id
    WHERE t.code='default' AND a.code='contract-monitor';

    -- 1. Standard contract structure
    v_note_id := _seed_note(
        'Cấu trúc hợp đồng dịch vụ chuẩn Việt Nam',
        E'Hợp đồng dịch vụ tối thiểu cần các điều khoản sau (theo BLDS 2015 + BLLĐ 2019):\n\n' ||
        E'**Phần mở đầu:**\n' ||
        E'1. Số hợp đồng + ngày ký\n' ||
        E'2. Căn cứ pháp lý (BLDS 2015, Luật Thương mại 2005, các luật chuyên ngành)\n' ||
        E'3. Thông tin bên A + bên B (tên đầy đủ pháp lý, địa chỉ, mã số thuế, người đại diện, chức vụ, số ĐT)\n\n' ||
        E'**Nội dung (Điều 1-N):**\n' ||
        E'- **Đ.1 — Đối tượng hợp đồng:** mô tả dịch vụ/hàng hoá cụ thể\n' ||
        E'- **Đ.2 — Phạm vi công việc:** SOW chi tiết, deliverables\n' ||
        E'- **Đ.3 — Thời gian thực hiện:** ngày bắt đầu, kết thúc, milestones\n' ||
        E'- **Đ.4 — Giá trị hợp đồng + phương thức thanh toán:** tổng giá, VAT, đợt thanh toán\n' ||
        E'- **Đ.5 — Trách nhiệm bên A** (bên cung cấp)\n' ||
        E'- **Đ.6 — Trách nhiệm bên B** (bên nhận)\n' ||
        E'- **Đ.7 — Bảo hành / bảo trì**\n' ||
        E'- **Đ.8 — Chấm dứt HĐ + thanh lý**\n' ||
        E'- **Đ.9 — Phạt vi phạm + bồi thường thiệt hại**\n' ||
        E'- **Đ.10 — Bảo mật thông tin (NDA)**\n' ||
        E'- **Đ.11 — Sở hữu trí tuệ (nếu có)**\n' ||
        E'- **Đ.12 — Sự kiện bất khả kháng**\n' ||
        E'- **Đ.13 — Giải quyết tranh chấp** (hoà giải → toà án nhân dân cấp huyện/tỉnh hoặc VIAC)\n' ||
        E'- **Đ.14 — Điều khoản chung** (số bản, hiệu lực)\n\n' ||
        E'**Phần cuối:**\n' ||
        E'- Chữ ký + đóng dấu 2 bên\n' ||
        E'- Số bản (thường: 4 bản, mỗi bên 2 bản)\n\n' ||
        E'**Phụ lục thường có:**\n' ||
        E'- Phụ lục 1: Chi tiết công việc\n' ||
        E'- Phụ lục 2: Bảng giá\n' ||
        E'- Phụ lục 3: Tiêu chuẩn nghiệm thu',
        ARRAY['hop-dong','cau-truc','dich-vu','template']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 2. Penalty clauses
    v_note_id := _seed_note(
        'Điều khoản phạt vi phạm + bồi thường thiệt hại (Luật Thương mại 2005 Đ.301)',
        E'**Phạt vi phạm hợp đồng (Đ.301 Luật TM 2005):**\n' ||
        E'- Do các bên thoả thuận trong HĐ\n' ||
        E'- **Tối đa 8% giá trị phần nghĩa vụ bị vi phạm**\n' ||
        E'- Nếu quy định vượt 8% → phần vượt bị vô hiệu, chỉ áp dụng đến 8%\n\n' ||
        E'**Bồi thường thiệt hại (Đ.302-303):**\n' ||
        E'- Tính theo thiệt hại thực tế + lợi ích lẽ ra được hưởng\n' ||
        E'- Không giới hạn mức trần\n' ||
        E'- **Có thể áp dụng đồng thời** với phạt vi phạm nếu HĐ có thoả thuận\n\n' ||
        E'**Điều khoản mẫu phổ biến:**\n\n' ||
        E'*Chậm giao hàng/hoàn thành:*\n' ||
        E'```\n' ||
        E'Bên A chậm giao hàng/hoàn thành mỗi ngày phải chịu phạt 0.05% giá trị\n' ||
        E'phần chậm giao, tối đa không quá 8% giá trị hợp đồng.\n' ||
        E'```\n\n' ||
        E'*Chậm thanh toán:*\n' ||
        E'```\n' ||
        E'Bên B chậm thanh toán phải trả lãi chậm trả bằng lãi suất cơ bản NHNN\n' ||
        E'công bố tại thời điểm trả, tính trên số tiền chậm trả từ ngày đáo hạn.\n' ||
        E'```\n\n' ||
        E'*Vi phạm chất lượng:*\n' ||
        E'```\n' ||
        E'Nếu hàng hoá / dịch vụ không đúng quy cách chất lượng, bên A phải sửa chữa\n' ||
        E'thay thế trong 15 ngày. Nếu không khắc phục được → bồi thường 100% giá trị\n' ||
        E'phần vi phạm + chịu phạt 5% giá trị hợp đồng.\n' ||
        E'```',
        ARRAY['phat-vi-pham','boi-thuong','luat-tm','dieu-301']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 3. Dispute resolution
    v_note_id := _seed_note(
        'Điều khoản giải quyết tranh chấp — Toà án vs Trọng tài',
        E'**3 hình thức giải quyết tranh chấp phổ biến:**\n\n' ||
        E'**1. Thương lượng / hoà giải (mặc định):**\n' ||
        E'- Các bên tự thoả thuận trong 30 ngày kể từ khi có tranh chấp\n' ||
        E'- Không tốn phí, giữ quan hệ\n' ||
        E'- Không có giá trị cưỡng chế nếu không lập thành văn bản có công chứng\n\n' ||
        E'**2. Toà án nhân dân:**\n' ||
        E'- Thẩm quyền: TAND cấp huyện (tranh chấp thông thường), cấp tỉnh (có yếu tố nước ngoài, giá trị lớn)\n' ||
        E'- Nơi thụ lý: nơi bị đơn có trụ sở hoặc theo thoả thuận trong HĐ\n' ||
        E'- Ưu: án phí thấp (0.5-3% giá trị tranh chấp)\n' ||
        E'- Nhược: chậm (6 tháng - 2 năm), công khai\n\n' ||
        E'**3. Trọng tài thương mại (Luật TT 54/2010):**\n' ||
        E'- **Điều kiện bắt buộc:** HĐ phải có **thoả thuận trọng tài** trước hoặc sau khi tranh chấp\n' ||
        E'- **VIAC** (Trung tâm Trọng tài Quốc tế VN) là phổ biến nhất\n' ||
        E'- Nếu đã có thoả thuận trọng tài → toà án phải từ chối thụ lý\n' ||
        E'- Ưu: nhanh (3-6 tháng), bảo mật, thẩm phán có chuyên môn\n' ||
        E'- Nhược: phí trọng tài cao hơn (1-3% + phí cố định), phán quyết là chung thẩm không kháng cáo\n\n' ||
        E'**Điều khoản mẫu (Trọng tài VIAC):**\n' ||
        E'```\n' ||
        E'Mọi tranh chấp phát sinh từ hoặc liên quan đến hợp đồng này sẽ được giải\n' ||
        E'quyết bằng trọng tài tại Trung tâm Trọng tài Quốc tế Việt Nam (VIAC)\n' ||
        E'bên cạnh Phòng Thương mại và Công nghiệp Việt Nam theo Quy tắc tố tụng\n' ||
        E'trọng tài của trung tâm này.\n' ||
        E'- Số lượng trọng tài viên: 3 (hoặc 1)\n' ||
        E'- Địa điểm: Hà Nội / TP.HCM\n' ||
        E'- Ngôn ngữ: tiếng Việt\n' ||
        E'- Luật áp dụng: pháp luật Việt Nam\n' ||
        E'```\n\n' ||
        E'**Điều khoản mẫu (Toà án):**\n' ||
        E'```\n' ||
        E'Mọi tranh chấp phát sinh từ hợp đồng này, nếu không giải quyết được bằng\n' ||
        E'thương lượng trong 30 ngày, sẽ được đưa ra Toà án nhân dân [tỉnh/TP nơi\n' ||
        E'bên bị đơn có trụ sở] giải quyết theo quy định của pháp luật Việt Nam.\n' ||
        E'```',
        ARRAY['tranh-chap','trong-tai','viac','toa-an','luat-tt']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

    -- 4. Force majeure
    v_note_id := _seed_note(
        'Điều khoản bất khả kháng (BLDS 2015 Đ.156)',
        E'**Định nghĩa (Đ.156 BLDS 2015):**\n' ||
        E'"Sự kiện bất khả kháng là sự kiện xảy ra một cách khách quan, không thể lường trước và không thể khắc phục được mặc dù đã áp dụng mọi biện pháp cần thiết và khả năng cho phép."\n\n' ||
        E'**3 điều kiện đồng thời phải thoả:**\n' ||
        E'1. **Khách quan** — không do lỗi của các bên\n' ||
        E'2. **Không lường trước** khi ký HĐ\n' ||
        E'3. **Không thể khắc phục** dù đã cố gắng hết sức\n\n' ||
        E'**Ví dụ được công nhận:**\n' ||
        E'- Thiên tai: bão lụt, động đất, sạt lở\n' ||
        E'- Dịch bệnh diện rộng (COVID-19 được TAND công nhận là BKK cho nhiều HĐ)\n' ||
        E'- Chiến tranh, xung đột vũ trang\n' ||
        E'- Đình công tổng quy mô lớn\n' ||
        E'- Thay đổi luật pháp làm mất khả năng thực hiện\n' ||
        E'- Hoả hoạn (nếu không do lỗi bên gây)\n\n' ||
        E'**KHÔNG được coi là BKK:**\n' ||
        E'- Khó khăn tài chính, thay đổi giá thị trường\n' ||
        E'- Đối tác vi phạm HĐ với bên thứ 3\n' ||
        E'- Thiếu vật tư mà thị trường vẫn có\n' ||
        E'- Nhân viên nghỉ việc / nghỉ ốm\n\n' ||
        E'**Điều khoản mẫu:**\n' ||
        E'```\n' ||
        E'1. Trong trường hợp xảy ra sự kiện bất khả kháng khiến một bên không\n' ||
        E'   thực hiện được nghĩa vụ, bên đó phải thông báo bằng văn bản cho\n' ||
        E'   bên kia trong vòng 05 ngày kể từ ngày xảy ra sự kiện, kèm theo\n' ||
        E'   chứng cứ chứng minh.\n' ||
        E'\n' ||
        E'2. Bên bị ảnh hưởng được miễn trách nhiệm bồi thường thiệt hại trong\n' ||
        E'   thời gian sự kiện diễn ra, nhưng phải áp dụng mọi biện pháp hợp lý\n' ||
        E'   để hạn chế thiệt hại.\n' ||
        E'\n' ||
        E'3. Nếu sự kiện kéo dài quá 60 ngày, các bên có quyền thoả thuận điều\n' ||
        E'   chỉnh hoặc chấm dứt hợp đồng mà không phải bồi thường.\n' ||
        E'```',
        ARRAY['bat-kha-khang','bkk','blds','dieu-156','force-majeure']
    );
    INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
    VALUES (v_agent_id, v_note_id, (SELECT id FROM users WHERE email='tuong247@gmail.com'))
    ON CONFLICT DO NOTHING;

END $$;

-- Cleanup helper function
DROP FUNCTION IF EXISTS _seed_note(TEXT, TEXT, TEXT[]);

COMMIT;

-- ============================================================================
-- Verify
-- ============================================================================
\echo ''
\echo '=== Notes per agent after seeding ==='
SELECT a.code AS agent,
       COUNT(ak.note_id) AS notes_attached
FROM agents a
LEFT JOIN agent_knowledge ak ON ak.agent_id = a.id
JOIN tenants t ON t.id = a.tenant_id
WHERE t.code = 'default'
GROUP BY a.code
ORDER BY a.code;
