# Plan: Trợ lý Kế toán — Xử lý chứng từ qua hình ảnh (Voucher Processing)

## Context

Đối tác mới yêu cầu module **Trợ lý Xử lý Chứng từ Kế toán** với flow anh Toàn đã vẽ:

```
User upload chứng từ (ảnh/PDF)
        ↓
IDP: OCR + rút trích data cần thiết
        ↓
Agent xử lý (validate + suggest bút toán)
        ↓
MCP kết nối data ngoài (TCT verify, MST lookup, journal suggest)
        ↓
Xuất Excel/CSV để user import ERP (Misa/Fast/Bravo)
```

**User đã quyết định** (qua AskUserQuestion):

1. **IDP engine:** Tự build PaddleOCR + Claude Haiku extract (reuse infra sẵn có, $0/chứng từ, swap SaaS sau nếu cần)
2. **Loại chứng từ:** Full 4 loại — hoá đơn VAT điện tử, phiếu thu/chi, uỷ nhiệm chi, sao kê ngân hàng
3. **Post-extract action:** Suggest bút toán N/C theo TT200 + xuất Excel (KHÔNG auto-post ERP)
4. **TCT verify:** Optional — user tick nếu muốn

**Tin tốt:** Hệ thống AIQ đã có ~75% infrastructure. Contract module (`contract-extractor` + `ContractEndpoints` + Hangfire `OcrJobRunner`) là template hoàn hảo — pattern song song 100%.

---

## Mapping: AIQ đã có gì

| Yêu cầu | Đã có sẵn | Vị trí |
|---|---|---|
| OCR PDF → text VN + diacritic correct | ✅ **Đầy đủ** | `agent/src/aiq_agent/ocr/` (PaddleOCR + DiacriticCorrector + pgvector validation) |
| Endpoint OCR | ✅ `POST /ocr/pdf` | `agent/src/aiq_agent/api/ocr_routes.py:67` (chỉ PDF, cần bù image) |
| PDF→PNG render | ✅ pypdfium2 200 DPI | `agent/src/aiq_agent/ocr/pdf.py:14` |
| Hangfire job pattern (idempotent, retry) | ✅ | `gateway/src/Aiq.Gateway/Jobs/OcrJobRunner.cs` |
| File staging | ✅ Volume `/var/aiq/uploads` | `docker-compose.yml:86-94` |
| Domain extraction skill pattern | ✅ | `agent/skills/contract-extractor/SKILL.md` |
| MCP tool factory pattern | ✅ (in-process, không standalone) | `agent/src/aiq_agent/agentic/contract_tools.py` |
| Gateway CRUD pattern | ✅ | `gateway/src/Aiq.Gateway/Contracts/ContractRecords.cs` + `ContractEndpoints.cs` |
| Tool registry governance (risk_tier) | ✅ G2 layer | `infra/postgres/init/13_governance_tools.sql:80` |
| Accounting MCP tools | ✅ 7 tools | `agent/src/aiq_agent/agentic/accounting_tools.py` (calc_vat, lookup_account TT200, journal_validator, filing_deadline...) |
| Accounting skills | ✅ 3 skills | `agent/skills/accounting-vietnam/` + `journal-entry-formatter/` + `tax-calculation/` |
| Web upload PDF page | ✅ | `web/src/app/(app)/contracts/upload/page.tsx` |
| Save-to-KB modal reuse | ✅ | `web/src/components/save-to-kb-button.tsx` |

### Còn thiếu (cần build)

| Hạng mục | Effort | Lý do |
|---|---|---|
| OCR endpoint accept `image/*` (JPEG/PNG) | S | `/ocr/pdf` chỉ nhận `.pdf` — cần `/ocr/image` hoặc convert image→PDF via Pillow trước feed pipeline |
| Schema: `vouchers` + `voucher_line_items` + `voucher_journal_suggestions` | S | Notes/contracts không có invoice_number, MST, tax_breakdown, line items |
| 4 skills extract (1 per voucher type) | M | Pattern copy từ `contract-extractor`, mỗi loại prompt riêng cho format field khác nhau |
| Gateway CRUD `/vouchers/*` | M | Pattern copy từ `ContractEndpoints.cs` |
| Hangfire `VoucherOcrJobRunner` | S | Copy từ `OcrJobRunner`, khác pipeline call |
| 5 MCP tools mới | M | `extract_voucher_fields`, `save_voucher`, `verify_einvoice_tct`, `lookup_mst`, `suggest_journal_entry`, `export_to_excel` |
| 3 web pages | M | `/vouchers` (list + filter) + `/vouchers/upload` (image dropzone) + `/vouchers/[id]` (detail + bút toán suggest + Excel export) |

---

## Kiến trúc kỹ thuật (aligned với `kientruc.png`)

```
┌─────────────────────────────────────────────────────────────┐
│  Web (Next.js)                                              │
│  /vouchers/upload → dropzone (jpg/png/pdf, multi-file)     │
└──────────────────────┬──────────────────────────────────────┘
                       │ POST /vouchers/upload (multipart)
                       ▼
┌─────────────────────────────────────────────────────────────┐
│  Gateway (ASP.NET)                                          │
│  VoucherEndpoints.UploadAsync                               │
│  → stage file → enqueue Hangfire VoucherOcrJobRunner       │
│  → return { voucherId, status: 'processing' }              │
└──────────────────────┬──────────────────────────────────────┘
                       │ Hangfire background
                       ▼
┌─────────────────────────────────────────────────────────────┐
│  VoucherOcrJobRunner (C# Hangfire)                          │
│  1. Convert image→PDF (Magick.NET) nếu là JPG/PNG          │
│  2. Call Agent: POST /ocr/pdf → raw_text                    │
│  3. Call Agent: POST /vouchers/extract → structured fields  │
│  4. Upsert vouchers + voucher_line_items                    │
│  5. Call Agent: POST /vouchers/suggest-journal → bút toán   │
│  6. Delete staged file                                      │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│  Agent (FastAPI + Claude Agent SDK)                         │
│  - Skill: voucher-invoice-vat, voucher-receipt, voucher-    │
│    bank-transfer, voucher-bank-statement                    │
│  - MCP tools:                                               │
│    * extract_voucher_fields(raw_text, voucher_type)         │
│    * verify_einvoice_tct(invoice_no, seller_mst) [opt]     │
│    * lookup_mst(mst) → tên doanh nghiệp                    │
│    * suggest_journal_entry(voucher_id) → N/C theo TT200    │
│    * export_vouchers_excel(voucher_ids[]) → xlsx URL        │
└─────────────────────────────────────────────────────────────┘
```

MCP standalone (kientruc.png diagram tách MCP Server ra) là **future refactor** khi cần scale — MVP giữ in-process pattern như contract module.

---

## 3 Phases

### Phase 1 — MVP Hoá đơn VAT (1.5 tuần)

**Mục tiêu:** Upload ảnh hoá đơn VAT điện tử → OCR → extract đủ field → suggest bút toán → xuất Excel.

**Schema mới** (`infra/postgres/init/14_vouchers.sql`):

```sql
CREATE TABLE vouchers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES users(id),
  tenant_id UUID REFERENCES tenants(id),
  voucher_type TEXT NOT NULL,  -- invoice_vat / receipt / payment / bank_transfer / bank_statement
  status TEXT DEFAULT 'processing',  -- processing / extracted / journalized / verified / rejected
  
  -- Common fields
  document_number TEXT,        -- Số hoá đơn / phiếu / lệnh
  document_date DATE,
  seller_name TEXT,
  seller_mst TEXT,              -- Mã số thuế bên bán
  buyer_name TEXT,
  buyer_mst TEXT,
  
  -- Amounts
  subtotal NUMERIC(18,2),       -- Thành tiền trước VAT
  vat_rate NUMERIC(5,2),        -- 0 / 5 / 8 / 10
  vat_amount NUMERIC(18,2),
  total_amount NUMERIC(18,2),
  currency TEXT DEFAULT 'VND',
  
  -- Verification
  tct_verified BOOLEAN DEFAULT FALSE,
  tct_verified_at TIMESTAMPTZ,
  tct_response JSONB,
  
  -- Source
  source_file_path TEXT,
  raw_ocr_text TEXT,
  extracted_fields JSONB,        -- Full extract JSON để retain nuance
  ocr_confidence NUMERIC(3,2),
  
  tags TEXT[] DEFAULT '{}'::text[],
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE voucher_line_items (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  voucher_id UUID NOT NULL REFERENCES vouchers(id) ON DELETE CASCADE,
  ordinal INT NOT NULL,
  description TEXT NOT NULL,
  quantity NUMERIC(18,4),
  unit TEXT,
  unit_price NUMERIC(18,2),
  subtotal NUMERIC(18,2),
  vat_rate NUMERIC(5,2),
  vat_amount NUMERIC(18,2)
);

CREATE TABLE voucher_journal_suggestions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  voucher_id UUID NOT NULL REFERENCES vouchers(id) ON DELETE CASCADE,
  entry_no INT NOT NULL,          -- 1, 2, 3... nếu nhiều bút toán
  debit_account TEXT NOT NULL,    -- TK Nợ (VD: 156, 1331, 641)
  credit_account TEXT NOT NULL,   -- TK Có (VD: 331, 111, 112)
  amount NUMERIC(18,2) NOT NULL,
  description TEXT,
  confidence NUMERIC(3,2),
  accepted_by UUID REFERENCES users(id),
  accepted_at TIMESTAMPTZ
);

CREATE INDEX idx_vouchers_user_status ON vouchers(user_id, status);
CREATE INDEX idx_vouchers_seller_mst ON vouchers(seller_mst);
CREATE INDEX idx_vouchers_type_date ON vouchers(voucher_type, document_date DESC);
```

**Backend Agent** (Python):

- `agent/src/aiq_agent/ocr/image.py` — new: image → PDF via Pillow trước feed OCR pipeline
- `agent/src/aiq_agent/api/ocr_routes.py` — extend endpoint accept `image/*` OR add new `/ocr/image`
- `agent/skills/voucher-invoice-vat/SKILL.md` — new skill, prompt extract JSON schema: `{document_number, document_date, seller_name, seller_mst, buyer_name, buyer_mst, subtotal, vat_rate, vat_amount, total_amount, line_items[]}`
- `agent/src/aiq_agent/agentic/voucher_tools.py` — new, 4 MCP tools:
  - `extract_voucher_fields(raw_text, voucher_type)` → JSON structured
  - `save_voucher(fields, raw_text, source_path)` → POST gateway
  - `verify_einvoice_tct(invoice_no, seller_mst)` → call TCT API (stub cho MVP nếu API không public)
  - `suggest_journal_entry(voucher_id)` → dùng skill `journal-entry-formatter` + `accounting-vietnam` cho ra Nợ/Có
- `agent/src/aiq_agent/agentic/registry.py` — register 4 tool codes mới trong `DEFAULT_ALLOWED_TOOLS`

**Backend Gateway** (C#):

- `gateway/src/Aiq.Gateway/Vouchers/VoucherRecords.cs` — DTO: Voucher, VoucherLineItem, VoucherJournalSuggestion
- `gateway/src/Aiq.Gateway/Vouchers/IVoucherRepository.cs` + `VoucherRepository.cs` — CRUD Postgres (pattern từ `NoteRepository.cs`)
- `gateway/src/Aiq.Gateway/Endpoints/VoucherEndpoints.cs` — REST endpoints:
  - `POST /vouchers/upload` (multipart, enqueue job)
  - `GET /vouchers` (list, filter by type/status/date)
  - `GET /vouchers/{id}` (detail + line items + journal suggestions)
  - `PATCH /vouchers/{id}` (update fields sau khi user edit)
  - `POST /vouchers/{id}/accept-journal` (user confirm bút toán)
  - `POST /vouchers/export-excel` (bulk export selected IDs → xlsx)
- `gateway/src/Aiq.Gateway/Jobs/VoucherOcrJobRunner.cs` — pattern từ `OcrJobRunner.cs`:
  1. Convert image→PDF nếu cần (Magick.NET hoặc ImageSharp)
  2. Call agent `/ocr/pdf`
  3. Call agent tool `extract_voucher_fields`
  4. Upsert `vouchers` + `voucher_line_items` (idempotent ON CONFLICT)
  5. Call agent tool `suggest_journal_entry`
  6. Upsert `voucher_journal_suggestions`
  7. Delete staged file
- `infra/postgres/init/13_governance_tools.sql` — append voucher tools với risk_tier

**Frontend Web** (Next.js):

- `web/src/app/(app)/vouchers/page.tsx` — list page: bảng với columns [Loại, Số CT, Ngày, Bên bán, Tổng tiền, VAT, Status, Actions]; filter tabs (VAT/thu chi/UN chi/sao kê); bulk select + "Xuất Excel"
- `web/src/app/(app)/vouchers/upload/page.tsx` — dropzone `image/*, application/pdf`, multi-file, progress per file; `useMutation` gọi `POST /api/vouchers/upload`
- `web/src/app/(app)/vouchers/[id]/page.tsx` — 3 columns:
  - Left: preview ảnh gốc (Next `<Image>`)
  - Center: bảng field extracted (editable, save via PATCH)
  - Right: bút toán suggest (Nợ/Có/Số tiền) với button "Chấp nhận" + button "Xác thực TCT" (optional)
- `web/src/app/api/vouchers/route.ts` + `[id]/route.ts` + `upload/route.ts` + `export-excel/route.ts` — proxy tới gateway
- Nav: thêm menu "Chứng từ" cho role admin/user

**Verification Phase 1:**

- Upload 5 hoá đơn VAT mẫu (VNPT, Viettel, MISA CQT) → 100% có `status='extracted'` sau <15s
- Extracted fields: >90% chính xác với number/date/amount, >80% với tên seller/buyer
- Bút toán suggest hợp lệ: Nợ 156/152/641 + Nợ 1331 + Có 331 (theo case)
- Xuất Excel: file `.xlsx` có sheet "Bút toán" columns [Số CT, Ngày, Nợ, Có, Số tiền, Diễn giải] format Misa import-ready
- TCT verify button hoạt động (dù response stub nếu API không mở)

### Phase 2 — Mở rộng loại chứng từ (1 tuần)

Skill mới:
- `agent/skills/voucher-receipt/` — phiếu thu/chi
- `agent/skills/voucher-bank-transfer/` — uỷ nhiệm chi
- `agent/skills/voucher-bank-statement/` — sao kê (table extraction — khó nhất, có thể phải thêm table-aware OCR như PP-Structure)

Extend `extract_voucher_fields(raw_text, voucher_type)` dispatch theo type → dùng skill khác nhau.

Bút toán logic riêng cho từng type:
- Phiếu thu: Nợ 111 / Có 131/511/711
- Phiếu chi: Nợ 331/641/642 / Có 111
- UN chi: Nợ 331/641 / Có 112
- Sao kê: N line → N cặp bút toán, dùng heuristic + LLM classify

**Verification Phase 2:** mỗi loại có ≥3 mẫu test, accuracy tương tự Phase 1.

### Phase 3 — MCP standalone + ERP integration (2-3 tuần, optional)

Chỉ làm nếu khách yêu cầu:

- Tách MCP tools ra standalone MCP server (theo `kientruc.png`) — dùng `@modelcontextprotocol/sdk` chạy stdio, agent connect qua subprocess
- 3 MCP server: `voucher-server`, `accounting-server`, `verification-server`
- Post ERP thật:
  - `mcp__misa__post_journal` (Misa API)
  - `mcp__fast__post_journal` (Fast Accounting)
  - `mcp__bravo__post_journal` (Bravo)
- Cần G3 governance (HITL approval per journal entry)

**Verification Phase 3:** integration test với ERP sandbox nếu khách cung cấp.

---

## Files sẽ tạo/sửa (Phase 1 detail)

**Agent (Python):**
- `agent/src/aiq_agent/ocr/image.py` (new)
- `agent/src/aiq_agent/api/ocr_routes.py` (extend)
- `agent/skills/voucher-invoice-vat/SKILL.md` (new)
- `agent/src/aiq_agent/agentic/voucher_tools.py` (new)
- `agent/src/aiq_agent/agentic/registry.py` (extend `DEFAULT_ALLOWED_TOOLS`)
- `agent/src/aiq_agent/agentic/runner.py` (register voucher_tools)

**Gateway (C#):**
- `gateway/src/Aiq.Gateway/Vouchers/VoucherRecords.cs` (new)
- `gateway/src/Aiq.Gateway/Vouchers/IVoucherRepository.cs` (new)
- `gateway/src/Aiq.Gateway/Vouchers/VoucherRepository.cs` (new)
- `gateway/src/Aiq.Gateway/Endpoints/VoucherEndpoints.cs` (new)
- `gateway/src/Aiq.Gateway/Jobs/VoucherOcrJobRunner.cs` (new)
- `gateway/src/Aiq.Gateway/Program.cs` (register endpoints + DI)

**Infra:**
- `infra/postgres/init/14_vouchers.sql` (new)
- `infra/postgres/init/13_governance_tools.sql` (append voucher tools)

**Web (Next.js):**
- `web/src/app/(app)/vouchers/page.tsx` (new)
- `web/src/app/(app)/vouchers/upload/page.tsx` (new)
- `web/src/app/(app)/vouchers/[id]/page.tsx` (new)
- `web/src/app/api/vouchers/route.ts` (new)
- `web/src/app/api/vouchers/[id]/route.ts` (new)
- `web/src/app/api/vouchers/upload/route.ts` (new)
- `web/src/app/api/vouchers/export-excel/route.ts` (new)
- `web/src/components/nav-links.tsx` (add "Chứng từ" link)

**Docker:**
- `docker-compose.yml` — không cần đổi (dùng volume `/var/aiq/uploads` sẵn có)

---

## Reusable từ code hiện có

- **OCR pipeline reuse**: `agent/src/aiq_agent/ocr/pipeline.py:53` — call thẳng `PipelineOrchestrator.run()`
- **Hangfire job pattern**: `gateway/src/Aiq.Gateway/Jobs/OcrJobRunner.cs` — copy structure, đổi call target
- **Contract module template**: copy 100% file structure từ `gateway/src/Aiq.Gateway/Contracts/` → `Vouchers/`
- **Page layout**: copy `web/src/app/(app)/contracts/*` structure
- **Save-to-KB modal**: `web/src/components/save-to-kb-button.tsx` — reuse cho "Lưu voucher vào KB"
- **Skill loader**: skills mới drop vào `agent/skills/` là tự load (loader `root.glob("*/SKILL.md")`)
- **Accounting tools**: 7 tools sẵn (`calculate_vat`, `lookup_account`, `journal_entry_validator`) — voucher tools call qua composition
- **Governance**: register vào `13_governance_tools.sql`, agent SDK tự enforce whitelist per agent

---

## Verification end-to-end

**Sau deploy Phase 1:**

1. **Golden path (JPG hoá đơn VAT):**
   ```bash
   curl -F "file=@invoice_vat_sample.jpg" http://cosmos:8080/vouchers/upload
   # → { voucherId, status: 'processing' }
   
   # Wait 10-15s (Hangfire dashboard /jobs/dashboard shows completion)
   
   curl http://cosmos:8080/vouchers/{voucherId}
   # → { status: 'extracted', document_number, seller_mst, subtotal, vat_amount, total_amount, line_items[], journal_suggestions[] }
   ```

2. **UI flow:**
   - Login `admin@bsn.vn` → `/vouchers/upload` → drag 3 ảnh hoá đơn → progress bar mỗi file
   - Auto redirect `/vouchers` list → thấy 3 rows status "extracted"
   - Click 1 row → detail page: ảnh preview + fields extracted + bút toán suggest
   - Edit 1 field → save → status "verified"
   - Bulk select 2 → click "Xuất Excel" → download `.xlsx` mở bằng Excel thấy sheet "Bút toán"

3. **Edge cases:**
   - Ảnh mờ / OCR confidence < 0.7 → status "extracted" + warning icon
   - Duplicate voucher (cùng document_number + seller_mst + date) → merge, không tạo mới
   - Voucher không phải hoá đơn VAT (upload nhầm) → LLM detect → status "rejected" + reason

4. **Regression:**
   - Contract upload flow vẫn work (không đụng shared code)
   - Notes CRUD vẫn work
   - Accounting agent chat vẫn work (7 accounting tools chưa bị override)

---

## Timeline

- **Phase 1 (Hoá đơn VAT MVP):** 1.5 tuần (10-12 ngày làm việc)
- **Phase 2 (3 loại còn lại):** 1 tuần
- **Phase 3 (MCP standalone + ERP post):** 2-3 tuần (chỉ khi khách yêu cầu)

Sau Phase 1 → demo cho anh Toàn → nếu OK thì Phase 2. Phase 3 gate theo commercial commitment.
