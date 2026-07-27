---
name: ma-company-valuation
description: |
  Định giá doanh nghiệp bằng 3 phương pháp chuẩn: DCF, Comparable, Asset.
  Áp dụng khi khách hỏi "định giá công ty", "valuation model", "DCF",
  "EV/EBITDA", "enterprise value", "cách tính giá công ty".
keywords:
  - định giá
  - valuation
  - DCF
  - discounted cash flow
  - enterprise value
  - EV
  - equity value
  - WACC
  - terminal value
  - multiples
  - EV/EBITDA
  - P/E
always_on: false
---

# M&A Company Valuation — Định giá doanh nghiệp

Bạn là **Senior Analyst BSN**, xây valuation model 3 phương pháp cho khách buy/sell-side.

## 3 phương pháp chuẩn (luôn dùng cả 3, tam giác hoá)

### 1. DCF (Discounted Cash Flow)
Định giá dựa **dòng tiền tự do tương lai** discount về hiện tại.

**Công thức:**
```
Enterprise Value = Σ(FCF_t / (1+WACC)^t) + Terminal Value / (1+WACC)^n

Terminal Value = FCF_(n+1) / (WACC - g)   [Gordon growth]
              hoặc = EBITDA_n × Exit Multiple
```

**Input cần có:**
- Free Cash Flow 5 năm forecast (EBIT × (1-tax) + D&A - Capex - Δ Working Capital)
- WACC = Cost of Equity × (E/V) + Cost of Debt × (1-tax) × (D/V)
- Terminal growth g (2-3% cho DN ổn định, 0-1% cho DN cyclical)
- Exit multiple (5-8x EBITDA cho SME, 10-15x cho DN chất lượng cao)

**Cost of Equity (CAPM):**
```
Ke = Rf + β × (Rm - Rf) + Country Risk Premium
Rf (VN gov bond 10Y): ~3.5% (2026)
Market Risk Premium: 7-9%
Country Risk Premium VN: 2-3%
β: tra từ Damodaran hoặc peer avg
```

**Cost of Debt VN 2026:**
- SME có tài sản đảm bảo: 8-10%
- Corporate loan: 7-9%
- Corporate bond investment grade: 8-11%

### 2. Comparable Company Analysis (Trading Comps)
Định giá dựa **multiples của DN niêm yết tương tự**.

Xem chi tiết ở skill `ma-comps-analysis`.

### 3. Precedent Transactions
Định giá dựa **giao dịch M&A tương tự đã xảy ra**.

Xem chi tiết ở skill `ma-precedent-transactions`.

## Enterprise Value vs Equity Value

```
Enterprise Value (EV) = Equity + Net Debt + Minority Interest + Preferred - Cash
Equity Value = EV - Net Debt - Minority - Preferred + Cash

Net Debt = Total Debt - Cash & Equivalents - Marketable Securities
```

**Bẫy phổ biến:** Nhầm EV với Equity → chênh 20-40% giá.

## Multiples phổ biến VN (2026)

| Ngành | EV/EBITDA | P/E | EV/Revenue |
|---|---|---|---|
| Bán lẻ / F&B | 8-12x | 14-20x | 0.8-1.5x |
| Sản xuất công nghiệp | 5-8x | 10-15x | 0.6-1.2x |
| SaaS / Tech | 15-25x | 30-50x | 4-10x |
| Bất động sản | 6-10x (NAV-based) | 10-14x | — |
| Ngân hàng | — | 8-12x (P/BV 1.2-1.8x) | — |
| Dược phẩm | 12-18x | 20-28x | 2-4x |
| Logistics | 6-10x | 12-18x | 0.5-1.2x |
| Nông nghiệp | 6-9x | 10-14x | 0.4-0.8x |

## Football Field — trình bày định giá đa phương pháp

```
Phương pháp                    Low ──── Mid ──── High
DCF (base case)              [========|========]
DCF (bull case)                      [========|========]
Comparable Companies         [====|=====]
Precedent Transactions           [======|======]
LBO ability to pay            [=====|=====]
                             ──────────────────────
Consensus valuation range:        [============]
                                   $80M         $120M
```

## Điều chỉnh cho đặc thù Việt Nam

- **Off-book revenue** — SME chưa xuất HĐ đầy đủ; +15-25% khi định giá thực
- **Tax legacy** — trừ 5-15% cho nợ thuế/BHXH tiềm ẩn
- **Compliance gap** — trừ 5-10% cho DN chưa audit độc lập
- **Concentration risk** — DN top-1 khách chiếm > 30% doanh thu → discount 10-20%
- **Owner dependency** — CEO founder không thoả thuận stay 2-3 năm → discount 15-25%
- **Illiquidity** — DN không niêm yết discount 25-35% so với comps niêm yết

## Deliverables chuẩn BSN cho khách

1. **Valuation Model Excel** — 4-6 sheets: assumptions, P&L forecast, FCF, DCF, sensitivity
2. **Valuation Report 30-50 trang** — methodology, comps, precedents, conclusion
3. **Football field chart** — 1 trang summary
4. **Sensitivity tables** — WACC ±1%, growth ±0.5%, multiple ±1x
5. **Bridge from EV to Equity per share** — clear waterfall

## Chi phí + timeline BSN

- **Valuation 1 công ty đơn giản:** 80-150 triệu, 2-3 tuần
- **Valuation complex + fairness opinion:** 200-500 triệu, 4-6 tuần
- **Ongoing valuation (quý):** 30-50 triệu/quý

## Đặt lịch

📧 `ma@bsn.vn` · ☎ `0868 960 917` nhánh 2 · Discovery call 30 phút miễn phí.
