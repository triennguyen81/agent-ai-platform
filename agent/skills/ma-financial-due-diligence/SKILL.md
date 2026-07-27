---
name: ma-financial-due-diligence
description: |
  Financial Due Diligence (FDD) framework — quality of earnings, quality
  of assets, working capital normalization, debt-like items. Áp dụng khi
  khách hỏi "FDD", "financial DD", "quality of earnings", "QoE",
  "kiểm tra tài chính deal".
keywords:
  - financial due diligence
  - FDD
  - quality of earnings
  - QoE
  - working capital
  - debt-like items
  - EBITDA normalization
  - vendor DD
always_on: false
---

# Financial Due Diligence (FDD) — Framework chuẩn Big-4

Bạn là **FDD Partner của BSN**, độc lập với auditor, focus vào 4 workstreams: **QoE, QoA, WC, Debt-like**.

## 4 Workstreams chính

### 1. Quality of Earnings (QoE)
"Reported EBITDA có bền vững, có tái diễn không?"

### 2. Quality of Assets (QoA)
"Balance sheet items có real value không?"

### 3. Working Capital (WC) normalization
"Steady-state WC là bao nhiêu để set purchase price baseline?"

### 4. Debt-like items
"Có nợ tiềm ẩn nào ngoài balance sheet không?"

## Workstream 1: Quality of Earnings

### EBITDA adjustments framework

Reported EBITDA → Adjusted EBITDA qua 5 tiers:

**Tier A: One-off / Non-recurring**
- Legal settlement (one-off) → add back
- Fire/flood insurance recovery → strip out
- Gain/loss disposal of asset → strip out
- COVID government subsidy 2020-2021 → strip out
- Restructuring cost (real one-time) → add back

**Tier B: Owner-related (relevant nếu ownership change)**
- Owner compensation above market → add back excess
- Personal expenses (car, travel, family) → add back
- Related-party rent below market → adjust to market
- Related-party purchase above market → adjust

**Tier C: Non-cash items**
- Depreciation/amortization → already excluded from EBITDA
- Stock-based comp → controversial, strategic buyer usually add back, PE usually not

**Tier D: Pro-forma adjustments**
- Full year effect of new store opened Q3 → annualize
- Recent price increase → full-year run rate
- New contract won → include (if signed)
- Recent M&A integration → normalize

**Tier E: Sustainability adjustments (NEGATIVE add-backs — reduce EBITDA)**
- Under-invested maintenance capex → normalize higher capex forward
- Under-staffed → normalize salary cost higher
- Deferred marketing spend → normalize
- Off-book revenue reliance → discount

### VN-specific EBITDA issues

**Off-book / Undeclared revenue** (very common cho VN SME):
- Cash sales không lên hoá đơn (~10-30% cho F&B, retail)
- Approach: forensic bank deposit analysis, POS reconciliation
- **DO NOT** add back off-book vào adjusted EBITDA cho buyer FDD
  → sustainability risk sau deal (buyer không thể replicate legally)
- Show as **"informational only"** với discount factor

**Tax legacy exposure:**
- Under-declared VAT past 5 years
- Corporate income tax dispute pending
- Transfer pricing risk (nếu related party)
- Approach: review tax filings + inspection reports 5 years
- Quantify exposure + assume as "debt-like item"

**Family employees:**
- Anh em, con cái salary above market → adjust down
- Owner CEO salary usually way above → adjust to market
- Include successor cost trong adjustment (new professional CEO cost $150-300k/năm)

### QoE report typical output

```
Reported EBITDA (LTM Sep 2025)      $8.0m
─────────────────────────────────────────
Non-recurring adjustments:
  + One-off legal settlement          +$0.3m
  - COVID subsidy                     -$0.2m
  + Restructuring cost                +$0.5m
  - Insurance recovery                -$0.1m

Owner-related adjustments:
  + Excess owner compensation         +$0.4m
  + Personal expenses                 +$0.2m
  + Related-party rent below market   -$0.3m

Pro-forma adjustments:
  + Full year effect new HCMC store   +$0.6m
  + New enterprise contract           +$0.4m

Sustainability adjustments:
  - Under-maintenance capex normalize -$0.3m
  - Successor CEO cost premium        -$0.2m
─────────────────────────────────────────
Adjusted EBITDA (LTM)                $9.3m
─────────────────────────────────────────

Buyer view multiple: 8x
Purchase EBITDA: $9.3m × 8 = $74.4m (enterprise value)
```

## Workstream 2: Quality of Assets

### Cash
- Restricted cash (bank guarantee, escrow) → not free cash
- Petty cash undocumented → reduce
- Foreign currency at market rate
- Fake cash (deposit slip mismatched với bank confirmation) → red flag

### Accounts Receivable
- Aging analysis (0-30, 31-60, 61-90, 90+)
- Provision adequacy — >90 days usually 100% provision
- Related-party AR — treat separately
- AR to top 10 customer — concentration risk

### Inventory
- Aging + slow-moving analysis
- Physical count discrepancy
- Obsolete inventory not written off
- Fair value review (LIFO vs FIFO)

### PP&E (Property, Plant, Equipment)
- Existence — physically verify (site visit)
- Ownership — title deed verification
- Impairment test — idle equipment
- Restoration obligation (cần tháo dỡ khi hết lease)

### Intangibles
- Goodwill — impairment test
- Software — license verify
- Brand — third-party valuation
- Customer list — quantify

### VN-specific asset issues

**Real estate:**
- Sổ đỏ (land title) — verify with cơ quan địa chính
- Land use rights expire — remaining years
- Zoning matches usage
- Đền bù giải phóng mặt bằng liability

**Vehicles:**
- Company car nhưng dùng cá nhân
- Vehicle titled under owner personally

**Inventory in bonded warehouse:**
- Duty accrued nếu import
- Match với customs record

## Workstream 3: Working Capital normalization

### Purpose
Set "target working capital" — deviation từ target → price adjustment at closing.

### Method
1. Calculate WC = AR + Inventory - AP - Accrued (excluding cash and debt)
2. Analyze 24 months trailing monthly WC
3. Remove seasonality
4. Strip one-off items
5. Compute "steady state" WC as average
6. Set target = steady state

### Example

```
Monthly WC 24 months trailing:
- Average: $12.5m
- Peak (seasonal Dec): $18m
- Trough (Feb): $8m
- Coefficient of variation: 20%

Adjusted for one-off:
- Remove Q3 2024 stockpile ($2m)
- Add back Q1 2025 write-off ($1m)

Steady-state WC = $11.5m
→ Set target = $11.5m

At closing:
  Actual WC = $10.3m → shortfall $1.2m → buyer pays $1.2m less
  Actual WC = $12.8m → surplus $1.3m → buyer pays $1.3m more
```

### WC true-up mechanism in SPA
- Estimated closing statement (T-3 days)
- Provisional purchase price adjust
- Final closing statement (T+60 days) — reviewed by both auditor
- Final true-up 30 days after

## Workstream 4: Debt-like Items

**Not on balance sheet but treated as debt in purchase price calculation.**

Common items:
- Unfunded pension obligation
- Deferred tax liability (real ones, likely to crystallize)
- Contingent liability (litigation, tax dispute)
- Long-term lease obligation (post-IFRS16 already on BS but check)
- Bonuses accrued but not paid
- Customer deposits (refundable)
- Warranty obligation not accrued
- Environmental remediation
- Restoration/decommissioning
- Off-book tax exposure quantified

Formula:
```
Enterprise Value          $100m
- Debt                    ($20m)
- Debt-like items         ($5m)     ← this
- Preferred equity        ($0m)
+ Cash                    +$8m
+ Working capital adjust  +$1.3m
─────────────────────────────
Equity value              $84.3m
```

## FDD deliverables

1. **QoE report** (40-60 pp) — adjusted EBITDA detail
2. **QoA report** (20-30 pp) — asset review
3. **WC analysis** (10-15 pp) — WC target recommendation
4. **Debt-like items schedule** (5-10 pp)
5. **Findings + red flags** (5 pp summary)
6. **Adjusted financial model** (Excel)

## Sell-side vs Buy-side FDD

| Item | Vendor FDD (sell-side) | Buy-side FDD |
|---|---|---|
| Objective | Present positive story, pre-empt issues | Find issues, negotiate price down |
| Timing | Pre-marketing (Week 2-5) | Post-LOI (Week 22-30) |
| Cost | Seller pays, $80-200k | Buyer pays, $150-400k |
| Standard | Big-4 or top-tier IB | Big-4 preferred |
| Distribution | Buyer via data room | Buyer only |
| Vendor DD saves | ~30-40% time in DD phase | N/A |

## Red flags trigger deeper DD

- Reported margins >>> industry average
- Very lumpy monthly revenue with no explanation
- Related-party transactions >10% of revenue/cost
- Customer concentration >30%
- Rapid AR aging deterioration
- Inventory grow faster than revenue
- Frequent auditor change
- Tax filing delays
- Owner using company for personal purposes
- No monthly financial reporting rigor

## Chi phí BSN

- Vendor FDD (sell-side): **150-250 triệu VND** (2-3 months)
- Buy-side FDD support (khách hire Big-4, BSN advise): **80-150 triệu VND**
- Buy-side FDD lead (BSN thực hiện): **200-400 triệu VND** cho SME deal, **400-800 triệu** cho large deal

Cross-ref: `ma-risk-assessment` (findings feed into risk register), `ma-negotiation-strategy` (FDD findings drive price adjustment), `ma-commercial-due-diligence` (parallel workstream).
