---
name: ma-risk-assessment
description: |
  Risk assessment framework cho M&A deal — legal, tax, regulatory,
  operational, HR, IP, ESG. Áp dụng khi khách hỏi "risk register",
  "rủi ro deal", "issue log", "red flag review", "deal killers".
keywords:
  - risk assessment
  - risk register
  - deal killer
  - red flag
  - regulatory risk
  - tax risk
  - legal risk
  - ESG risk
always_on: false
---

# Risk Assessment — Framework toàn diện

Bạn là **Risk Partner BSN**, chủ trì cross-workstream risk review — synthesize từ FDD, CDD, legal DD, tech DD, HR DD thành **Risk Register duy nhất**.

## Risk taxonomy — 8 categories

| # | Category | Owner DD | VN Frequency |
|---|---|---|---|
| 1 | Financial | FDD lead | Very high |
| 2 | Tax | Tax advisor + FDD | Very high (esp SME) |
| 3 | Legal / Contractual | Legal DD | High |
| 4 | Regulatory | Legal + industry expert | Very high (regulated sectors) |
| 5 | Commercial / Customer | CDD | Medium-high |
| 6 | Operational | Operational DD | Medium |
| 7 | HR / Culture | HR DD | Medium |
| 8 | IP / Technology | IP + Tech DD | Medium (higher for tech) |
| 9 | ESG / Environmental | ESG advisor | Growing importance |

## Risk scoring matrix

```
Likelihood × Impact = Risk Score

Likelihood (1-5):
1 = Very unlikely (<5% chance)
2 = Unlikely (5-20%)
3 = Possible (20-50%)
4 = Likely (50-80%)
5 = Almost certain (>80%)

Impact (1-5) — measured in % Equity Value or absolute:
1 = Negligible (<0.5% EV or <$100k)
2 = Minor (0.5-2% EV or $100k-$1m)
3 = Moderate (2-5% EV or $1-5m)
4 = Major (5-15% EV or $5-30m)
5 = Critical (>15% EV or >$30m) — deal killer

Score interpretation:
1-4  = LOW — accept
5-9  = MEDIUM — mitigate (contract mechanism)
10-15 = HIGH — negotiate + escrow + escrow + covenant
16-25 = CRITICAL — walk away or restructure fundamentally
```

## Category 1: Financial risks (from FDD)

Common findings:
- Revenue not recurring / lumpy
- Customer concentration >30%
- EBITDA quality (off-book, related party)
- WC volatility
- Cash conversion cycle issues
- Debt covenants nearly breached

Mitigation:
- Escrow 5-20% for 12-24 months
- Indemnification with $X cap
- Earn-out for growth achievement
- WC true-up mechanism

## Category 2: Tax risks (VN specific — HIGH IMPORTANCE)

### Common VN tax issues in SME target

1. **VAT under-declaration** — thường 10-30% of true revenue not on invoice
2. **CIT (Corporate Income Tax) errors**
   - Deductibility incorrect
   - Related-party pricing (NĐ 132/2020)
3. **Personal Income Tax (PIT)**
   - Employee compensation via cash off-payroll
   - Contractor treated wrong
4. **Foreign Contractor Tax (FCT)**
   - Missed on cross-border payments
5. **Transfer pricing**
   - No BEPS documentation
   - Non-arm's-length pricing
6. **Import duty / VAT on import**
   - Mis-classification HS code
7. **Land use rights + property tax**
8. **Historical tax inspection findings**
   - Unresolved thanh tra thuế

### Quantify tax exposure

```
Item                          Likelihood  Impact($M)  Score
─────────────────────────────────────────────────────────
VAT under-declaration 5Y      4          2.5          10
CIT deductibility challenge   3          0.8          2.4
Transfer pricing adjustment   4          1.5          6
FCT on management fee         3          0.3          0.9
─────────────────────────────────────────────────────────
Total exposure quantified:    ~$5.1M

Recommended mitigation:
- Special tax indemnity (uncapped) surviving 5 years
  (VN statute of limitation cho tax = 10 năm)
- Escrow $2M cho tax specifically
- Voluntary tax disclosure pre-closing để reduce exposure
```

### Time limitation
- Cơ quan thuế truy thu 10 năm (Luật Quản lý thuế 2019)
- Do đó tax indemnity phải survival ≥10 năm cho buyer
- Seller thường push for 5 năm → negotiation point

## Category 3: Legal / Contractual

### Contract review deliverables
- Material contract summary (top 20-50 by value)
- Change of control (CoC) clauses — nào require consent
- Termination rights on ownership change
- Non-compete / exclusivity commitment
- Guarantees given by company or by owner personally
- Litigation active + threatened

### CoC common trap
- Bank loan agreements typically require consent
- Landlord may require consent
- Key customer contracts (esp with State-Owned Enterprises)
- Insurance policies

**Nếu CoC consent bị deny → deal có thể fall through hoặc buyer force renegotiate.**

Mitigation:
- Third-party consent workstream (identify Week 1 of DD)
- Track consent status
- If key consent refused → assess deal viability

### Litigation
- Pending lawsuits — quantify potential loss
- Threatened claim — legal opinion likelihood
- Regulatory investigation — biggest risk

## Category 4: Regulatory (VN — VERY HIGH)

### Foreign investment restrictions

**WTO Commitment schedule** — foreign ownership cap:
- Banking: 30% cap
- Distribution: 100% allowed but licenses required
- Real estate: restricted for residential
- Telecom: various caps
- Airline: 34% cap

**Nghị định 31/2021/NĐ-CP** — Investment Law:
- Ngành kinh doanh có điều kiện (267 industries)
- Ngành cấm đầu tư (đối với NĐT nước ngoài)

### M&A approval required

Under **Luật Cạnh tranh 2018**:
- Notification to VCA (Vietnam Competition Authority) if:
  - Combined market share ≥20%
  - Combined revenue ≥1000 tỷ
- Review timeline: 30-90 days
- Concentration prohibited nếu SLC (substantial lessening of competition)

Under **Luật Đầu tư 2020, Điều 26**:
- Foreign investor acquiring VN company shares → registration required
- Timeline: 15 working days
- Approval in principle if in conditional business

**Sector-specific approvals:**
- SBV (banking, fintech, e-wallet)
- MOIT (energy, distribution)
- MIC (telecom, media)
- MOH (pharma, medical)
- MARD (agri, food processing)

### Deal killer regulatory risks
- Anti-monopoly denial
- FDI approval denied (rare but happens for sensitive sectors)
- Sector license non-transferable

## Category 5: Commercial / Customer risks (from CDD)

Reference: `ma-commercial-due-diligence`

Key mitigations:
- Post-closing customer retention KPI trong earn-out
- Non-compete với founder (18-36 months, defined geo)
- Customer meeting attend by buyer during DD (with seller present)
- Warranty on customer retention 12-24 months

## Category 6: Operational risks

- IT infrastructure obsolete
- Supply chain single point of failure
- Facility lease expire
- Equipment obsolete
- Vendor concentration
- Quality issues / recall risk
- Business continuity

Mitigation:
- CapEx normalization in EBITDA
- Facility lease extension pre-signing
- Vendor diversification plan

## Category 7: HR / Culture

- Key management retention
- Union / collective bargaining risk
- Wage inflation risk
- Skill gap post-transition
- Culture clash post-integration

### Key management retention framework

Identify top 10-15 execs critical. For each:
- Retention letter signed pre-signing
- Retention bonus (typically 25-100% of salary paid at 12-24 months post-close)
- Non-compete + non-solicit (18-36 months, defined geo/sector)
- Stock option / equity rollover

**Founder CEO** thường là biggest single risk. Structure:
- Transitional role 12-24 months mandatory
- Consulting agreement year 2-3 optional
- Earn-out tied to KPI

## Category 8: IP / Technology

- Brand / trademark ownership (registered under company vs owner personally)
- Software license compliance
- Copyright / content rights
- Domain name ownership
- Trade secret protection (NDA in place with employees + contractors)
- Open source usage compliance
- Data privacy (Nghị định 13/2023 personal data)

**Common VN issue:** Brand đăng ký dưới tên cá nhân chủ (not company). → require transfer to company pre-signing.

## Category 9: ESG (growing)

- Environmental compliance (waste, emissions, land)
- Labor practices (working hours, safety)
- Governance (board structure, related party)
- Supply chain (labor practices upstream)

Increasingly important cho FDI buyer (EU, US, JP) — họ có ESG mandate từ investor.

## Risk Register — deliverable format

```
ID    Category       Risk Description            L  I  Score  Owner    Mitigation                        Status
─────────────────────────────────────────────────────────────────────────────────────────────────────────────
R001  Tax           VAT under-declaration 5Y     4  4  16     BSN Tax  Special indemnity + escrow $2M    Open
R002  Regulatory    VCA approval required        3  4  12     Legal    File post-signing, condition CP   Open
R003  Legal         Key customer CoC consent    4  4  16     Legal    Obtain pre-signing               In progress
R004  Financial     WC volatility                3  2  6      FDD      WC true-up mechanism             Closed
R005  HR            CEO retention                4  5  20     HR       3-year retention + earn-out      Open
R006  Commercial    Top customer concentration   3  4  12     CDD      Non-compete + warranty           Open
R007  IP            Brand under owner name       5  2  10     Legal    Transfer pre-signing (CP)        In progress
─────────────────────────────────────────────────────────────────────────────────────────────────────────────
Total identified: 47 risks
- 3 CRITICAL (walk-away consideration)
- 8 HIGH (heavy mitigation)
- 19 MEDIUM
- 17 LOW
```

## Risk-based deal decision matrix

| Total CRITICAL | Total HIGH | Decision |
|---|---|---|
| 0 | ≤3 | Proceed with standard mitigations |
| 0 | 4-8 | Proceed but heavy escrow + indemnity |
| 1-2 | 4-8 | Restructure or price reduction 15-30% |
| 3+ | Any | Walk away or major restructure |

## VN-specific "always check" list

1. Thanh tra thuế kết luận 5 năm gần nhất
2. Sổ đỏ / Land use rights valid + owned by company
3. Nghị định 132/2020 transfer pricing docs
4. Environmental permits current
5. Fire safety certificate
6. Employee social insurance đóng đủ
7. Related-party transaction disclosed
8. Vendor / supplier tax invoice compliance
9. Brand registered dưới tên company (not cá nhân)
10. FDI ownership cap check

## Chi phí BSN

- Risk register + mitigation strategy: **150-250 triệu VND**
- Bundled với overall M&A advisory: no separate charge
- Post-signing risk monitoring: **50-100 triệu VND** quarterly retainer

Cross-ref: `ma-financial-due-diligence` (financial risks source), `ma-commercial-due-diligence` (commercial risks source), `ma-negotiation-strategy` (risks drive negotiation), `ma-term-sheet-design` (mitigations become terms), `ma-closing-and-integration` (residual risks post-close).
