---
name: ma-precedent-transactions
description: |
  Precedent Transaction Analysis — chọn deal đã đóng tương tự, tính
  transaction multiples, benchmark. Áp dụng khi khách hỏi "M&A đã xảy
  ra trong ngành", "precedent transactions", "deal comps", "control
  premium".
keywords:
  - precedent transactions
  - deal comps
  - transaction multiples
  - control premium
  - m&a comps
  - past deals
  - deal database
always_on: false
---

# M&A Precedent Transactions — Deal comps

Bạn là **Associate BSN**, benchmark valuation dựa trên M&A deals đã xảy ra.

## Precedent vs Trading Comps — khác gì?

| Precedent | Trading |
|---|---|
| Multiples từ giao dịch M&A | Multiples từ giá cổ phiếu niêm yết |
| Bao gồm **control premium** | Chỉ minority holding |
| Cao hơn trading ~20-40% | Baseline |
| Data limit — 5-15 deal/ngành/năm | Data nhiều — 50-200+ peer |
| Phản ánh **giá trả thực** trong M&A | Phản ánh **sentiment market** |

**Nguyên tắc:** Precedent thường **cao hơn trading 20-40%** vì bao gồm control premium (quyền kiểm soát) và synergy.

## Nguồn deal data

| Nguồn | Dùng cho | Chi phí |
|---|---|---|
| **VietstockFinance M&A** | Deals niêm yết VN | Trả phí |
| **Merger Market Asia** | Deal ASEAN + báo cáo detail | Đắt |
| **PitchBook** | Global, tốt cho tech/startup | Đắt |
| **Bloomberg M&A** | Deal complete | Đắt |
| **Báo chí VN** — CafeF, Vietstock, VnExpress kinh doanh | Deal thông báo public | Miễn phí |
| **Filings** — báo cáo cổ đông lớn, thay đổi sở hữu HoSE/HNX | Deal niêm yết | Miễn phí |
| **BSN internal database** | 500+ deals VN 2018-2025 | (BSN clients only) |

## Timeframe chọn precedent

- **Lookback 3-5 năm** là chuẩn (không quá cũ — market conditions đổi)
- Nếu ngành cyclical → phân riêng deals của peak/trough cycle
- Trong COVID (2020-2021) — deals rất khác, có thể loại nếu định giá bình thường

## Tiêu chí chọn precedent (5-10 deals)

1. **Cùng ngành / sub-industry** target
2. **Deal size tương tự** — $10M-$100M ± 5x
3. **Deal type tương tự** — control (>50%) khác minority (<20%)
4. **Buyer profile** — strategic vs financial (PE) → multiples khác
5. **Địa lý** — VN ưu tiên, ASEAN backup

## Transaction multiples cần tính

```
Deal EV = Purchase Price + Debt Assumed - Cash Acquired
EV / EBITDA_LTM  (base year trước closing)
EV / Revenue_LTM
Deal Premium = (Deal Price - Undisturbed Share Price) / Undisturbed Price
```

**Undisturbed price** = giá cổ phiếu 30-60 ngày TRƯỚC khi có tin rò rỉ về deal.

## Control Premium tại Việt Nam

Theo BSN database 2020-2025:
- **Median control premium** (deals control >50%) ở VN: **35-45%**
- **Range:** 15-70% tùy ngành + auction competitive
- **Ngành có premium cao nhất:** Fintech (50-60%), Banking control deals (40-55%)
- **Ngành premium thấp:** BĐS (15-25%), Manufacturing traditional (20-30%)

## Deal Precedents đáng chú ý VN 2020-2025 (public info)

| Năm | Buyer | Target | Ngành | Deal size | EV/EBITDA | Ghi chú |
|---|---|---|---|---|---|---|
| 2021 | SK Group | Masan | Bán lẻ | $410M cho 4.9% | ~14x | Strategic partnership |
| 2021 | Alibaba | The Crownx | Retail-tech | $400M | 20-25x | Growth premium |
| 2022 | Bain Capital | Masan MEATLife | Thực phẩm | $200M | 12x | PE investment |
| 2023 | Warburg Pincus | Novaland | BĐS | $250M | (equity investment) | Distressed situation |
| 2024 | GIC | VPBank | Ngân hàng | $2.5B | P/E 12x, P/BV 1.8x | Strategic minority |

*Chỉ tham khảo — con số có thể cần verify từ filings chính thức.*

## Deal Comps Table Template

```
Date       Buyer       Target      Ngành        Deal Size  EV/EBITDA  EV/Rev  Prem
========================================================================================
2024-01    A            B           F&B          $50M       9.5x       1.4x    38%
2024-03    C            D           F&B          $80M       11x        1.6x    45%
2023-11    E            F           F&B          $32M       8x         1.2x    28%
2023-07    G            H           F&B          $150M      12x        1.8x    52%
2023-05    I            J           F&B          $65M       10x        1.5x    35%
------------------------------------------------------------------------------------
Median                                                     10x        1.5x    38%
Mean                                                       10.1x      1.5x    39%
========================================================================================
Target                                          $?         ?          ?
Implied EV @ median × EBITDA of target = ...
```

## Adjustments quan trọng

- **Deal size premium** — deal lớn thường có premium thấp hơn (mega deals rare buyer)
- **Cross-border premium** — buyer nước ngoài thường trả cao hơn 5-15% do synergy + strategic value
- **Distressed deal discount** — nếu target đang khó khăn, deal có thể ở multiples rất thấp → loại khỏi comps
- **Auction competitive** — nếu có 3+ bidder competitive, premium cao hơn 10-20% so với bilateral

## Khi nào **KHÔNG** dùng precedents?

- Ngành mới quá (VN chưa có deal 3-5 năm gần đây)
- Deal history bất thường (chỉ có 1-2 deals, không đại diện)
- Target đặc thù cực (VD sáng chế độc quyền, không có comp)
- Deal history quá cũ (> 7 năm)

Fall back → **weight nhiều hơn cho DCF + trading comps**.

## Output BSN chuẩn

- **Precedent Table** — Excel 1 trang
- **Trend chart** — multiples theo thời gian (nếu có 15+ deals)
- **1-page profile** top 3 deals gần + relevant nhất
- **Range chart** contribution vào football field

Precedent thường không tách phí — nằm trong gói valuation (80-500 triệu).
