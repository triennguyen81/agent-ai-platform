# Kế hoạch — Agent Trợ Lý Kế Toán

**Phiên bản:** 1.0 · **Ngày:** 2026-08-24 · **Áp dụng cho tenant:** Việt Đăng (mở rộng)

---

## Tổng quan 3 Use Case

| UC | Tên | Trạng thái | % done |
|---|-----|-----------|:------:|
| **UC1** | Xử lý hoá đơn (invoice) | ✅ MVP + AR context + 3-way match + risk flags | 75% |
| **UC2** | Xử lý phiếu thu nợ | ✅ FIFO matching + apply-receipt + chiết khấu | 90% |
| **UC3** | Xử lý expense (phải chi) | ❌ Chưa có (data model + skill sẵn) | 10% |

Chung cả 3 UC:
- Actor chính: **Kế toán viên** (role=user) — nghiệp vụ hàng ngày
- Actor phụ: **Kế toán trưởng** (role=admin) — duyệt vượt ngưỡng
- Cả 3 dùng chung: OCR (RapidOCR), LLM extract (Claude Haiku via CLI), AR/AP ledger, audit log
- Governance: AI **suggest**, người **accept** — không auto-post

---

## UC1 — Xử lý hoá đơn bán/mua

### Đã có (production-ready trên cosmos)

- OCR + LLM extract 23 fields chuẩn TT78/2021
- AR context enrichment: lookup buyer_mst → customer + open_invoices + credit_limit + aging
- Risk flags 5 loại: `>500M requires L2`, `overdue_90d`, `would_breach_credit`, `credit_used_gte_80pct`, `has_overdue_amount`
- 3-way match: invoice → sales_order → delivery_note → contract_milestone
- Duplicate detection (E2): reject nếu trùng seller_mst + document_number
- Bút toán suggest N131 / C511 + C33311 tự động (LLM)

### Còn thiếu

| # | Item | Effort | Value |
|---|------|--------|-------|
| 1.1 | **Accept/Reject bút toán** cho invoice (hiện chỉ có cho receipt) | 2h | Complete workflow |
| 1.2 | **Post AP ledger** cho invoice mua (Nợ 156/641 / Có 331) — auto-insert vào bảng AP mới | 4h | Đối xứng với AR |
| 1.3 | **Route HITL queue** khi risk_flags có `requires_l2_approval` | 3h | Governance thật (spec BR-5) |
| 1.4 | **Chart of accounts editor** — cho phép admin custom TK theo công ty | 6h | Multi-tenant flexibility |
| 1.5 | **Line item accounting** — mỗi line có thể vào TK khác (VD ghế → 156, dịch vụ → 642) | 4h | Chuẩn xác hơn |
| 1.6 | **PDF/XML e-invoice parser** (TT78) — thay OCR ảnh bằng đọc XML trực tiếp | 8h | Accuracy 100% với HĐ điện tử |
| 1.7 | **Batch upload + bulk approve** | 3h | Tăng năng suất |

### Ưu tiên UC1

**P0 (2 tuần):** #1.1 Accept invoice + #1.3 HITL routing
**P1 (1 tháng):** #1.6 XML parser + #1.2 AP ledger
**P2 (sau):** #1.4, #1.5, #1.7

---

## UC2 — Xử lý phiếu thu nợ (đã hoàn thành 90%)

### Đã có (production-ready)

**7 bước phiếu thu**, đầy đủ end-to-end:

1. Upload → `/vouchers/upload?voucherType=receipt`
2. RapidOCR + LLM extract → payer_mst, amount, document_number, date
3. Alt-key parser đọc payer_mst / buyer_mst / customer_mst
4. AR context lookup → open_invoices của khách
5. **FIFO matching** — deterministic, không LLM: áp thu vào HĐ theo due-date oldest-first
6. Bút toán suggest N111/N112 / C131 (LLM)
7. **Apply-receipt** — insert credit rows vào `sim_ar_ledger`, update `sim_customers.current_ar`, chuyển voucher status=verified

Chiết khấu thanh toán sớm:
- Auto-detect shortfall ≤ 3% + không quá hạn
- Book Nợ 5211 / Có 131
- Hint hiển thị trong ReceiptMatchingPanel

### Còn thiếu

| # | Item | Effort | Value |
|---|------|--------|-------|
| 2.1 | **Chat upload** — drop ảnh phiếu thu vào cửa sổ chat thay vì trang riêng | 4h | UX yêu cầu của user |
| 2.2 | **Nguồn "sao kê ngân hàng"** — parse .xlsx/.csv sao kê, extract nhiều dòng thu/chi | 8h | Bulk processing |
| 2.3 | **Match tự động sao kê ↔ HĐ** — không cần từng phiếu thu | 6h | Nghiệp vụ thật của trưởng phòng |
| 2.4 | **Confidence-based auto-accept** — nếu match 100% amount + khách + không có discount → auto-verify | 3h | Giảm click cho HĐ nhỏ |
| 2.5 | **Unapply / rollback** — cho phép undo apply-receipt (kèm audit trail) | 4h | Sửa sai |
| 2.6 | **Receipt template printer** — sinh phiếu thu PDF từ data | 3h | Cho phiếu thu tự lập, không phải upload |

### Ưu tiên UC2

**P0:** #2.5 Unapply (đã có rủi ro user apply nhầm)
**P1:** #2.1 Chat upload + #2.2 Bank statement parser
**P2:** #2.3 Auto-match + #2.4 Auto-accept + #2.6 Printer

---

## UC3 — Xử lý expense (chi phí phải trả) — **NEW**

### Định nghĩa

Kế toán viên xử lý các chứng từ **chi phí công ty phải thanh toán**:
- Hoá đơn NCC (invoice_vat_in — đã có schema)
- Phiếu chi tiền mặt (payment)
- Uỷ nhiệm chi ngân hàng (bank_transfer)
- Đề nghị hoàn ứng của nhân viên (expense_claim — đã có sim_expense_claims)

Đối xứng với UC1+UC2 nhưng cho phía **AP (accounts payable)**.

### Flow 8 bước đề xuất

```
1. Upload chứng từ (ảnh/PDF/XML) → OCR + LLM extract
2. Detect loại: invoice_in / payment / bank_transfer / expense_claim
3. Lookup NCC theo seller_mst → sim_suppliers → get current_ap + payment_terms
4. Match với PO (nếu có) → 3-way match invoice ↔ PO ↔ GRN
5. Suggest bút toán:
   - invoice_in mua hàng: Nợ 156/152/641/642 + Nợ 1331 / Có 331
   - payment: Nợ 331/641 / Có 111/112
   - bank_transfer: Nợ 331/641 / Có 112 + phí chuyển tiền
   - expense_claim: Nợ 6417/6428 / Có 141 (tạm ứng) + Có 111/112 (hoàn tiền)
6. Risk check:
   - Expense > 30tr chưa CK? (vi phạm điều kiện khấu trừ VAT TT78)
   - Nhân viên vượt monthly_expense_budget?
   - Duplicate invoice từ NCC (chống bill trùng)?
   - Expense_claim > outstanding_advance?
7. Route:
   - < 5M → auto-approve
   - 5-100M → 1 kế toán duyệt
   - > 100M → HITL queue cần Kế toán trưởng
8. Apply-expense:
   - Insert row vào sim_ap_ledger (mới cần build) → tăng current_ap
   - Nếu payment/bank_transfer: giảm current_ap ngay
   - Update sim_employees.outstanding_advance nếu là hoàn ứng
   - Voucher status → verified
```

### Cần build (backend)

| # | Item | Effort | Notes |
|---|------|--------|-------|
| 3.1 | **Schema** `sim_ap_ledger` — đối xứng sim_ar_ledger | 1h | Table + indexes |
| 3.2 | **ISupplierRepository** với `FindByMst`, `GetPayablesAsync`, `ApplyPaymentAsync` | 4h | Đối xứng ICustomerRepository |
| 3.3 | **Endpoint** `GET /suppliers/*` + `POST /vouchers/{id}/apply-expense` | 3h | Đối xứng /customers + /apply-receipt |
| 3.4 | **BuildApContextAsync** trong VoucherExtractJobRunner cho invoice_in | 2h | Tính projected_payable + flags |
| 3.5 | **BuildExpenseClaimMatchAsync** — match hoàn ứng vs tạm ứng | 3h | Employee.outstanding_advance |
| 3.6 | **PO 3-way match** endpoint `GET /purchase-orders/by-invoice/{doc}` | 2h | Đã có sim_purchase_orders |
| 3.7 | **Risk rules** cho expense (>30M CK, budget breach, dup NCC) | 3h | Business logic |

### Cần build (frontend)

| # | Item | Effort | Notes |
|---|------|--------|-------|
| 3.8 | **`/suppliers` list page** — mirror `/customers` | 3h | AP aging bảng |
| 3.9 | **AP Context Panel** trong voucher detail — đối xứng AR panel | 2h | current_ap, credit terms, overdue |
| 3.10 | **Expense Claim Panel** — hiện tạm ứng, khoản chi, hoàn tiền | 3h | Cho voucher_type=expense_claim |
| 3.11 | **Accept-expense button** → gọi `/apply-expense` | 2h | Đối xứng Accept receipt |
| 3.12 | **`/expenses` dashboard** — pending approval, tổng chi tháng, top NCC | 4h | Trưởng phòng dashboard |

### Skill LLM (cần enhance)

- Enhance skill `voucher-invoice-vat` để phân biệt in vs out (dựa seller_mst = my_tenant_mst?)
- Skill mới `voucher-expense-claim` (đã có schema trong `voucher-universal-extractor`)
- Skill `voucher-bank-transfer` — chú ý xử lý tỷ giá ngoại tệ (EUR/USD)

### Test data (đã có sẵn simulator)

`sim_expense_claims` đã có 5 mẫu:
- HC-2026-08-023 Trần Văn Đức công tác HN 11.35M (approved)
- HC-2026-08-019 Vũ Thị Hương hội chợ HN 30.3M (approved)
- HC-2026-08-031 Bùi Minh Khoa Đồng Nai 2.95M (submitted)
- HC-2026-08-042 Đinh Thị Mai FB Ads 33.5M (submitted)
- HC-2026-07-089 Trần Văn Đức Đà Nẵng 10.79M (paid)

`sim_purchase_orders` đã có 6 PO:
- PO-2026-0032 KaVo Đức 50k EUR ghế nha khoa (received, match invoice KAV-2026-0812)
- PO-2026-0055 NCC vật tư VN 85M (matched invoice 00005678)
- ...

### Ưu tiên UC3

**Sprint 1 (1 tuần):** Backend core — #3.1, #3.2, #3.3, #3.4 → có thể extract + suggest bút toán cho invoice_in
**Sprint 2 (1 tuần):** UI hoàn chỉnh — #3.8, #3.9, #3.11
**Sprint 3 (1 tuần):** Expense claim đặc thù — #3.5, #3.10
**Sprint 4 (1 tuần):** PO matching + risk rules — #3.6, #3.7, #3.12

---

## Kiến trúc chung 3 UC

```
                        ┌────────────────────────────┐
                        │   Master Data (simulator)   │
                        │   Customer + Supplier + Employee │
                        │   Sales Order + Purchase Order   │
                        │   Contract + Milestone            │
                        └──────────┬─────────────────┘
                                   │ lookup
┌──────────┐  ┌──────────┐  ┌──────▼──────────┐  ┌──────────────┐
│ Upload   │→ │ OCR      │→ │ Agent Extract   │→ │ Business     │
│ (image/  │  │ (RapidOCR│  │ + AR/AP context │  │ Rules Engine │
│  PDF)    │  │  ONNX)   │  │ + Matching      │  │ (risk flags) │
└──────────┘  └──────────┘  └─────────────────┘  └──────┬───────┘
                                                        │
                    ┌───────────────────────────────────┼──────────────────┐
                    ▼                                   ▼                  ▼
              ┌──────────┐                     ┌────────────┐      ┌──────────────┐
              │ UI Review│                     │ HITL queue │      │ Auto-apply   │
              │ (accept/ │                     │ (>100M or  │      │ (< threshold │
              │  reject) │                     │  L2 flags) │      │  + safe)     │
              └────┬─────┘                     └─────┬──────┘      └──────┬───────┘
                   │                                 │                    │
                   └────────────────┬────────────────┴────────────────────┘
                                    ▼
                          ┌─────────────────────┐
                          │ AR/AP Ledger Update │
                          │ + Journal Entry     │
                          │ + Audit Log         │
                          └─────────────────────┘
```

### Data model cần bổ sung (Phase 2)

```sql
-- UC3 — AP ledger (mirror sim_ar_ledger)
CREATE TABLE sim_ap_ledger (
    id UUID PK,
    tenant_id UUID FK tenants,
    supplier_id UUID FK sim_suppliers,
    document_date DATE,
    document_number TEXT,
    document_type TEXT,           -- invoice / payment / bank_transfer / expense_claim
    debit NUMERIC(18,2),          -- tăng phải trả (ta nợ NCC)
    credit NUMERIC(18,2),         -- giảm phải trả (thanh toán)
    balance_after NUMERIC(18,2),
    due_date DATE,
    reference_note TEXT           -- 'voucher:{id}|apply:{ap_doc}'
);

-- UC3 — HITL approval queue (cross-cutting)
CREATE TABLE approval_queue (
    id UUID PK,
    tenant_id UUID FK,
    voucher_id UUID FK vouchers,
    submitted_by UUID FK users,
    approval_level INT,           -- 1=kế toán viên, 2=trưởng phòng
    threshold_amount NUMERIC(18,2),
    trigger_reason TEXT[],        -- ['>100M', 'exceed_budget', 'duplicate_ncc']
    status TEXT,                  -- pending / approved / rejected
    reviewed_by UUID FK users,
    reviewed_at TIMESTAMPTZ,
    review_note TEXT
);
```

### Endpoint chung mới cần build

| Endpoint | Method | Ai gọi | Chức năng |
|----------|--------|--------|-----------|
| `/vouchers/{id}/apply-invoice` | POST | Kế toán viên | Confirm HĐ bán → tăng AR (đối xứng apply-receipt) |
| `/vouchers/{id}/apply-expense` | POST | Kế toán viên | Confirm chi phí → tăng AP hoặc giảm AP nếu payment |
| `/vouchers/{id}/submit-approval` | POST | Kế toán viên | Đẩy vào HITL queue nếu vượt ngưỡng |
| `/approval-queue/` | GET | Trưởng phòng | List pending approvals |
| `/approval-queue/{id}/decide` | POST | Trưởng phòng | Approve/reject với ghi chú |
| `/suppliers/` | GET | Kế toán | List NCC + current_ap |
| `/suppliers/by-mst/{mst}` | GET | Bg extract job | Lookup NCC khi extract invoice_in |
| `/suppliers/{id}/payables` | GET | Kế toán | Chi tiết công nợ NCC |
| `/purchase-orders/by-invoice/{doc}` | GET | Bg extract job | 3-way match cho AP |

---

## Cross-cutting concerns

### Governance & Audit

- Mọi apply-receipt / apply-expense phải ghi audit log: who, when, before_balance, after_balance
- HITL queue với approval_level (kế toán viên 5-100M, trưởng phòng >100M)
- Idempotency guard (đã có ở apply-receipt) — copy pattern

### Notification

- Email cho trưởng phòng khi có approval pending
- Slack/LINE webhook cho voucher >500M
- In-app notification khi voucher extract xong (đã có infra)

### Reporting (Phase 3)

- Dashboard `/reports/ar-aging` — bảng aging chuẩn cuối tháng
- Dashboard `/reports/ap-aging` — công nợ phải trả
- Report `/reports/journal-book` — sổ nhật ký theo TT200
- Export Excel Misa-ready cho tất cả các module

### Test data expansion

- Thêm 20-30 hoá đơn/tháng vào sim_ar_ledger cho realistic aging
- Thêm 15-20 expense_claim với đủ pattern (approved/rejected/paid)
- Thêm 10 PO với các trạng thái received/partial/pending

---

## Roadmap tổng — 8 tuần

| Tuần | UC1 | UC2 | UC3 |
|:---:|-----|-----|-----|
| 1 | Accept invoice + HITL routing (P0) | Unapply/rollback | Backend core (schema + repo) |
| 2 | | | Backend endpoint + agent enrichment |
| 3 | | Chat upload dropzone | UI supplier list + AP panel |
| 4 | XML e-invoice parser | Bank statement parser | Accept-expense button |
| 5 | AP ledger auto-insert | Auto-match statement ↔ HĐ | Expense claim panel |
| 6 | | Auto-accept confidence-based | PO 3-way match |
| 7 | Chart of accounts editor | Receipt PDF printer | Risk rules engine |
| 8 | Line item accounting | | Dashboard + report |

**Cross-cutting parallel:**
- Notification (email + in-app) — Tuần 4-5
- Audit log dashboard — Tuần 6
- Excel export (Misa-ready) — Tuần 7-8

## Ưu tiên đề xuất — chọn 1 trong 3 path

### Path A — "Deep on 1 module" (recommended for pilot)
Focus 4 tuần vào **UC3 completeness** → có full stack AR + AP → sell được cho khách hàng thật.

### Path B — "Wide coverage"
Song song 3 UC ở mức MVP → demo được nhiều feature, nhưng không cái nào production-ready.

### Path C — "Governance-first"
Skip feature mới, tập trung HITL + audit + notification + Excel export cho UC1+UC2 → khách enterprise thích, nhưng feature-set nhỏ hơn đối thủ.

**Em đề xuất Path A** — UC3 là gap lớn nhất, làm xong sẽ có sản phẩm hoàn chỉnh AR + AP đúng chuẩn kế toán VN.
