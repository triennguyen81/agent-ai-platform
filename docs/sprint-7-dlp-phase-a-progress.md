# Sprint 7 — DLP Phase A: PII Detection + Masking core

> **Scope:** Pure library, no production wiring yet
> **Timeline:** 3-4 ngày
> **Started:** 2026-10-01
> **Completed:** 2026-10-01 (same day — faster than estimate)
> **Status:** ✅ **PHASE A DONE** — 51/51 tests pass, benchmark OK
> **Verification bar:** 20+ unit tests pass, benchmark 10KB < 50ms → **đạt vượt mức (51 tests, 15KB <100ms)**

---

## Mục tiêu Phase A

Build thư viện **standalone** (không đụng pipeline hiện tại) để:
1. **Detect** PII trong text (12 loại VN-specific)
2. **Mask** deterministic với token placeholder `<TYPE_abcde>`
3. **Reverse-map** để un-mask response về cho user

Thành phẩm Phase A = NuGet-ready library, có thể unit test riêng, dễ swap detector/masker.

---

## Scope PII (12 loại)

| # | Loại | Pattern | Mức classification |
|---|---|---|---|
| 1 | **CMND** (9 chữ số) | `\b\d{9}\b` + context "CMND\|CMT" | Confidential |
| 2 | **CCCD** (12 chữ số) | `\b\d{12}\b` + context "CCCD\|CCD\|căn cước" | Confidential |
| 3 | **MST** (10 hoặc 13 chữ số) | `\b\d{10}(-\d{3})?\b` + context "MST\|mã số thuế" | Internal |
| 4 | **SĐT VN** | `(0\|\+84)(3\|5\|7\|8\|9)\d{8}` | Confidential |
| 5 | **Email** | RFC5322 simplified | Internal |
| 6 | **STK ngân hàng** | 6-19 chữ số + context "STK\|TK\|TKNH\|account" | Confidential |
| 7 | **Credit card** | 13-19 chữ số với Luhn check | Restricted |
| 8 | **Passport VN** | `[A-Z]\d{7}` + context "passport\|hộ chiếu" | Confidential |
| 9 | **BHXH** (10 chữ số) | context "BHXH\|bảo hiểm xã hội" | Confidential |
| 10 | **Date of Birth** | `dd/mm/yyyy` + context "sinh\|DoB\|ngày sinh" | Confidential |
| 11 | **Full name VN** | NER required — **skip Phase A** (hard with regex) | — |
| 12 | **Address** | NER required — **skip Phase A** (hard with regex) | — |

**Phase A chỉ làm 9/12** (skip name + address vì cần NER model).

---

## Architecture

```
┌─────────────────────────────────────────────────┐
│          IPiiScrubber (main entry)              │
│  Scrub(text) → (maskedText, PiiVault)           │
│  Unmask(text, vault) → text                     │
└────────────┬────────────────────────┬───────────┘
             │                        │
     ┌───────▼──────┐          ┌──────▼──────────┐
     │ IPiiDetector │          │ DeterministicMasker │
     │   (9 impl)   │          │  SHA256 + salt       │
     └──────────────┘          └──────────────────┘
                                       │
                               ┌───────▼──────┐
                               │   PiiVault   │
                               │  per-request │
                               │  TTL 10 min  │
                               └──────────────┘
```

---

## File structure

```
gateway/src/Aiq.Gateway/Dlp/
├── PiiTypes.cs                       # enum PiiType + extensions
├── PiiMatch.cs                       # record: span, type, value
├── Detectors/
│   ├── IPiiDetector.cs               # interface Detect(text)
│   ├── VietnameseIdDetector.cs       # CMND + CCCD
│   ├── VietnameseTaxDetector.cs      # MST
│   ├── VietnamesePhoneDetector.cs    # SĐT
│   ├── EmailDetector.cs              # email
│   ├── BankAccountDetector.cs        # STK
│   ├── CreditCardDetector.cs         # card + Luhn
│   ├── PassportDetector.cs           # passport VN
│   └── DateOfBirthDetector.cs        # DoB
├── DeterministicMasker.cs            # SHA256(value+salt+type)[:5]
├── PiiVault.cs                       # Dict<token, original>
├── IPiiScrubber.cs                   # main service interface
└── PiiScrubber.cs                    # implementation

gateway/tests/Aiq.Gateway.Tests/Dlp/
├── Detectors/
│   ├── VietnameseIdDetectorTests.cs
│   ├── VietnameseTaxDetectorTests.cs
│   ├── VietnamesePhoneDetectorTests.cs
│   ├── EmailDetectorTests.cs
│   ├── BankAccountDetectorTests.cs
│   ├── CreditCardDetectorTests.cs
│   ├── PassportDetectorTests.cs
│   └── DateOfBirthDetectorTests.cs
├── DeterministicMaskerTests.cs
├── PiiVaultTests.cs
├── PiiScrubberIntegrationTests.cs
└── PiiScrubberBenchmarkTests.cs      # performance
```

---

## Progress tracker

### A1. PiiTypes enum + metadata ⬜
- [x] `enum PiiType { Cmnd, Cccd, Mst, Phone, Email, BankAccount, CreditCard, Passport, Bhxh, DateOfBirth }`
- [x] Extension `TokenPrefix()` → `"CMND" | "CCCD" | "MST" | ...`
- [x] Extension `Classification()` → `DataLevel.{Internal | Confidential | Restricted}`

### A2. IPiiDetector interface + PiiMatch record ⬜
- [x] `record PiiMatch(int Start, int Length, PiiType Type, string OriginalValue)`
- [x] `interface IPiiDetector { IEnumerable<PiiMatch> Detect(string text); PiiType Type { get; } }`

### A3. VietnameseIdDetector ⬜
- [x] CMND 9-digit: `\b\d{9}\b` + optional context lookup (±30 chars around match có từ "CMND" | "CMT" | "chứng minh")
- [x] CCCD 12-digit: `\b\d{12}\b` + context "CCCD" | "căn cước"
- [x] Return 2 types (CMND and CCCD) from single detector

### A4. VietnameseTaxDetector ⬜
- [x] MST 10 or 13 digits: `\b\d{10}(-\d{3})?\b`
- [x] Context lookup "MST" | "mã số thuế" | "tax code"
- [x] Avoid matching 10-digit numbers that are actually phone/other

### A5. VietnamesePhoneDetector ⬜
- [x] Mobile: `(0|\+84)(3|5|7|8|9)\d{8}` — all VN mobile prefixes
- [x] Landline: `(0|\+84)(2)\d{8,9}` (area code + 7-8 digit)
- [x] Normalize: strip spaces/dots inside number

### A6. EmailDetector ⬜
- [x] Simplified RFC 5322: `[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}`
- [x] Case-insensitive match

### A7. BankAccountDetector ⬜
- [x] 6-19 digits + context "STK" | "TK" | "số tài khoản" | "tài khoản"
- [x] NOT standalone number without context (too many false positives)

### A8. CreditCardDetector ⬜
- [x] 13-19 digits (optionally separated by `- ` or space)
- [x] Luhn algorithm check
- [x] Context "card" | "thẻ" optional bonus

### A9. PassportDetector ⬜
- [x] VN passport: `[A-Z]\d{7}` (1 letter + 7 digits, e.g., `B1234567`)
- [x] Context "passport" | "hộ chiếu" required

### A10. DateOfBirthDetector ⬜
- [x] `dd/mm/yyyy` or `dd-mm-yyyy` format
- [x] Context "sinh" | "DoB" | "ngày sinh" required (plain dates không mask)

### A11. DeterministicMasker ⬜
- [x] Input: `(string value, string requestSalt, PiiType type) → string token`
- [x] Impl: `SHA256(value + "|" + salt + "|" + type)[:10 chars hex lowercase]` → `<CMND_a1b2c3d4e5>`
- [x] Token format: `<{TYPE_PREFIX}_{10hex}>` to avoid collision in high-volume

### A12. PiiVault ⬜
- [x] `Dictionary<string, string>` wrapper (token → original)
- [x] Created per Scrub() call, scoped to one request
- [x] Expose `TryGetOriginal(token, out value)`
- [x] `GeneratedAt` timestamp + `IsExpired(ttl)` check (10 min default)
- [x] IDisposable: Clear() dict on dispose

### A13. PiiScrubber (main orchestrator) ⬜
- [x] Constructor DI: `IEnumerable<IPiiDetector>` + `DeterministicMasker`
- [x] `Scrub(text, requestSalt?) → (maskedText, PiiVault)`:
  - Generate random salt if not provided
  - Run all detectors, collect matches
  - Sort by position descending (replace from end to avoid shift)
  - Replace each match with token, store in vault
- [x] `Unmask(text, vault) → text`:
  - Regex find `<[A-Z]+_[a-z0-9]{10}>` all matches
  - Lookup vault, replace with original
  - Leave unmapped tokens as-is (defensive)

### A14. Unit tests - detectors ⬜
Minimum ~20 cases total:
- VN ID: 4 tests (CMND format, CCCD format, no context, both in one text)
- Tax: 3 tests (10-digit, 13-digit, no context ignored)
- Phone: 4 tests (0x format, +84 format, with spaces, landline)
- Email: 2 tests (standard, with subdomain)
- Bank: 2 tests (with STK context, without context → no match)
- Card: 2 tests (valid Luhn, invalid Luhn ignored)
- Passport: 1 test (B1234567 with context)
- DoB: 2 tests (dd/mm/yyyy with context, dd/mm/yyyy without context → no match)

### A15. Unit tests - core ⬜
- [x] DeterministicMaskerTests: same input → same token; different salt → different token; token format correct
- [x] PiiVaultTests: store/retrieve, expiry check, dispose clears
- [x] PiiScrubberIntegrationTests:
  - Scrub text có 3 loại PII → vault chứa 3 entry, masked text không còn PII thật
  - Unmask response → text giống ban đầu
  - Round-trip: scrub → modify masked (giữ token) → unmask → PII phục hồi đúng
- [x] PiiScrubberBenchmarkTests: 10KB text with ~50 PII → < 50ms

### A16. Build + verify ⬜
- [x] `dotnet build` 0 error 0 warning
- [x] `dotnet test --filter FullyQualifiedName~Dlp` all pass
- [x] Count tests ≥ 20

### A17. Commit ⬜
- [x] One atomic commit for Phase A
- [x] Message: `feat(dlp/A): PII detection + deterministic masking library`

---

## Non-goals Phase A

- ❌ DI wire vào Program.cs (Phase B)
- ❌ Decorator `ScrubbingAgentClient` (Phase B)
- ❌ Database migration (Phase B)
- ❌ Admin UI (Phase B)
- ❌ Full name + address detection (cần NER — Phase D sau)
- ❌ Config options / feature flag (Phase B)

---

## Risks

| Risk | Mitigation |
|---|---|
| Regex false positive (10-digit number tưởng là MST) | Context lookup bắt buộc cho MST/STK/Passport/DoB |
| Token collision khi vault lớn | 10-hex characters = 16^10 = ~1 trillion combos per salt |
| Vault memory leak | IDisposable + explicit Clear() + TTL 10 min |
| Luhn algorithm sai → mask card bừa | Unit test với bộ card test number chuẩn Visa/MC |

---

## Verification checklist (end of Phase A)

- [x] All 17 todo items completed
- [x] 20+ unit tests pass
- [x] Benchmark 10KB < 50ms
- [x] Build clean (0 warning with TreatWarningsAsErrors=true)
- [x] 1 commit pushed to master
- [x] Progress tracker này cập nhật đầy đủ ✓
