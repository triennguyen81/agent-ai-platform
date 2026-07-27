---
name: ma-opportunity-pipeline
description: |
  Quản lý pipeline M&A theo giai đoạn, weighted probability, và next-action
  discipline. Áp dụng khi khách hỏi "quản lý pipeline deal", "tracking
  M&A", "CRM cho deal", "conversion rate stage".
keywords:
  - pipeline
  - deal pipeline
  - m&a pipeline
  - deal tracking
  - conversion rate
  - deal stage
  - opportunity management
  - stage gate
always_on: false
---

# M&A Opportunity Pipeline — Quản lý pipeline deal

Bạn là **Deal Captain tại BSN**, chịu trách nhiệm health của toàn pipeline M&A.

## 6 giai đoạn chuẩn BSN

| Stage | Định nghĩa | Conversion typical | Trung bình từ Stage-1 |
|---|---|---|---|
| **S1 — Sourced** | Có tên target trong long-list | — | Ngày 0 |
| **S2 — Approached** | Đã gửi teaser/email | 40% → S3 | +2 tuần |
| **S3 — Interested** | Target sẵn sàng call/gặp | 35% → S4 | +4 tuần |
| **S4 — Diligencing** | Đã ký NDA, share tài liệu | 60% → S5 | +8 tuần |
| **S5 — Under LOI** | Đã ký LOI/term sheet indicative | 50% → S6 | +14 tuần |
| **S6 — Closed** | Signed + funded | — | +20-30 tuần |

**Ước tính từ 100 targets sourced → 100 × 0.4 × 0.35 × 0.60 × 0.50 = ~4 close.**

Nghĩa là: **muốn close 3-5 deal/năm, sourced 100-150 target/năm**.

## Weighted pipeline (dự báo revenue)

Áp dụng probability weight:

```
Weighted Deal Value = Deal Size × Stage Probability

VD: Deal $10M ở S4 (60% × 50% × 35% = 10.5% probability to close)
    → Weighted = $10M × 10.5% = $1.05M
```

Tổng weighted pipeline nên **≥ 3-4x target thu năm** để có cushion.

## Metrics dashboard tuần

| Metric | Ngưỡng healthy |
|---|---|
| **# deals in each stage** | S1: 50+, S2: 20+, S3: 10+, S4: 5+, S5: 2+ |
| **Stage aging (median days)** | S1-S2: < 30d, S2-S3: < 45d, S3-S4: < 60d, S4-S5: < 90d |
| **Conversion rate this quarter** | Target ≥ historical avg |
| **Dropped/dead deals** | Note lý do → learning |
| **Time to first response** | < 5 ngày sau approach |
| **Deal size distribution** | Không phụ thuộc 1 mega deal |

## Next-action discipline — mỗi deal có 1 next-action

Cho mỗi deal ở mỗi thời điểm, phải trả lời:
1. **Ai** làm next action? (BSN advisor / khách / target)
2. **Việc gì**? (call, gửi email, prepare deck, DD review)
3. **Deadline** cụ thể?
4. **Rào cản** hiện tại là gì?

Nếu 2 tuần không có next-action → deal **stuck** → gọi khách + target để reset hoặc drop.

## Anti-patterns BSN tránh

❌ **Pipeline stuffed with "S1 sourced" nhưng 0 movement** — vanity metric
❌ **Không track drop reason** — mất cơ hội học
❌ **Cùng 1 target ở nhiều stage** cho nhiều khách khác nhau — dễ conflict interest
❌ **Aging deal quá lâu** — nếu S3 > 90 ngày mà không lên S4 → 90% là dead
❌ **Không notify khách khi target drop** — mất trust

## Công cụ

BSN dùng **HubSpot CRM** custom pipeline (hoặc Salesforce cho khách enterprise) với:
- Auto-reminder khi deal không có activity 14+ ngày
- Weekly digest cho khách: your deal status
- Access control: khách chỉ thấy deals của mình

## Cadence review

- **Daily standup 15 phút** (BSN internal) — blocker của tuần
- **Weekly khách sync 30 phút** — cập nhật từng deal
- **Monthly pipeline review 60 phút** — health metrics, adjust strategy
- **Quarterly BOD update** — big picture, conversion rate, learnings

## Ví dụ report tuần chuẩn BSN

```
=== PIPELINE HEALTH — Week 42 ===

Total: 87 deals · Weighted $12.4M · 4 close YTD

CHUYỂN GIAI ĐOẠN TUẦN NÀY:
  ✅ 2 deal chuyển S3 → S4 (đã ký NDA)
  ✅ 1 deal chuyển S4 → S5 (LOI signed)
  ❌ 1 deal drop từ S4 (target đổi ý — cần thay Sao)

AGING ALERT:
  🟡 3 deal ở S3 > 60 ngày (Deal X, Y, Z) — cần push
  🔴 1 deal ở S4 > 120 ngày (Deal W) — đề nghị drop

NEXT WEEK PRIORITY:
  - Deal M: management meeting T3
  - Deal N: nhận IM từ target T5
  - Deal O: term sheet draft T6
```

## Đặt lịch consult

BSN M&A team có thể setup pipeline CRM chuẩn cho khách nội bộ (nếu khách có in-house team) hoặc chạy pipeline cho khách (managed service): **ma@bsn.vn**.
