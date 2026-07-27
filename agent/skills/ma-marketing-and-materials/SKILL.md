---
name: ma-marketing-and-materials
description: |
  Chuẩn bị bộ tài liệu marketing deal M&A — Teaser, IM (Information
  Memorandum), Management Presentation, Financial Model summary.
  Áp dụng khi khách hỏi "làm IM", "chuẩn bị deck bán công ty",
  "management presentation", "tài liệu marketing deal".
keywords:
  - information memorandum
  - IM
  - teaser
  - management presentation
  - deck bán
  - marketing materials
  - pitchbook
  - CIM
always_on: false
---

# Marketing & Materials — Bộ vũ khí sell-side

Bạn là **IB Materials Lead của BSN**, chuẩn bị bộ tài liệu marketing chuẩn Big-4 IB.

## Hierarchy tài liệu marketing sell-side

| # | Tài liệu | Trang | Đối tượng | Confidentiality |
|---|---|---|---|---|
| 1 | **Teaser (Blind Profile)** | 1-2 | Anonymized, first contact | Public → NDA prompt |
| 2 | **NDA (Non-Disclosure Agreement)** | 2-4 | Legal binding | N/A |
| 3 | **Information Memorandum (IM/CIM)** | 60-100 | Post-NDA, IOI phase | High — track distribution |
| 4 | **Management Presentation** | 30-50 slides | Live meeting, shortlisted bidders | Very high |
| 5 | **Financial Model** | Excel file | Bidders finalizing bid | With watermark |
| 6 | **Data Room** | 300-800 docs | Confirmatory DD | Access-logged |
| 7 | **Q&A tracker** | Google sheet | Ongoing | Version-controlled |

## Teaser — 1-2 trang anonymized

### Cấu trúc chuẩn (1 trang A4)

```
─────────────────────────────────────────
[LOGO BSN]           CONFIDENTIAL TEASER
                     Project [Codename]
─────────────────────────────────────────

Business Overview                    (100 words)
- Ngành, mô hình, khách hàng target
- Không tiết lộ tên công ty
- Ví dụ: "A leading Vietnamese consumer-focused
  F&B chain with 200+ stores across HCMC and Hanoi"

Key Highlights                       (Bullet points)
• Revenue: $XXm (CY24), growth CAGR X%
• EBITDA margin: XX%
• X years profitable operation
• Strong management team

Investment Rationale                 (3-5 bullets)
• Market leader in [segment]
• Recurring revenue [%]
• Expansion optionality

Transaction Details
• Type: Majority/Minority stake
• Size: $XX-XXm indicative
• Timeline: Q1 2026 close target

Next Steps
Interested parties: Sign NDA to access IM
Contact: [BSN partner name, email]
─────────────────────────────────────────
```

### Quy tắc anonymize
- Không nói tên công ty, tên brand
- Không dùng ảnh nhận diện được (logo, store front)
- Vẫn được nói ngành + geo generic
- Nếu công ty độc quyền (chỉ 1-2 công ty match) → cân nhắc mất anonymity là chấp nhận được

## Information Memorandum (IM) — 60-100 trang

### Table of Contents chuẩn

```
1. Executive Summary                   [5 pp]
2. Investment Highlights               [8 pp]
3. Company Overview                    [10 pp]
   3.1 History & milestones
   3.2 Business model
   3.3 Products/services
   3.4 Locations/footprint
4. Market Overview                     [12 pp]
   4.1 Vietnam market size + growth
   4.2 Competitive landscape
   4.3 Market position + share
   4.4 Regulatory environment
5. Growth Strategy                     [10 pp]
   5.1 Organic growth initiatives
   5.2 Expansion plans (geo, product)
   5.3 M&A/consolidation opportunities
6. Operations                          [8 pp]
   6.1 Supply chain
   6.2 Technology & systems
   6.3 Key operational KPIs
7. Management Team                     [5 pp]
   7.1 Org chart
   7.2 Bios of top 10 execs
   7.3 Retention/incentive structure
8. Financial Overview                  [15 pp]
   8.1 Historical P&L (3-5 years)
   8.2 Balance sheet highlights
   8.3 Cash flow analysis
   8.4 KPI dashboard
   8.5 Forecast (3-5 years)
   8.6 Assumptions
9. Transaction Overview                [5 pp]
   9.1 Rationale for sale
   9.2 Process timeline
   9.3 What's being offered
10. Appendix                          [10-20 pp]
    A. Detailed financials
    B. Corporate structure
    C. Legal + IP + licenses
    D. Customer references (anonymized)
```

### Chất lượng nội dung mỗi section

**Executive Summary** phải standalone — bidder chỉ đọc 5 trang này cũng ra IOI được.
- Investment thesis (2-3 sentences why buy)
- Financial snapshot (revenue, EBITDA, growth)
- Ask (deal type + indicative range)

**Market Overview** dùng:
- Third-party research (Nielsen, Euromonitor, VCCI, Statista)
- Cite sources ở footnote
- Chart: market size 5-year historical + 5-year forecast

**Financials:**
- Vietnamese GAAP + IFRS reconciliation nếu có
- Show adjusted EBITDA (add-back one-off items rõ ràng)
- Include working capital normalization
- Forecast realistic — bidder sẽ challenge

## Management Presentation — 30-50 slides

### Cấu trúc

- **Opening (5 slides):** Company intro + agenda
- **Business overview (10-15 slides):** Deep dive từng product line
- **Financials (5-10 slides):** Live walkthrough model
- **Growth strategy (5-10 slides):** Concrete plans
- **Q&A prep (backup slides):** 20-30 slides sẵn cho câu hỏi khó

### Delivery tips
- Duration: 2-3 hours per bidder
- CEO + CFO + COO cùng present
- Rehearsal đầy đủ 2-3 lần trước live meeting
- Chuẩn bị "war room" — team M&A sẵn sàng feed data khi bidder hỏi
- Không nói too much — để bidder hỏi

## Financial Model — Excel workbook

### Sheets chuẩn

```
1. Cover (dashboard + assumptions summary)
2. Assumptions (all drivers with sources)
3. P&L (monthly for 5Y projection, quarterly/annual summary)
4. Balance Sheet
5. Cash Flow (indirect + direct)
6. Working Capital
7. CapEx schedule
8. Debt schedule
9. Tax
10. Segment/Product breakdown
11. Valuation (DCF + comps + precedent)
12. Sensitivity
13. Scenarios (base/upside/downside)
```

### Standards
- No hard-coded values in output cells — everything from assumptions
- Color code: blue=input, black=formula, green=link
- Version control: `Model_v3.2_2026-01-15_BSN.xlsx`
- Locked cells for output (bidder không sửa được)
- Watermark: bidder name embedded

## Data Room organization

### Folder structure

```
01. Corporate
   /01.1 Charter documents
   /01.2 Board minutes
   /01.3 Shareholder register
02. Financial
   /02.1 Audited financials (5Y)
   /02.2 Management accounts monthly
   /02.3 Tax filings + assessments
03. Commercial
   /03.1 Top customer contracts
   /03.2 Top supplier contracts
   /03.3 Pricing history
04. Legal
   /04.1 Litigation status
   /04.2 Licenses + permits
   /04.3 IP registry
05. HR
   /05.1 Employment contracts
   /05.2 Compensation structure
   /05.3 Union agreements
06. Operations
   /06.1 Real estate leases
   /06.2 Insurance
   /06.3 IT infrastructure
07. Regulatory
   /07.1 Compliance certificates
   /07.2 Environmental permits
```

### Rules
- Watermark every PDF (bidder name)
- Access log (who viewed what, when)
- Q&A per document allowed
- Naming convention: `[Section]_[Description]_[YYYY-MM-DD].pdf`

## Đặc thù VN

### Bilingual requirement
- IM thường **English primary** cho bidder nước ngoài, có Vietnamese executive summary
- Legal docs (contracts, permits): Vietnamese original + English translation
- Số liệu tài chính: dùng VND (billion) + USD conversion tại footer

### VN legal disclosures phải include
- Chứng nhận đầu tư (nếu FDI)
- Giấy phép con (theo ngành: kinh doanh có điều kiện)
- Kết luận thanh tra thuế 3 năm gần nhất
- Danh sách quan hệ liên kết (related party) per NĐ 132/2020

### Off-book adjustment
- IM phải show đầy đủ off-book revenue + expenses trong "adjusted P&L"
- Đây là **the most sensitive item** — buyer sẽ challenge kỹ nhất
- Cần vendor DD report chứng thực

## BSN deliverables checklist per phase

Phase Prep (Week 1-8):
- [ ] Teaser (1-2 pp)
- [ ] IM (60-100 pp)
- [ ] Financial model (comprehensive)
- [ ] Data room (300+ docs indexed)
- [ ] Buyer list (30-80 targets)

Phase Marketing (Week 9-16):
- [ ] Approach materials (emails, call scripts)
- [ ] NDA distribution + tracking
- [ ] IM distribution log
- [ ] Q&A first-round responses

Phase DD (Week 17-26):
- [ ] Management presentation (30-50 slides)
- [ ] Site visit itineraries
- [ ] Q&A tracker (running list)
- [ ] Confirmatory DD support docs

## Chi phí BSN

- Full IM package (Teaser + IM + Model + Data room setup): **150-250 triệu VND** (2-3 tháng)
- Management presentation prep + rehearsal: **50-80 triệu VND**
- Ongoing marketing support: bundled trong retainer

## Common mistakes cần tránh

1. **IM quá dài** (>120 pp) → bidder không đọc; giữ 60-100 pp
2. **Forecast không defensible** — mất credibility từ đầu; hedge realistic
3. **Data room chaos** — bidder frustrated, DD kéo dài
4. **Không rehearsal management presentation** — CEO fumble live
5. **Anonymize sai** — leak trong lúc chưa NDA
6. **Model errors** — bidder tìm ra ngay → mất trust
7. **English-only cho VN bidder** → miss Vietnamese strategic bidders (Masan, Vingroup, Thaco)

Cross-ref: `ma-process-timeline` (khi nào cần từng deliverable), `ma-company-valuation` (financial model backbone), `ma-teaser-approach` (channels để distribute).
