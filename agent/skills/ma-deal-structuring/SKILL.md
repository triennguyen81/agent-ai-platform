---
name: ma-deal-structuring
description: |
  Thiết kế cấu trúc deal M&A — asset vs share, control vs minority,
  earn-out, escrow, tax structuring. Áp dụng khi khách hỏi "cấu trúc
  deal", "mua tài sản hay cổ phần", "deal structure", "earn-out".
keywords:
  - deal structure
  - cấu trúc deal
  - asset deal
  - share deal
  - stock purchase
  - asset purchase
  - earn-out
  - escrow
  - contingent consideration
  - m&a structure
always_on: false
---

# M&A Deal Structuring — Thiết kế cấu trúc giao dịch

Bạn là **Deal Attorney tại BSN**, thiết kế deal structure tối ưu tax + risk.

## 4 câu hỏi chiến lược trước khi structure

1. **Buyer là ai?** Chiến lược (strategic) — muốn synergy, hay tài chính (PE) — muốn ROIC?
2. **Mua bao nhiêu %?** Control (>50%), majority (>65%), minority strategic (10-30%), acqui-hire?
3. **Consideration là gì?** Tiền mặt / cổ phiếu / mix / earn-out?
4. **Rủi ro nào share?** Buyer chịu / seller chịu / escrow?

## Asset Deal vs Share Deal — chọn cái nào?

| Tiêu chí | Asset Deal | Share Deal |
|---|---|---|
| **Mua gì?** | Tài sản + hợp đồng cụ thể | Cổ phần công ty (kế thừa mọi thứ) |
| **Liability legacy** | Không kế thừa (chỉ HĐ ghi trong deal) | **Kế thừa toàn bộ** (thuế cũ, kiện tụng, BHXH...) |
| **Tax buyer** | Bước-up basis → depreciation cao hơn | Không step-up basis |
| **Tax seller** | Thường phải chịu 2 lớp thuế (corp + personal) | Chỉ 1 lớp (VAT/TNCN trên chênh lệch) |
| **Process** | Chuyển từng HĐ, giấy phép | Đơn giản — chuyển cổ phần |
| **Regulator** | Ít approval | Có thể cần MOIT/UBCK/SBV (tuỳ) |
| **Khi nào phù hợp** | Buyer sợ legacy risk, mua 1 mảng | Đơn giản, seller muốn exit sạch |

**Rule of thumb VN:** SME dưới 100 tỷ → share deal đơn giản. Deal > 100 tỷ + có nhiều legacy risk → asset deal chi tiết.

## Control Structures

### Full Acquisition (100%)
- Đơn giản nhất
- Buyer toàn quyền, không lo minority shareholder blocking
- Seller exit sạch

### Majority (51-99%)
- Buyer control, seller giữ lại minority stake
- Cần **Shareholder Agreement (SHA)** cẩn thận: quyền veto, drag-along, tag-along, ROFR
- Phù hợp khi buyer muốn seller **stay engaged** để chuyển giao know-how

### Strategic Minority (10-49%)
- Không control nhưng có ảnh hưởng
- Cần **board seat** + **veto rights** trên major decisions (M&A, borrowing > threshold, capex > threshold, hiring CEO/CFO)
- Phù hợp cho: quỹ PE growth capital, chiến lược partnership

### Joint Venture (50-50)
- Nguy cơ **deadlock** cao — phải có tiebreaker
- Cần **JV Agreement** rất chi tiết
- Ít dùng trong VN M&A truyền thống

## Consideration Structures

### 100% Cash at Close
- Đơn giản nhất, seller thích
- Buyer cần financing capacity

### Stock-for-Stock
- Buyer bảo tồn cash
- Seller nhận cổ phần buyer — có upside nếu buyer tăng giá
- Cần valuation cả 2 bên, complex hơn

### Cash + Stock Mix (VD 70% cash + 30% stock)
- Balanced, seller có exposure vào buyer
- Phổ biến trong strategic M&A

### **Earn-out** (contingent consideration)
```
Base consideration: $50M at close
+ Earn-out: up to $20M based on:
  - Year 1 revenue ≥ X → $8M
  - Year 2 EBITDA ≥ Y → $12M
```
- Dùng khi 2 bên bất đồng về valuation
- Rủi ro: seller có thể "gaming" số → design KPI cẩn thận
- Thời gian typical: 2-3 năm

### Seller Financing / Vendor Loan
- Seller cho buyer vay 1 phần giá deal
- Trả góp 3-5 năm với lãi suất
- Phổ biến với deal small-mid market VN

## Escrow / Holdback

Giữ lại **10-20% giá deal** trong tài khoản escrow bên thứ 3 để:
- **Indemnity** — cover breach of reps & warranties
- **Working capital adjustment** — post-closing true-up
- **Tax legacy** — cover các nghĩa vụ thuế cũ phát sinh

Escrow release schedule typical:
- 50% sau 6 tháng
- 30% sau 12 tháng
- 20% sau 18-24 tháng (hoặc khi hết statute of limitations tax)

## Tax Structuring — Việt Nam

**Chuyển nhượng cổ phần công ty VN — thuế cho seller:**
- **Cá nhân** — 0.1% × giá bán (thuế TNCN, khoán, phần lớn tính theo giá này)
- **Doanh nghiệp** — 20% × chênh lệch (giá bán - giá vốn) = TNDN
- **NĐT nước ngoài** — 0.1% × giá bán (áp dụng withholding)

**Chuyển nhượng tài sản:**
- Bên bán chịu VAT 10% (trừ 1 số exempted)
- Bên bán chịu TNDN 20% trên lợi nhuận
- Có thể dùng "hoà tan sáp nhập" để tránh double taxation trong 1 số trường hợp

**Cấu trúc offshore:**
- Nhiều FDI dùng SPV Singapore/HK holding cổ phần VN → khi bán offshore, thuế thấp hơn
- **Cảnh báo:** VN có capital gains tax "chuyển nhượng gián tiếp" từ NĐ 132/2020 — không phải cứ offshore là né được

## Common Pitfalls

❌ **Không tính hết legacy tax** trong share deal — buyer inheritance
❌ **Escrow quá thấp** — không cover breach lớn
❌ **Earn-out KPI dễ manipulate** — seller sẽ optimize KPI, không optimize business
❌ **SHA thiếu drag-along** — buyer minority sau này khó exit
❌ **Không xin approval từ đầu** — deals ngân hàng, viễn thông, vận tải hàng không phải xin MOIT/SBV

## Deliverables BSN cho khách

1. **Deal Structure Memo** — 10-15 trang, recommend structure + rationale
2. **Tax analysis** — so sánh 2-3 structure options
3. **Term Sheet template** — reflect structure đã chọn
4. **Timeline** — key milestones + approval needed
5. **Coordination** với luật sư, tax advisor khách

## Chi phí BSN

- Structure design (không kèm valuation/DD): **100-250 triệu**, 2-4 tuần
- Kèm luôn valuation + advisory M&A: gói **500 triệu - 2 tỷ** tuỳ deal size

Cross-ref: xem `ma-capital-structure` (financing) và `ma-term-sheet-design` (LOI).
