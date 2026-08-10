-- ============================================================================
-- BSN M&A Advisor Agent Setup
--
-- Đăng ký agent 'bsn-ma-advisor' cho tenant 'bsn' với 21 kỹ năng M&A/IB
-- chuyên sâu (7 nhóm x 3 skills). Cấp ACL cho 4 accounts BSN sẵn có,
-- gắn kèm 4 knowledge notes seed (fee table, VN legal cheat sheet,
-- notable deals 2020-2025, glossary).
--
-- Idempotent — safe to re-run.
-- Prereq: bsn_pilot_setup.sql đã chạy (tenant + accounts đã tồn tại).
-- ============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. Agent: bsn-ma-advisor
-- ---------------------------------------------------------------------------
INSERT INTO agents (
    tenant_id, code, name, type, description,
    lifecycle_state, owner_user_id, sponsor_team, action_tier, config
)
SELECT
    (SELECT id FROM tenants WHERE code='bsn'),
    'bsn-ma-advisor',
    'BSN M&A / Investment Banking Advisor',
    'domain',
    'Trợ lý M&A + IB chuyên sâu của BSN — cover full deal lifecycle từ origination, valuation, structuring, execution, diligence, communication đến closing + integration. 21 kỹ năng theo chuẩn Big-4 IB, thích ứng thị trường Việt Nam (Luật DN 2020, VCA, transfer pricing NĐ 132, tax legacy SME).',
    'production',
    (SELECT id FROM users WHERE email='admin@bsn.vn'),
    'BSN M&A Practice',
    'suggest',
    jsonb_build_object(
        'skills', jsonb_build_array(
            'vietnamese-style',
            'ma-target-screening',
            'ma-teaser-approach',
            'ma-opportunity-pipeline',
            'ma-company-valuation',
            'ma-comps-analysis',
            'ma-precedent-transactions',
            'ma-deal-structuring',
            'ma-capital-structure',
            'ma-term-sheet-design',
            'ma-process-timeline',
            'ma-marketing-and-materials',
            'ma-negotiation-strategy',
            'ma-financial-due-diligence',
            'ma-commercial-due-diligence',
            'ma-risk-assessment',
            'ma-stakeholder-management',
            'ma-investor-pitch',
            'ma-board-pack-prep',
            'ma-project-management',
            'ma-data-room-management',
            'ma-closing-and-integration',
            -- ib-* pack (English, concise prompt-oriented) — complements
            -- the deep VN-context ma-* pack. Only the skills that don't
            -- overlap with ma-* are included here.
            'ib-committee-qa-prep',
            'ib-committee-recommendation',
            'ib-thesis-angle-development',
            'ib-sector-screen-mapping',
            'ib-scenario-toggle-design',
            'ib-sensitivity-tables',
            'ib-three-statement-build',
            'ib-model-audit-review',
            'ib-risk-flag-register',
            'ib-buyer-qa-management'
        ),
        'model', 'claude-haiku-4-5-20251001',
        'max_turns', 12,
        'system_hint',
        'Bạn là BSN M&A Advisor — chuyên gia tư vấn M&A/Investment Banking. ' ||
        'Luôn trả lời bằng tiếng Việt. Ưu tiên khung phân tích structured (bullet, bảng, số cụ thể). ' ||
        'Dẫn chiếu skill khi phù hợp (ví dụ: "theo framework valuation, DCF cho ra..."). ' ||
        'Với deal VN, luôn cân nhắc: Luật DN 2020, VCA approval, transfer pricing NĐ 132, ' ||
        'tax legacy SME, off-book revenue, foreign ownership caps. Không bao giờ tư vấn bằng chữ Hán.'
    )
ON CONFLICT (tenant_id, code) DO UPDATE
SET config = EXCLUDED.config,
    description = EXCLUDED.description,
    updated_at = NOW();

-- ---------------------------------------------------------------------------
-- 2. ACL — owner admin, user access cho user1-3
-- ---------------------------------------------------------------------------
INSERT INTO agent_acl (agent_id, principal_id, principal_type, access_level, granted_by)
SELECT a.id,
       (SELECT id FROM users WHERE email='admin@bsn.vn'),
       'user', 'owner',
       (SELECT id FROM users WHERE email='admin@bsn.vn')
FROM agents a
JOIN tenants t ON t.id=a.tenant_id
WHERE t.code='bsn' AND a.code='bsn-ma-advisor'
ON CONFLICT (agent_id, principal_id, principal_type) DO NOTHING;

INSERT INTO agent_acl (agent_id, principal_id, principal_type, access_level, granted_by)
SELECT a.id, u.id, 'user', 'user',
       (SELECT id FROM users WHERE email='admin@bsn.vn')
FROM agents a
JOIN tenants t ON t.id=a.tenant_id
CROSS JOIN users u
WHERE t.code='bsn' AND a.code='bsn-ma-advisor'
  AND u.email IN ('user1@bsn.vn', 'user2@bsn.vn', 'user3@bsn.vn')
ON CONFLICT (agent_id, principal_id, principal_type) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 3. 4 seed knowledge notes cho BSN M&A practice
-- ---------------------------------------------------------------------------
INSERT INTO notes (user_id, tenant_id, title, content, tags)
SELECT (SELECT id FROM users WHERE email='admin@bsn.vn'),
       (SELECT id FROM tenants WHERE code='bsn'),
       t.title, t.content, t.tags
FROM (VALUES
    (
        'BSN M&A — Bảng giá dịch vụ & phạm vi engagement',
        E'# BSN M&A / IB — Bảng giá + Scope\n\n' ||
        E'## Sell-side Advisory (đại diện bên bán)\n\n' ||
        E'| Deal size | Retainer | Success fee | Min term |\n' ||
        E'|---|---|---|---|\n' ||
        E'| <$5M | 200-300M VND | 3-5% | 4 tháng |\n' ||
        E'| $5-20M | 300-500M VND | 2-4% | 6 tháng |\n' ||
        E'| $20-100M | 500M-1B VND | 1.5-3% | 8-12 tháng |\n' ||
        E'| >$100M | 1-2B VND | 1-2% | 12 tháng |\n\n' ||
        E'## Buy-side Advisory\n\n' ||
        E'Tương tự sell-side, minus success fee thấp hơn 0.5%.\n\n' ||
        E'## Standalone services\n\n' ||
        E'- **Valuation (DCF + comps + precedent):** 80-150 triệu VND\n' ||
        E'- **Vendor Due Diligence (FDD):** 150-250 triệu VND\n' ||
        E'- **Commercial Due Diligence:** 300-500 triệu VND\n' ||
        E'- **Information Memorandum + Financial Model:** 150-250 triệu VND\n' ||
        E'- **Pitch deck (Series A/B):** 80-150 triệu VND\n' ||
        E'- **Term Sheet drafting:** 50-100 triệu VND\n' ||
        E'- **Data Room setup + managed (monthly):** 30-50 triệu/tháng\n' ||
        E'- **Post-merger integration (100-day plan):** 200-400 triệu VND\n\n' ||
        E'## Discount / package\n\n' ||
        E'- Package end-to-end sell-side + PMI: -10-15% vs à-la-carte\n' ||
        E'- Repeat client (deal #2+): -10% retainer\n' ||
        E'- Cross-border deal: +20-30% premium (thêm compliance workload)\n\n' ||
        E'## Exclusions (không bao gồm)\n\n' ||
        E'- Legal counsel (khách phải hire law firm riêng)\n' ||
        E'- Big-4 auditor (khách hire riêng nếu deal >$10M)\n' ||
        E'- Regulator lobbying (illegal — BSN không làm)\n' ||
        E'- VDR provider license (charged pass-through)',
        ARRAY['ma','pricing','engagement','bsn-fee']::text[]
    ),
    (
        'BSN M&A — Vietnam Legal Cheat Sheet (2026)',
        E'# VN Legal Framework cho M&A — Quick Reference\n\n' ||
        E'## Luật chủ chốt\n\n' ||
        E'- **Luật Doanh nghiệp 2020** (Law on Enterprises)\n' ||
        E'  - Điều 138.2.d: shareholder approval cho tx ≥35% asset\n' ||
        E'  - Điều 157: board meeting notice 3 ngày\n' ||
        E'  - Điều 167: related party transaction procedure\n\n' ||
        E'- **Luật Đầu tư 2020** (Law on Investment)\n' ||
        E'  - Điều 26: FDI registration khi acquire VN company\n' ||
        E'  - Ngành nghề kinh doanh có điều kiện: 267 ngành\n\n' ||
        E'- **Luật Chứng khoán 2019**\n' ||
        E'  - Tender offer khi acquire >25% listed company\n' ||
        E'  - Disclosure 24h khi transaction ≥5% listed\n\n' ||
        E'- **Luật Cạnh tranh 2018** (Competition Law)\n' ||
        E'  - VCA notification khi market share ≥20% OR revenue ≥1000 tỷ\n' ||
        E'  - Timeline: 30-90 days\n\n' ||
        E'## Nghị định chủ chốt\n\n' ||
        E'- **NĐ 155/2020/NĐ-CP:** Guidance Luật Chứng khoán\n' ||
        E'- **NĐ 31/2021/NĐ-CP:** Guidance Luật Đầu tư — FDI + conditional business\n' ||
        E'- **NĐ 132/2020/NĐ-CP:** Transfer pricing — related party transactions\n' ||
        E'- **NĐ 13/2023/NĐ-CP:** Personal data protection (data privacy)\n\n' ||
        E'## Foreign ownership caps (WTO commitment schedule)\n\n' ||
        E'| Sector | Cap |\n' ||
        E'|---|---|\n' ||
        E'| Banking | 30% (aggregate foreign) |\n' ||
        E'| Airline | 34% |\n' ||
        E'| Telecom (facility-based) | 65% |\n' ||
        E'| Advertising | 100% (via JV) |\n' ||
        E'| Distribution / retail | 100% (with sublicenses) |\n' ||
        E'| Real estate (residential) | Restricted |\n' ||
        E'| Media / publishing | Restricted |\n\n' ||
        E'## Regulator ownership approval matrix\n\n' ||
        E'| Regulator | Sector | Trigger |\n' ||
        E'|---|---|---|\n' ||
        E'| SBV | Banking, fintech, e-wallet | Any share transfer |\n' ||
        E'| MOIT | Energy, distribution | License holder change |\n' ||
        E'| MIC | Telecom, media | License holder change |\n' ||
        E'| MOH | Pharma, medical devices | License holder change |\n' ||
        E'| MARD | Agri, food processing | Some sub-sectors |\n' ||
        E'| VCA | All sectors | Concentration threshold |\n' ||
        E'| Sở KHĐT | All | Business registration amend |\n\n' ||
        E'## Tax statute of limitation\n\n' ||
        E'- CIT, VAT, PIT: 10 năm (Luật Quản lý thuế 2019)\n' ||
        E'- Do đó: tax indemnity trong SPA nên survival ≥10 năm cho buyer',
        ARRAY['ma','legal','vn-law','regulatory']::text[]
    ),
    (
        'BSN M&A — Notable VN Deals 2020-2025',
        E'# VN M&A Precedent Deals — Benchmark cho pricing\n\n' ||
        E'## Consumer / Retail\n\n' ||
        E'- **SK Group → Vincommerce (2020):** $410M for 16.3%. Implied EV ~$2.5B. Multiple ~1.5x sales.\n' ||
        E'- **Central Group → NKID (Nguyễn Kim, 2015 — reference):** ~$200M, first major foreign retail deal.\n' ||
        E'- **Alibaba → Lazada VN (parts of):** portfolio deals, not disclosed.\n\n' ||
        E'## F&B\n\n' ||
        E'- **Bain Capital → Masan MEATLife (2021):** $105M for 6%. EV ~$1.75B. 8-10x EBITDA.\n' ||
        E'- **KKR → Vinamec Healthcare (2020):** partial exit, deal ~$650M portion.\n' ||
        E'- **Jollibee → Highlands Coffee (2011 reference, expanded 2020):** ~$60M for majority, expanded.\n\n' ||
        E'## Banking / Financial Services\n\n' ||
        E'- **KEB Hana Bank → BIDV (2019 — reference, closed 2020):** $875M for 15%. Implied ~1.4x P/B.\n' ||
        E'- **SMBC → FE Credit (VPB subsidiary, 2021):** $1.4B for 49%. Implied EV ~$2.8B.\n' ||
        E'- **Sumitomo Mitsui → VPBank (2023):** $1.5B for 15%. Implied 1.5x P/B.\n\n' ||
        E'## Real Estate / Construction\n\n' ||
        E'- **Warburg Pincus → BW Industrial (2021):** $200M growth investment.\n' ||
        E'- **Keppel Land → Vietnam projects (multiple 2020-2024):** various sizes $50-200M.\n\n' ||
        E'## Technology / Fintech\n\n' ||
        E'- **Momo (2021):** Series E led by Mizuho, $200M, valuation ~$2B.\n' ||
        E'- **VNLife (VNPay parent, 2021):** $250M Series B, valuation ~$1B+.\n' ||
        E'- **Tiki (2020):** $130M Series E, valuation ~$800M.\n' ||
        E'- **Sky Mavis (2021):** $152M Series B, valuation $3B.\n\n' ||
        E'## Manufacturing / Industrial\n\n' ||
        E'- **Thaco → HAG Agri (Hoàng Anh Gia Lai, 2020-2022 series):** total ~$500M for agri assets.\n' ||
        E'- **Vietnam JG Summit → JSC (2022):** electricity distribution.\n\n' ||
        E'## Multiples benchmark (median VN 2020-2025)\n\n' ||
        E'| Industry | EV/Revenue | EV/EBITDA | P/E |\n' ||
        E'|---|---|---|---|\n' ||
        E'| F&B | 1.5-3x | 8-12x | 15-22x |\n' ||
        E'| Retail | 0.5-1.5x | 6-10x | 12-18x |\n' ||
        E'| Fintech | 5-15x | N/A (loss) | N/A |\n' ||
        E'| Banking | N/A | N/A | P/B 1.2-1.8x |\n' ||
        E'| Manufacturing | 0.8-1.5x | 5-8x | 10-15x |\n' ||
        E'| Real estate | 0.8-2x | 6-10x | 8-12x |\n' ||
        E'| Healthcare | 2-5x | 10-15x | 18-25x |\n' ||
        E'| SaaS/Tech | 5-15x | N/A | N/A |\n\n' ||
        E'## Control premium\n\n' ||
        E'- **VN median 2020-2025: 35-45%** so với minority stake.\n' ||
        E'- Strategic buyer premium: 45-60%.\n' ||
        E'- Financial (PE) buyer premium: 25-35%.\n\n' ||
        E'## Deal cost benchmark\n\n' ||
        E'- IB fee (all-in): 1.5-4% cho deal $10-100M, 1-2% cho >$100M\n' ||
        E'- Legal: 0.3-0.8%\n' ||
        E'- Big-4 auditor (buy-side FDD): 0.2-0.5%\n' ||
        E'- Total deal cost: 2.5-6% of deal size',
        ARRAY['ma','precedent','vn-deals','multiples']::text[]
    ),
    (
        'BSN M&A — Glossary tiếng Việt + tiếng Anh',
        E'# M&A / IB Glossary — VN + EN\n\n' ||
        E'## Deal types\n\n' ||
        E'- **M&A** (Mergers & Acquisitions) — Sáp nhập & Mua lại\n' ||
        E'- **Merger** — Sáp nhập\n' ||
        E'- **Acquisition** — Mua lại\n' ||
        E'- **Share deal** — Mua cổ phần\n' ||
        E'- **Asset deal** — Mua tài sản\n' ||
        E'- **LBO** (Leveraged Buyout) — Mua lại có đòn bẩy\n' ||
        E'- **MBO** (Management Buyout) — Ban quản trị mua lại\n' ||
        E'- **JV** (Joint Venture) — Liên doanh\n' ||
        E'- **Strategic** — Nhà đầu tư chiến lược\n' ||
        E'- **Financial (PE/VC)** — Nhà đầu tư tài chính\n\n' ||
        E'## Deal stages\n\n' ||
        E'- **Origination** — Tìm kiếm deal\n' ||
        E'- **Teaser** — Tài liệu quảng cáo ẩn danh\n' ||
        E'- **NDA** (Non-Disclosure Agreement) — Thoả thuận bảo mật\n' ||
        E'- **IM / CIM** (Information Memorandum / Confidential Info Memo) — Bản chào bán\n' ||
        E'- **IOI** (Indication of Interest) — Bày tỏ quan tâm\n' ||
        E'- **LOI** (Letter of Intent) — Thư đề nghị\n' ||
        E'- **Term Sheet** — Bản điều khoản chính\n' ||
        E'- **MOU** (Memorandum of Understanding) — Biên bản ghi nhớ\n' ||
        E'- **DD** (Due Diligence) — Thẩm định\n' ||
        E'- **SPA** (Share Purchase Agreement) — Hợp đồng mua bán cổ phần\n' ||
        E'- **APA** (Asset Purchase Agreement) — Hợp đồng mua tài sản\n' ||
        E'- **Signing** — Ký kết\n' ||
        E'- **Closing** — Hoàn tất giao dịch\n' ||
        E'- **PMI** (Post-Merger Integration) — Hợp nhất sau sáp nhập\n\n' ||
        E'## Financial terms\n\n' ||
        E'- **EV** (Enterprise Value) — Giá trị doanh nghiệp\n' ||
        E'- **Equity Value** — Giá trị vốn chủ sở hữu\n' ||
        E'- **EBITDA** — Lợi nhuận trước lãi, thuế, khấu hao\n' ||
        E'- **DCF** (Discounted Cash Flow) — Chiết khấu dòng tiền\n' ||
        E'- **WACC** — Chi phí vốn bình quân trọng số\n' ||
        E'- **CAPM** — Mô hình định giá tài sản vốn\n' ||
        E'- **Comps** (Comparable Companies) — Công ty so sánh\n' ||
        E'- **Precedent transactions** — Giao dịch tiền lệ\n' ||
        E'- **Working capital** — Vốn lưu động\n' ||
        E'- **QoE** (Quality of Earnings) — Chất lượng lợi nhuận\n' ||
        E'- **Earn-out** — Tiền thưởng theo kết quả\n' ||
        E'- **Escrow** — Ký quỹ\n' ||
        E'- **Holdback** — Giữ lại một phần\n' ||
        E'- **True-up** — Điều chỉnh chốt sổ\n' ||
        E'- **Rollover equity** — Cổ phần chuyển tiếp\n\n' ||
        E'## Deal terms\n\n' ||
        E'- **CP** (Conditions Precedent) — Điều kiện tiên quyết\n' ||
        E'- **CoC** (Change of Control) — Thay đổi quyền kiểm soát\n' ||
        E'- **MAC** (Material Adverse Change) — Thay đổi bất lợi trọng yếu\n' ||
        E'- **R&W** (Representations & Warranties) — Cam đoan & bảo đảm\n' ||
        E'- **Indemnification** — Bồi thường\n' ||
        E'- **Basket / Deductible** — Ngưỡng khiếu nại\n' ||
        E'- **Cap** — Trần bồi thường\n' ||
        E'- **Survival** — Thời hạn hiệu lực\n' ||
        E'- **Exclusivity** — Độc quyền đàm phán\n' ||
        E'- **Break fee** — Phí huỷ giao dịch\n' ||
        E'- **Non-compete** — Không cạnh tranh\n' ||
        E'- **Non-solicit** — Không lôi kéo\n\n' ||
        E'## Regulator VN\n\n' ||
        E'- **SBV** (State Bank of Vietnam) — NHNN\n' ||
        E'- **MOIT** (Ministry of Industry & Trade) — Bộ Công Thương\n' ||
        E'- **MIC** (Ministry of Info & Comms) — Bộ Thông tin Truyền thông\n' ||
        E'- **MOH** (Ministry of Health) — Bộ Y tế\n' ||
        E'- **MARD** (Ministry of Agri & Rural Dev) — Bộ NN & PTNT\n' ||
        E'- **MOF** (Ministry of Finance) — Bộ Tài chính\n' ||
        E'- **MPI** (Ministry of Planning & Invest) — Bộ KH & ĐT\n' ||
        E'- **VCA** (Vietnam Competition Authority) — Uỷ ban Cạnh tranh QG\n' ||
        E'- **SSC** (State Securities Commission) — UBCK NN\n' ||
        E'- **Sở KHĐT** — Sở Kế hoạch & Đầu tư (tỉnh/thành)\n\n' ||
        E'## VN legal instrument\n\n' ||
        E'- **Điều lệ** — Charter\n' ||
        E'- **Giấy CNĐK Doanh nghiệp (GCNĐKDN)** — Business registration certificate\n' ||
        E'- **Giấy CN Đầu tư (GCNĐT / IRC)** — Investment registration certificate\n' ||
        E'- **Sổ đỏ / GCN QSDĐ** — Land use rights certificate\n' ||
        E'- **Biên bản HĐQT** — Board resolution\n' ||
        E'- **Nghị quyết ĐHĐCĐ** — Shareholder resolution\n' ||
        E'- **Sổ đăng ký cổ đông** — Shareholder register',
        ARRAY['ma','glossary','terminology','vn-en']::text[]
    )
) AS t(title, content, tags)
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- 4. Attach 4 M&A knowledge notes vào bsn-ma-advisor
-- ---------------------------------------------------------------------------
INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
SELECT a.id, n.id, (SELECT id FROM users WHERE email='admin@bsn.vn')
FROM agents a
JOIN tenants t ON t.id=a.tenant_id
CROSS JOIN notes n
WHERE t.code='bsn' AND a.code='bsn-ma-advisor'
  AND n.tenant_id = t.id
  AND n.title IN (
      'BSN M&A — Bảng giá dịch vụ & phạm vi engagement',
      'BSN M&A — Vietnam Legal Cheat Sheet (2026)',
      'BSN M&A — Notable VN Deals 2020-2025',
      'BSN M&A — Glossary tiếng Việt + tiếng Anh'
  )
ON CONFLICT DO NOTHING;

COMMIT;

-- ---------------------------------------------------------------------------
-- Verify
-- ---------------------------------------------------------------------------
\echo ''
\echo '=== BSN M&A Advisor Agent ==='
SELECT a.code, a.lifecycle_state, a.action_tier,
       (SELECT email FROM users WHERE id=a.owner_user_id) AS owner,
       jsonb_array_length(a.config->'skills') AS num_skills,
       a.config->>'model' AS model,
       (SELECT COUNT(*) FROM agent_knowledge WHERE agent_id=a.id) AS notes,
       (SELECT COUNT(*) FROM agent_acl WHERE agent_id=a.id) AS acl_count
FROM agents a
JOIN tenants t ON t.id=a.tenant_id
WHERE t.code='bsn' AND a.code='bsn-ma-advisor';

\echo ''
\echo '=== Skills configured ==='
SELECT jsonb_array_elements_text(config->'skills') AS skill
FROM agents a JOIN tenants t ON t.id=a.tenant_id
WHERE t.code='bsn' AND a.code='bsn-ma-advisor';

\echo ''
\echo '=== ACL principals ==='
SELECT (SELECT email FROM users WHERE id=aa.principal_id) AS user_email,
       aa.access_level
FROM agent_acl aa
JOIN agents a ON a.id=aa.agent_id
JOIN tenants t ON t.id=a.tenant_id
WHERE t.code='bsn' AND a.code='bsn-ma-advisor'
ORDER BY aa.access_level DESC, user_email;

\echo ''
\echo '=== Knowledge notes attached ==='
SELECT n.title
FROM agent_knowledge ak
JOIN agents a ON a.id=ak.agent_id
JOIN tenants t ON t.id=a.tenant_id
JOIN notes n ON n.id=ak.note_id
WHERE t.code='bsn' AND a.code='bsn-ma-advisor'
ORDER BY n.title;
