# Sprint 7 — DLP Phase B: Integration vào production pipeline

> **Scope:** Wire Phase A library vào IAgentClient + audit log + admin UI
> **Timeline:** 2-3 ngày
> **Status:** 🚧 IN PROGRESS
> **Prerequisite:** Phase A (`f677059`) — 51/51 tests pass
> **Verification bar:** Upload voucher → prompt gửi Claude không chứa PII thật; response hiển thị PII thật cho user; audit log có event

---

## Mục tiêu Phase B

Thực tế hoá DLP: mọi prompt đi ra Claude cloud **phải** đi qua scrubber trước, response về được unmask lại cho user. Feature flag để rollback nhanh nếu phát hiện vấn đề.

```
IAgentClient (interface)
     ▲
     │ decorated by
     │
ScrubbingAgentClient ──wraps──▶ ClaudeAgentClient (real impl)
     │
     ├── IPiiScrubber.Scrub(prompt) → masked prompt
     ├── call wrapped client with masked prompt
     ├── IPiiScrubber.Unmask(response) → original PII back
     └── IDlpEventRepository.LogAsync(count, types)
```

---

## Deliverables

### B1. Progress doc ⬜
- [x] File này

### B2. DlpOptions config ⬜
- [x] `gateway/src/Aiq.Gateway/Configuration/DlpOptions.cs`
- [x] `SectionName = "Dlp"`, properties: `Enabled` (bool, default false), `VaultTtlMinutes` (int, default 10), `LogDetectedValues` (bool, default false — only for debug)
- [x] Add to `appsettings.cosmos.json` section Dlp: `{ "Enabled": true, "VaultTtlMinutes": 10 }`

### B3. Migration 050 + DLP event repo ⬜
- [x] `gateway/src/Aiq.Gateway/Data/Migrations/050_dlp_events.sql`
  - Table `sim_dlp_events`: id, tenant_id, user_id, request_id, direction (scrub/unmask), pii_count, pii_types_json, text_length_chars, created_at
  - Index tenant_id + created_at DESC
  - KHÔNG lưu actual PII values hay masked text (compliance)
- [x] `gateway/src/Aiq.Gateway/Dlp/IDlpEventRepository.cs` + impl
  - `Task LogAsync(DlpEvent event, CancellationToken)`
  - `Task<DlpEventSummary> GetSummaryAsync(tenantId, days, CancellationToken)`

### B4. ScrubbingAgentClient decorator ⬜
- [x] `gateway/src/Aiq.Gateway/Dlp/ScrubbingAgentClient.cs`
- [x] Implement `IAgentClient` — delegate wrapped client
- [x] Intercept each method that sends text to Claude:
  - `RunTaskAsync` — scrub prompt, unmask response (if text)
  - `RunOcrPipelineAsync` — OCR không gửi text, chỉ gửi image → không cần scrub input; output text là OCR result có thể chứa PII nhưng không gửi ra Claude → không cần unmask
- [x] Emit `IDlpEventRepository.LogAsync` cho mỗi scrub
- [x] Short-circuit nếu `DlpOptions.Enabled = false`

### B5. Program.cs DI wire ⬜
- [x] Register `DlpOptions` qua Options pattern
- [x] Register all `IPiiDetector` implementations as singleton
- [x] Register `DeterministicMasker` singleton
- [x] Register `IPiiScrubber` singleton
- [x] Register `IDlpEventRepository` scoped
- [x] Decorate `IAgentClient` with `ScrubbingAgentClient` CHỈ khi `Enabled = true`

### B6. Admin UI dashboard ⬜
- [x] `web/src/app/(app)/admin/dlp/page.tsx`
- [x] 3 summary cards: total events (24h), total PII masked (24h), top type
- [x] Chart: events theo ngày (7 ngày gần nhất)
- [x] Table: breakdown theo PII type với counts
- [x] Policy: nav link "/admin/dlp" chỉ cho `tenant_owner` role

### B7. Build + verify ⬜
- [x] Gateway build 0 error
- [x] Web tsc clean
- [x] Phase A tests still pass 51/51
- [x] New tests cho ScrubbingAgentClient (3-5 cases)

### B8. Deploy cosmos ⬜
- [x] Commit + push
- [x] Pull trên cosmos + rebuild gateway + web
- [x] Verify migration 050 runs clean
- [x] Enable feature flag qua appsettings.cosmos.json

### B9. E2E verification ⬜
- [x] Upload voucher mẫu có PII thật (hoặc test fixture)
- [x] Check gateway logs: thấy scrub count ≥ 1
- [x] Query `sim_dlp_events` → thấy row mới
- [x] Voucher detail page → PII vẫn hiển thị đúng (unmask working)
- [x] Admin dashboard `/admin/dlp` → thấy event summary

### B10. Commit + progress update ⬜

---

## Non-goals Phase B

- ❌ 4-tier data classification registry (Phase C)
- ❌ Policy engine (reject nếu Restricted trong prompt) → Phase C
- ❌ Rate limiting / backpressure cho DLP calls (optimize sau)
- ❌ Multi-language detector (chỉ VN)

---

## Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Decorator ảnh hưởng latency hiện tại | Trung | Benchmark trước/sau, feature flag để rollback nhanh |
| Agent không hiểu `<CMND_xxx>` placeholder → trả lời bậy | Cao | Test với sample voucher real; nếu bị → tune token format (long form "John_Doe_PII_PLACEHOLDER") |
| Unmask không trả về PII vì Claude paraphrase token | Cao | Round-trip test; nếu bị → fallback original prompt (log warning) |
| Audit log table swell nhanh | Thấp | Retention 90 ngày (TBD Phase C) |
| Config `Enabled=false` nhưng DI vẫn register decorator | Thấp | Conditional registration trong Program.cs |

---

## Verification mapping

| Spec requirement | Phase B deliverable |
|---|---|
| "Lắp context, gắn nhãn, che PII, tạo view Restricted" | B4 ScrubbingAgentClient |
| "Audit log append-only" | B3 sim_dlp_events (INSERT only, không UPDATE) |
| "Mỗi hành động của agent có nhật ký" | B3 LogAsync cho mỗi scrub call |
| Compliance NĐ13/2023 | B4 masking + B3 audit trail cho DPIA |
