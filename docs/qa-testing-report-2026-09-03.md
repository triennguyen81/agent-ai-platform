# QA Testing Report — Sprint 1-4.5 UC3 Expense/AP + Bug Fixes

> **Sprint:** UC3 Expense/AP end-to-end (Sprint 1 → 4.5) + Merge multi-tenant refactor + Bug fixes
> **Deploy date:** 2026-09-01 → 2026-09-03
> **Environment:** Production `https://app.cattuong.ai` (Contabo VPS cosmos)
> **Branches shipped:** `feature/uc3-ap-merged` merged via PR #1 → `master`
> **Tester audience:** QA team

---

## 1. Tổng quan — Đã ship gì

### 8 commits chính trên master

| # | Commit | Nội dung |
|---|---|---|
| 1 | `292a573` | Sprint 1 — Backend AP core (sim_ap_ledger, Supplier + Employee repos, extract enrichment) |
| 2 | `ea19a7d` | Sprint 2 — UI `/suppliers` page + AP panels + AcceptExpenseButton |
| 3 | `a58096d` | Sprint 3 — Expense per-category breakdown + 4 demo templates PNG |
| 4 | `7185f69` | Sprint 4 — PO 3-way match + risk config + gap UC1/UC2 + `/expenses` dashboard |
| 5 | `4b56710` | Sprint 4.5 — Artifact Library + 3-step training walkthrough |
| 6 | `5b01d40` | Merge origin/master multi-tenant refactor vào UC3 branch |
| 7 | `6345e56` | Fix bug 403 "An assigned agent is required" (auto-fallback agent_id) |
| 8 | `c288c3c` | Docs AccAgent spec v0.1 (reference) |

### Coverage — 6 usecase types

Hệ thống giờ xử lý được tất cả 6 loại chứng từ kế toán:

| voucher_type | Chiều | UC |
|---|---|---|
| `invoice_vat_out` | AR (bán) | UC1 (đã có từ trước) |
| `receipt` | AR (thu) | UC2 (đã có từ trước) |
| `invoice_vat_in` | AP (mua) | UC3 mới ⭐ |
| `payment` | AP (chi TM) | UC3 mới ⭐ |
| `bank_transfer` | AP (UNC) | UC3 mới ⭐ |
| `expense_claim` | AP (hoàn ứng NV) | UC3 mới ⭐ |

---

## 2. Test environment

| Item | Value |
|---|---|
| Production URL | https://app.cattuong.ai |
| Backend gateway | https://app.cattuong.ai/api (proxy qua Next.js) |
| Database | PostgreSQL 16 (aiq-postgres container) |
| OCR engine | RapidOCR (ONNX runtime, fallback từ PaddleOCR) |
| LLM extract | Claude via subscription CLI |
| Tenant test | `vietdang` (CTCP Thiết bị Nha khoa Việt Đăng) |

### Docker containers cần healthy

```
aiq-web       (Next.js 15 frontend)
aiq-gateway   (ASP.NET Core 9)
aiq-agent     (FastAPI Python 3.12)
aiq-postgres  (PostgreSQL 16 + pgvector)
aiq-redis
aiq-minio     (S3-compatible object store)
```

---

## 3. Test accounts (tenant Việt Đăng)

| Email | Password | Role | Dùng để test gì |
|---|---|---|---|
| `admin@vietdang.vn` | `VD@2026` | admin | Full nghiệp vụ + workspace + admin |
| `ketoan1@vietdang.vn` | `VD@2026` | user (kế toán viên) | Daily driver — upload + extract + accept |
| `ketoan2@vietdang.vn` | `VD@2026` | user (kế toán viên) | Test collab (2 người cùng tenant) |
| `giamdoc@vietdang.vn` | `VD@2026` | user | Xem báo cáo |
| `tuong247@gmail.com` | (anh giữ) | super_admin | Cross-tenant admin |

**Note:** Kế toán viên (`user` role) chỉ được assign agent `vd-accounting` trong tenant Việt Đăng — hạn chế cross-tenant.

---

## 4. Test cases theo Sprint

### Sprint 1 — Backend AP core (verify qua API)

**Endpoint mới:**

| Method | Path | Auth | Expected |
|---|---|---|---|
| GET | `/suppliers/` | user+ | 200 + 6 NCC (KaVo, Sirona, Morita + 3 VN) |
| GET | `/suppliers/by-mst/{mst}` | user+ | 200 + supplier record hoặc 404 |
| GET | `/suppliers/{id}` | user+ | 200 + full supplier detail |
| GET | `/suppliers/{id}/payables` | user+ | 200 + aging buckets + open payables |
| GET | `/purchase-orders/by-invoice/{docNumber}` | user+ | 200 + PO+GRN match hoặc null |

**Test qua curl (login trước để lấy TOKEN):**

```bash
curl -H "Authorization: Bearer $TOKEN" https://app.cattuong.ai/api/gateway/suppliers/
# Expected: JSON { "items": [...6 items...], "total": 6 }
```

**Test qua UI:**
- Login `ketoan1@vietdang.vn` → nav "Nghiệp vụ ▼" → click "Nhà cung cấp"
- Verify: 6 NCC hiển thị, có 3 summary card (Tổng phải trả / NCC ≥ 500M / NCC nhập khẩu)
- Filter theo country (VN/DE/JP/US) — verify list filter đúng
- Search "KaVo" — verify chỉ NCC-DE-001 hiện

### Sprint 2 — UI Suppliers + AP panels + Accept expense

**Test AP context panel (upload invoice_vat_in):**

1. Login `ketoan1@vietdang.vn`
2. Nav → "Chứng từ" → "Thêm chứng từ" → chọn voucher type `invoice_vat_in`
3. Upload `docs/demo-templates/07-hoa-don-ncc-kavo.png` (HĐ KaVo 1.375 tỷ)
4. Chờ ~30-60s pipeline OCR + extract
5. Auto-refresh trang → verify:
   - **AR context panel:** ✗ không hiện (đúng, invoice_vat_in là AP)
   - **AP context panel:** ✓ hiện với supplier KaVo (NCC-DE-001), country DE, currency EUR
   - Current balance 331 = 750,000,000 → Projected = 2,125,000,000 (+1.375 tỷ)
   - Aging buckets có over90 = 750M (KaVo đã overdue seed)
   - Risk flags đỏ: `expense_over_500m_requires_l2_approval` + `has_overdue_90d`
6. Click "Chấp nhận HĐ · Ghi công nợ 331" → verify success card:
   - Công nợ trước: 750M
   - Công nợ sau: 2,125M
   - Ledger rows: 1
7. Refresh trang → button không còn hiện + banner "✓ Đã ghi HĐ NCC vào sổ AP"
8. **Idempotency test:** upload lại cùng file → verify voucher bị reject vì duplicate `document_number`

**Test payment matching (upload bank_transfer):**

1. Sau khi đã book HĐ KaVo (step 6 trên), upload `docs/demo-templates/09-uy-nhiem-chi-vn.png` (UNC 828M → KaVo)
2. Chờ extract xong → verify:
   - **AP context panel:** hiện với KaVo, current_ap = 2,125M
   - **Payment matching panel:** hiện với FIFO oldest — apply 828M vào HĐ INV-KAV-2025-11-450 (opening balance) hoặc HĐ mới upload ở step 6
3. Click "Chấp nhận chi · Áp vào công nợ NCC" → verify:
   - Current AP giảm đúng 828M
   - Ledger row payment insert vào sim_ap_ledger với `debit=828M`

### Sprint 3 — Expense claim per-category + TT96 warning

1. Login `ketoan1@vietdang.vn`
2. Upload `docs/demo-templates/08-hoan-ung-cong-tac.png` (NV-0007 Trần Văn Đức, 11.35M)
3. Chờ extract → verify **ExpenseClaimPanel** hiện:
   - Employee: NV-0007, Kinh doanh dept
   - Total claim: 11,350,000
   - Advance covered: 5,000,000 (min với outstanding)
   - Remaining to reimburse: 6,350,000
   - Progress bar YTD budget
4. **Items table** hiển thị 5 khoản:
   - Vé máy bay 2,850,000 → TK 6417 (Kinh doanh dept)
   - Khách sạn 3,600,000 → TK 6417
   - Ăn uống 1,350,000 → TK 6417
   - Đi lại 850,000 → TK 6417
   - Tiếp khách 2,700,000 → **TK 6428** với badge màu amber (TT96 flag)
5. **Warning TT96** box đỏ hiển thị nhắc 15% cap tiếp khách
6. Bút toán tổng hợp:
   - Nợ 6417 (Kinh doanh) 8,650,000
   - Nợ 6428 (tiếp khách) 2,700,000
   - Có 141 5,000,000
   - Có 111 6,350,000
7. Click "Chấp nhận hoàn ứng · Ghi TK 141":
   - `sim_employees.outstanding_advance` giảm từ 5M → 0
   - `sim_employees.ytd_expense` tăng 11.35M

### Sprint 4 — PO 3-way match + risk + gap UC1/UC2 + /expenses dashboard

**Test PO 3-way match (invoice_vat_in với PO khớp):**

1. Upload `07-hoa-don-ncc-kavo.png` (KAV-2026-0812) — file này khớp PO-2026-0032 + GRN-2026-078
2. Verify **PoMatchPanel** hiện với 2 card:
   - **PO card:** PO-2026-0032, supplier NCC-DE-001, status "received", subtotal 1.375 tỷ, total 1.5125 tỷ, incoterm CIF, matched invoice = KAV-2026-0812
   - **GRN card:** GRN-2026-078, ngày 2026-07-25, warehouse "Kho HCM", status "received"
3. Badge quality: "Khớp đầy đủ" (xanh)

**Test risk flag `expense_over_30m_cash_payment` (TT78):**

1. Upload `docs/demo-templates/10-phieu-chi-tien-mat.png` (phiếu chi TM 35M cho NCC-VN-002)
2. Voucher type: `payment`
3. Verify **AP context panel** có risk flag ĐỎ: `Chi TM > 30M — không khấu trừ VAT (TT78 điều 6)`
4. Bút toán suggest KHÔNG có TK 1331 (VAT đầu vào bỏ)

**Test gap UC1 Accept invoice (invoice_vat_out):**

1. Login `ketoan1@vietdang.vn`
2. Upload `docs/demo-templates/02-hoa-don-vat-bv-rhm-650tr.png` (HĐ bán BV RHM 651M)
3. Sau extract → **ArContextPanel** hiện với BV RHM, current_ar 1.25B → projected 1.9B
4. Section mới "Ghi HĐ vào sổ AR" với button **"Chấp nhận HĐ · Ghi công nợ khách"**
5. Click button → verify:
   - Success card: prev 1.25B → new 1.9B, ledger rows +1
   - `sim_customers.current_ar` update

**Test gap UC2 Unapply receipt (rollback):**

1. Với voucher `receipt` đã ở status `verified` (VD case 04 Nha Xinh 183.75M)
2. Section mới "Rollback (nếu áp thu sai)" xuất hiện dưới ReceiptMatchingPanel
3. Click "Huỷ áp thu" → confirmation dialog đỏ hiện
4. Click "Xác nhận huỷ" → verify:
   - Success card amber: prev/new balance + rows deleted
   - Voucher status rollback về `extracted`
   - Có thể apply lại

**Test /expenses dashboard:**

1. Nav "Nghiệp vụ ▼" → "Chi phí"
2. Verify 4 summary cards:
   - Tổng phải trả NCC (sum current_ap)
   - NCC ≥ 500M (count) — hiện đỏ nếu ≥ 1
   - NCC nhập khẩu (count NCC country != VN)
   - Vouchers chưa verify (link `/vouchers?status=extracted`)
3. Section "Top 5 NCC nợ nhiều nhất" — hiển thị KaVo dẫn đầu

### Sprint 4.5 — Artifact Library / Training center

**Test list page:**

1. Nav "Nghiệp vụ ▼" → "Chứng từ mẫu"
2. Verify page hiển thị:
   - Title "Chứng từ mẫu · Training center"
   - 3 summary cards (10 mẫu / 3 bước / ~30 phút)
   - Group 6 categories (HĐ bán / HĐ mua / Phiếu thu / UNC / Phiếu chi TM / Hoàn ứng NV)
   - Grid card với thumbnail + title + amount + risk badge
3. Hover thumbnail → overlay "Bắt đầu học" xuất hiện

**Test walkthrough (mẫu 01 — HĐ bán Nha Xinh 30tr):**

1. Click card mẫu 01 → mở `/mau-chung-tu/01-hoa-don-vat-nha-xinh-30tr`
2. Layout 2-column: image sticky trái, walkthrough phải
3. **Bước 1 — Nhận diện:** 4 radio options, click "Hoá đơn bán (đầu ra)" → reveal correct + explanation Vietnamese
4. Click "Bước tiếp" → **Bước 2 — Fields:** table 6 fields với hint mỗi field, risk flags list bên dưới
5. Click "Bước tiếp" → **Bước 3 — Bút toán:** table Nợ đỏ / Có xanh, quiz "Tại sao dùng TK 131?" → chọn đáp án → reveal explanation, "Bài học chốt" box vàng
6. Click "Hoàn thành" → completion card xanh + 3 buttons (Về danh sách / Học mẫu tiếp / Upload chứng từ thực tế)
7. Click "Học mẫu tiếp" → auto navigate sang mẫu 02

**Test tất cả 10 mẫu:** verify mỗi mẫu có đủ 3 bước + đáp án + explanation không lỗi.

### Bug 403 fix — Auto-fallback agent_id

**Trước fix:** `/accounting/tax-calc` khi user (`ketoan1`) bấm "Tính" → HTTP 403 "An assigned agent is required"

**Sau fix:**

1. Login `ketoan1@vietdang.vn`
2. Nav "Nghiệp vụ ▼" → "Kế toán" → tab "Tính thuế"
3. Chọn tab TNDN, nhập:
   - Doanh thu: 50,000,000,000 (50 tỷ)
   - Chi phí được trừ: 38,000,000,000 (38 tỷ)
   - Lỗ kết chuyển: 0
   - Thuế suất: 20%
4. Click "Tính"
5. **Expected (sau fix):** Kết quả markdown hiển thị breakdown TNDN chi tiết, KHÔNG có lỗi 403
6. **Verify khác:** tab VAT (GTGT) + TNCN cũng tính được không lỗi

### Colleague's multi-tenant admin (merged, cần smoke test)

Với account `super_admin` (`tuong247@gmail.com`):

1. Nav "Quản trị ▼" → verify 7 items xuất hiện:
   - Agent Builder
   - Tenants
   - Users
   - Tools
   - Skills
   - Audit
   - Health
2. Vào từng page — verify không crash, load được data

Với account `admin` tenant (`admin@vietdang.vn`):

1. Nav "Quản trị ▼" → verify 5 items:
   - Users (tenant users)
   - Tenant Agents
   - Knowledge
   - HITL / Reviews
   - Audit / Usage

---

## 5. Test data available (seed on cosmos)

### Bảng master

| Bảng | Rows | Highlights |
|---|---|---|
| `sim_customers` | 12 | 8 PK + 2 BV + 2 đại lý; Nha Xinh 187.5M nợ, BV RHM 1.25B nợ |
| `sim_suppliers` | 6 | KaVo 750M nợ (overdue), Sirona 420M nợ (overdue), 3 NCC VN trong hạn, Morita 0 |
| `sim_employees` | 12 | NV-0007 Trần Văn Đức có outstanding_advance 5M |
| `sim_purchase_orders` | 6 | PO-2026-0032 KaVo 1.375 tỷ khớp GRN-2026-078 |
| `sim_goods_received_notes` | 6 | GRN cho 6 PO |
| `sim_ap_ledger` | 5 | Opening balances khớp current_ap |
| `sim_expense_claims` | 5 | 2 approved, 2 submitted, 1 paid |
| `sim_bank_accounts` | 4 | VCB VNĐ, VCB EUR, VCB USD, ACB VNĐ |
| `sim_bank_transactions` | 14 | Mix receipt + payment history |
| `sim_contracts` | 4 | BV RHM 3-giai đoạn, Ngọc Anh distribution |
| `sim_contract_milestones` | 6 | Milestones cho BV RHM contract |
| `sim_delivery_notes` | 11 | Cho AR side 3-way match |
| `sim_sales_orders` | 12 | AR side SO |
| `sim_products` | 21 | Ghế nha khoa, vật tư |

### 10 demo template files có sẵn upload test

Path: `docs/demo-templates/`

| # | File | Voucher type | Amount | Case highlight |
|---|---|---|---|---|
| 01 | `01-hoa-don-vat-nha-xinh-30tr.png` | invoice_vat_out | 30M | AR bán chịu cơ bản |
| 02 | `02-hoa-don-vat-bv-rhm-650tr.png` | invoice_vat_out | 651M | L2 approval trigger + VAT 5% TB y tế |
| 03 | `03-hoa-don-vat-dong-a-15tr.png` | invoice_vat_out | 15M | customer_not_found fallback |
| 04 | `04-phieu-thu-nha-xinh.png` | receipt | 183.75M | FIFO + auto discount 3.75M |
| 05 | `05-uy-nhiem-chi-kavo.png` | bank_transfer | 412.5M | T/T deposit 30% |
| 06 | `06-de-nghi-hoan-ung.png` | expense_claim | 11.35M | Hoàn ứng NV-0007 (4 items) |
| 07 | `07-hoa-don-ncc-kavo.png` | invoice_vat_in | 1.375B | Import EUR + PO match |
| 08 | `08-hoan-ung-cong-tac.png` | expense_claim | 11.35M | 5 items có TT96 tiếp khách |
| 09 | `09-uy-nhiem-chi-vn.png` | bank_transfer | 828M | Payment matching FIFO |
| 10 | `10-phieu-chi-tien-mat.png` | payment | 35M | TT78 flag TM > 30M |

QA download từ repo hoặc dùng ngay trong `docs/demo-templates/` folder trên máy dev.

---

## 6. Known issues + limitations

### Đã biết (không phải bug — design decision)

- **Accept button write vào sim_* ledger nội bộ** — không tự động export sang MISA/Fast/Bravo. Đây là design intent (AIQ Accounting standalone), khác với AccAgent spec (chỉ đề xuất, không ghi)
- **Kế toán viên (role=user) collab** — hiện IVoucherRepository query filter `AND user_id = @userId` sau merge PR #1. Nghĩa là kế toán A upload → kế toán B **không thấy** trong list voucher. Đây là regression từ merge, cần fix nếu QA yêu cầu collab
- **Sim data**: Tất cả data KH/NCC/HĐ/PO là simulator, không phải data thật của Việt Đăng. Khi triển khai thật cần MCP adapter kéo từ ERP
- **Payment matching AP KHÔNG auto-detect discount** — khác AR side. Đây là design intent (chiết khấu NCC negotiate manual)

### Bug có thể còn (chưa verify)

- **Kế toán viên không có agent assigned** → tính năng dùng `/agent/run` (tax-calc, journal, invoice paste) sẽ trả 400 "Tài khoản chưa được assign trợ lý". Fix I3: chạy SQL assign default agent cho user
- **Duplicate document_number cross-tenant** — chưa test kỹ. Kế toán viên tenant A upload HĐ có cùng số với tenant B — hành vi hiện chưa rõ
- **Upload PDF thay vì PNG** — theo code có support content-type `application/pdf` nhưng chưa test kỹ với PDF nhiều trang

### Environment-specific

- Web nếu bị 404 sau khi rebuild → clear browser cache Ctrl+Shift+R
- Login redirect loop nếu JWT expired → xoá cookie `auth_token` + login lại
- Upload chậm ≥ 60s = OCR timeout, retry được. Nếu > 3 lần fail → check `docker logs aiq-agent` xem RapidOCR có crash không

---

## 7. Regression scenarios (UC1 + UC2 phải vẫn work)

Sau khi ship UC3 + merge multi-tenant, các flow cũ phải vẫn hoạt động:

### UC1 — HĐ bán regression

1. Login `ketoan1@vietdang.vn`
2. Upload `02-hoa-don-vat-bv-rhm-650tr.png` (voucher_type=invoice_vat_out)
3. Verify **ArContextPanel** hiện với BV RHM data
4. Verify **MatchContextPanel** (3-way SO/DN/Contract) nếu có SO khớp
5. Verify journal entries suggest đúng (Nợ 131 / Có 511 + 3331)
6. Click "Chấp nhận HĐ · Ghi công nợ khách" (Sprint 4 gap UC1 mới)
7. Verify `sim_customers.current_ar` tăng

### UC2 — Phiếu thu regression

1. Login `ketoan1@vietdang.vn`
2. Upload `04-phieu-thu-nha-xinh.png` (voucher_type=receipt)
3. Verify **ReceiptMatchingPanel** hiện với FIFO oldest apply + discount 3.75M
4. Click "Chấp nhận thu · Cập nhật công nợ"
5. Verify `sim_customers.current_ar` giảm đúng
6. Click "Huỷ áp thu" (Sprint 4 gap UC2 mới) → verify rollback

---

## 8. Cách report bug

**Template khi log bug:**

```
Title: [module] short description
Env: production (app.cattuong.ai)
Account: [email dùng để test]
Steps to reproduce:
1. ...
2. ...
3. ...
Expected: ...
Actual: ...
Screenshot: [attach]
Browser console errors: [paste]
Network tab (nếu API lỗi): status code + response body
```

**Channels:**
- Zalo direct anh Tường
- Email: tuong247@gmail.com
- Github issue: https://github.com/tuongna247/agent_claude_sdk/issues (nếu QA có access)

### Priority levels

| P | Định nghĩa | Fix SLA |
|---|---|---|
| P0 | System down, không login được, data loss | 2 giờ |
| P1 | Feature core bị block, không có workaround (VD Accept button không work) | 1 ngày làm việc |
| P2 | Feature phụ bị lỗi, có workaround | 3 ngày |
| P3 | UI polish, wording, edge case | Sprint sau |

---

## 9. Reference — Docs khác

- `docs/accagent-spec-v0.1-dac-ta-ky-thuat.md` — Spec kỹ thuật AccAgent v0.1 (chưa implement, reference)
- `docs/accagent-spec-v0.1-quy-trinh-nghiep-vu.md` — Quy trình nghiệp vụ AccAgent v0.1
- `docs/plan-agent-tro-ly-ke-toan.md` — Plan session cũ (UC1/UC2/UC3 original design)
- `use-case-spec-xu-ly-chung-tu-ke-toan.md` — Use case spec chi tiết
- `docs/demo-templates/` — 10 file PNG mẫu để upload test

---

## 10. Session context — 2026-08-28 → 2026-09-03

Tổng 6 ngày ship intensive:

- Ngày 1-2: Sprint 1 backend AP core
- Ngày 3: Sprint 2 UI + Accept expense
- Ngày 4: Sprint 3 per-category + templates
- Ngày 4: Sprint 4 PO match + gaps
- Ngày 5: Sprint 4.5 Artifact Library
- Ngày 5: Merge colleague's PR (10 conflicts resolved)
- Ngày 5-6: Deploy cosmos + fix bug 403 + docs

Solo dev + continuous deploy strategy. Không có test tự động E2E — QA cần manual test 6 flow chính (5 sprint + regression UC1/UC2).

---

*Report v1.0 · 2026-09-03 · Tuong Nguyenanh. Câu hỏi trực tiếp anh Tường qua Zalo/email.*
