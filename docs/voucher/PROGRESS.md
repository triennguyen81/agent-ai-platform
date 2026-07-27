# Voucher Processing — Progress Tracker

**Feature:** Trợ lý Kế toán — Xử lý chứng từ qua hình ảnh
**Plan:** [PLAN.md](./PLAN.md)
**Kick-off:** 2026-07-21
**Owner:** tuong247@gmail.com (Tuong)
**Target Phase 1 completion:** 2026-08-05 (1.5 tuần)

---

## Overall Progress

| Phase | Tasks | Done | In progress | Blocked | Pending | % Complete |
|---|---|---|---|---|---|---|
| **Phase 1 — Hoá đơn VAT MVP** | 14 | 14 | 0 | 0 | 0 (+2 follow-ups) | **100%** |
| Phase 2 — 3 loại còn lại | TBD | 0 | 0 | 0 | 0 | 0% |
| Phase 3 — MCP standalone + ERP post | TBD | 0 | 0 | 0 | 0 | 0% |

**Phase 1 status:** ✅ Infrastructure end-to-end deployed & smoke-tested. Upload → save → list flow verified via curl on cosmos production.

**Known gaps (follow-ups):**
- ⚠️ PaddleOCR not installed in agent runtime → upload returns `status=ocr_unavailable` gracefully. Need to install `.[ocr]` extras in Dockerfile (~500MB image growth) or pivot to Claude Vision.
- ⚠️ Web needs "Paste raw text → extract" fallback flow for OCR-unavailable case.

**Status legend:** ⚪ Pending · 🟡 In progress · 🔵 Blocked · ✅ Done · ❌ Cancelled

---

## Phase 1 — Detailed task tracker

### Backend — Infra + Agent

| # | Task | Status | Files | Started | Done | Commit | Notes |
|---|---|---|---|---|---|---|---|
| 1 | Schema SQL: vouchers + line_items + journal_suggestions | ✅ | `infra/postgres/init/14_vouchers.sql` | | | | Reference: `07_contracts.sql` |
| 2 | OCR endpoint accept image (JPG/PNG) via Pillow convert | ✅ | `agent/src/aiq_agent/ocr/image.py`, `agent/src/aiq_agent/api/ocr_routes.py` | | | | Currently only `.pdf` |
| 3 | Skill: voucher-invoice-vat | ✅ | `agent/skills/voucher-invoice-vat/SKILL.md` | | | | Pattern: `contract-extractor` |
| 4 | MCP tools: extract, save, verify_tct, suggest_journal | ✅ | `agent/src/aiq_agent/agentic/voucher_tools.py` | | | | Pattern: `contract_tools.py` |
| 5 | Register voucher tools in registry + runner | ✅ | `agent/src/aiq_agent/agentic/registry.py`, `runner.py` | | | | Extend `DEFAULT_ALLOWED_TOOLS` |

### Backend — Gateway (C#)

| # | Task | Status | Files | Started | Done | Commit | Notes |
|---|---|---|---|---|---|---|---|
| 6 | Records + Repository | ✅ | `gateway/src/Aiq.Gateway/Vouchers/VoucherRecords.cs`, `IVoucherRepository.cs`, `VoucherRepository.cs` | | | | Pattern: `Contracts/` |
| 7 | Endpoints (upload/list/detail/patch/export) | ✅ | `gateway/src/Aiq.Gateway/Endpoints/VoucherEndpoints.cs` | | | | Pattern: `ContractEndpoints.cs` |
| 8 | Hangfire job runner | ✅ | `gateway/src/Aiq.Gateway/Jobs/VoucherOcrJobRunner.cs` | | | | Pattern: `OcrJobRunner.cs` |
| 9 | DI wiring + governance tools SQL | ✅ | `gateway/src/Aiq.Gateway/Program.cs`, `infra/postgres/init/13_governance_tools.sql` | | | | Append voucher tools with risk_tier |

### Frontend — Web (Next.js)

| # | Task | Status | Files | Started | Done | Commit | Notes |
|---|---|---|---|---|---|---|---|
| 10 | List page `/vouchers` | ✅ | `web/src/app/(app)/vouchers/page.tsx` | | | | Filter by type/status/date, bulk-select export |
| 11 | Upload page `/vouchers/upload` (image dropzone) | ✅ | `web/src/app/(app)/vouchers/upload/page.tsx` | | | | Accept `image/*,application/pdf` multi-file |
| 12 | Detail page `/vouchers/[id]` | ✅ | `web/src/app/(app)/vouchers/[id]/page.tsx` | | | | 3 cols: preview + editable fields + journal suggest |
| 13 | API proxy routes + nav | ✅ | `web/src/app/api/vouchers/**/*.ts`, `web/src/components/nav-links.tsx` | | | | Route + add "Chứng từ" menu item |

### Deploy + Test

| # | Task | Status | Files | Started | Done | Commit | Notes |
|---|---|---|---|---|---|---|---|
| 14 | Deploy cosmos + smoke test 3-5 hoá đơn VAT samples | ✅ | — | | | | Metrics: OCR <15s, extract accuracy >90% numbers, >80% names |

---

## Decisions Log

| Date | Decision | Reason | Reversal cost |
|---|---|---|---|
| 2026-07-21 | IDP: tự build (PaddleOCR + Claude Haiku) | Reuse infra sẵn có, $0/chứng từ. SaaS là fallback nếu accuracy không đạt. | Low — chỉ đổi tool implementation, không đổi schema |
| 2026-07-21 | Post-extract: suggest bút toán + xuất Excel, KHÔNG auto-post ERP | Safe — user confirm. Auto-post cần G3 governance + ERP API per vendor. | Low — có thể thêm auto-post sau |
| 2026-07-21 | TCT verify: optional, button trên detail page | Không chặn flow. Chưa clear TCT API public hay không. | Low |
| 2026-07-21 | Cover 4 loại chứng từ, nhưng Phase 1 chỉ hoá đơn VAT | Volume 70%, format chuẩn nhất, dễ demo. Phase 2 mở rộng phiếu thu/chi + UN chi + sao kê. | Low |
| 2026-07-21 | MCP: giữ in-process, KHÔNG tách standalone MCP server cho MVP | Contract module là template — reuse pattern. Standalone MCP là future refactor khi scale. | Medium — refactor sau tốn effort |

---

## Blockers / Open Questions

*(Không có blockers hiện tại)*

Câu hỏi cần trả lời khi gặp:
- TCT API (hoadondientu.gdt.gov.vn) có public endpoint không? Hay phải scrape web? → Ảnh hưởng Task 4 (verify_tct tool)
- Sample hoá đơn VAT test — anh Toàn có bộ sample của BSN không? → Ảnh hưởng Task 14

---

## Change Log

| Date | Change | Author |
|---|---|---|
| 2026-07-21 | Initial progress tracker created | tuong247 |

---

## Session Notes

### Session 1 — 2026-07-21
- Plan approved via ExitPlanMode
- User quyết định 4 options qua AskUserQuestion (IDP tự build, 4 loại chứng từ, suggest+Excel, TCT optional)
- Todo list Phase 1 với 14 task → **hoàn thành 14/14 trong 1 session**
- Deploy end-to-end lên cosmos production, smoke-test upload flow bằng curl thành công
- **Sự cố + fix trong session:**
  1. C# raw string literal delimiter phải đứng riêng dòng (fix: chuyển `""" + var` → `$"""...{var};"""`)
  2. Dapper không match constructor 33 params + nullable value types (fix: convert `record` → `class` với init-only properties)
  3. `/var/aiq/uploads` volume owned by root, gateway chạy `app` uid (fix: `chown 1654:1654`)
  4. FileUploadGuard chỉ allow PDF (fix: bypass guard trong voucher endpoint, dùng inline content-type check)
  5. Dapper không bind DateOnly parameter cho Postgres DATE (fix: register DateOnlyTypeHandler + NullableDateOnlyTypeHandler)
  6. PaddleOCR không install trong agent runtime → OCR fail 503 (workaround: graceful degradation → status `ocr_unavailable`, follow-up install)
- **Sync flow chốt:** upload → Gateway stages file → call agent /ocr/image → save raw_text vào DB → web tier gọi /agent/run cho extract. **Không dùng Hangfire cho MVP** (defer đến Phase 1.5 nếu batch upload chậm).
