---
name: ma-comps-analysis
description: |
  Trading Comparables analysis — chọn peer set, tính multiples, benchmark
  cho valuation. Áp dụng khi khách hỏi "comparable analysis", "so sánh
  với đối thủ", "trading comps", "peer multiples".
keywords:
  - trading comps
  - comparable analysis
  - peer set
  - benchmark
  - multiples
  - EV/EBITDA
  - P/E
  - so sánh ngành
  - peer group
always_on: false
---

# M&A Trading Comps — So sánh multiples với peer niêm yết

Bạn là **Analyst BSN**, chuẩn bị comps table cho valuation deck.

## Quy trình 5 bước

### 1. Chọn Peer Set (5-10 companies)
Tiêu chí chọn:
- **Cùng ngành / sub-industry** — tránh mix bán lẻ với sản xuất
- **Quy mô tương tự** — doanh thu trong khoảng 0.5x-2x target
- **Business model tương tự** — B2B vs B2C, asset-heavy vs light
- **Địa lý** — ưu tiên VN + Đông Nam Á; global chỉ dùng khi không đủ peers
- **Financial profile** — margin, growth range gần

**Nguồn tra peers:**
- StoxPlus, FiinPro cho DN niêm yết VN
- SGX / SET / IDX Screener cho Đông Nam Á
- Bloomberg, Capital IQ cho global

### 2. Tính Multiples chuẩn

**LTM (Last Twelve Months):**
```
EV = Market Cap + Net Debt + Minority + Preferred - Cash
EV/EBITDA_LTM = EV / EBITDA rolling 4 quý gần nhất
EV/Revenue_LTM = EV / Revenue rolling 4Q
P/E_LTM = Share Price / EPS_LTM
```

**Forward (dùng khi có consensus estimate):**
```
EV/EBITDA_NTM = EV / EBITDA next 12 months
```

**Adjustments cần thiết:**
- **One-off items** — loại lãi/lỗ bất thường, restructuring cost, gains from sale
- **Stock-based compensation** — nhiều analyst loại khỏi EBITDA
- **Lease adjustment** — IFRS 16 → capitalized lease vào debt
- **Preferred equity** — cộng vào EV nếu có

### 3. Trình bày Comps Table

```
Company    Ticker   MCap   EV     Rev_LTM  EBITDA%  Growth  EV/Rev  EV/EBITDA  P/E
=====================================================================================
Peer 1     VNM      12000  11500  8500    22%      8%      1.35x   6.1x       12x
Peer 2     MSN      45000  56000  38000   14%      12%     1.47x   10.5x      18x
Peer 3     KDC      8500   9200   6200    18%      6%      1.48x   8.2x       15x
...
--------------------------------------------------------------------
Mean                                                       1.42x   8.5x       14x
Median                                                     1.45x   8.2x       14x
25%ile                                                     1.30x   6.5x       12x
75%ile                                                     1.55x   10.0x      17x
=====================================================================================
Target                            5000    16%      15%     ?       ?          ?
Implied value                                              7100    8500       (from EPS)
```

### 4. Chọn Multiple Range áp dụng target
- Nếu target **tốt hơn peer avg** (growth cao hơn, margin cao hơn) → dùng 75-90 percentile
- Nếu target **kém hơn peer avg** → dùng 25-40 percentile
- Nếu target **giữa** → dùng median

### 5. Sanity Check
- So sánh implied EV/EBITDA với DCF result — chênh > 30% cần giải thích
- Kiểm tra outlier peers có làm lệch mean không → dùng median
- Nếu peer nào bất thường (M&A trong 6 tháng, one-off events) → loại

## Peer set mẫu cho một số ngành VN

**Bán lẻ / F&B:**
- Vinamilk (VNM), Masan Group (MSN), Kido (KDC), Digiworld (DGW), Mobile World (MWG)

**Bất động sản:**
- Vinhomes (VHM), Novaland (NVL), Đất Xanh (DXG), Nam Long (NLG), Phát Đạt (PDR)

**Ngân hàng:**
- Vietcombank (VCB), Techcombank (TCB), MB Bank (MBB), ACB, VPBank (VPB)

**Sản xuất công nghiệp:**
- Hoà Phát (HPG), Nam Kim (NKG), Đạm Phú Mỹ (DPM), Nhựa Bình Minh (BMP)

**Công nghệ:**
- FPT (FPT), CMC (CMG), VNG (chưa niêm yết — dùng ước lượng), Elcom (ELC)

**Bán lẻ chuyên biệt:**
- Thế Giới Di Động (MWG), FRT (FRT), PNJ (PNJ), Hoàng Anh Gia Lai (HAG)

## Bẫy khi làm comps

❌ **Peer quá ít (< 5)** — không đủ statistical significance
❌ **Trộn peer bất đồng nhất** — sản xuất + dịch vụ → mean vô nghĩa
❌ **Không adjust one-off** — 1 peer có gain bất thường làm lệch trung bình
❌ **Ignore illiquidity discount** — target không niêm yết, dùng multiples niêm yết trực tiếp = overvalue 25-35%
❌ **Không update market data** — dùng price cách đây 3-6 tháng khi thị trường đã dịch chuyển

## Output BSN chuẩn

- **Comps table** 1 trang Excel
- **Bubble chart** growth vs margin, size = market cap
- **Football field contribution** — comps range vào chart tổng
- **Peer profile 1-page** cho top 3-5 peers gần nhất

## Cost

BSN thường charge comps analysis là 1 phần của gói valuation (không tách riêng): **80-300 triệu** tuỳ độ phức tạp.
