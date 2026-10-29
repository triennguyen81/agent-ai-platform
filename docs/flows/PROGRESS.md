# Tiến độ chứng từ flow v2

> **Khi resume:** chỉ đọc file này, rồi làm tiếp từ bước `⏳`/`[ ]` đầu tiên của PR đang làm.
> Không cần đọc lại chung-tu-current.md / chung-tu-proposed.md trừ khi bước đó ghi rõ mục cần đọc.
> Sau mỗi bước xong: đánh `[x]`, ghi ngắn kết quả + file đã đụng, commit cùng code của bước đó.

Branch: `feat/chung-tu-flow-v2` · Kế hoạch: `docs/flows/chung-tu-proposed.md` §9 · Quyết định: §10

## Cách chạy test (không có dotnet trên host)

```bash
# Postgres tạm (không đụng aiq-postgres)
docker network create aiq-test-net
docker run -d --name aiq-test-pg --network aiq-test-net -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=testpass \
  -e POSTGRES_DB=aiq_gw_test pgvector/pgvector:pg16 -c max_connections=1000
# Gateway (repo root)
docker run --rm --network aiq-test-net -e AIQ_TEST_POSTGRES="Host=aiq-test-pg;Port=5432;Database=aiq_gw_test;Username=postgres;Password=testpass" \
  -v aiq-gw-nuget-cache:/root/.nuget/packages -v "$PWD":/src -w /src/gateway mcr.microsoft.com/dotnet/sdk:9.0 \
  bash -c "dotnet build --configuration Release -warnaserror && dotnet test tests/Aiq.Gateway.Tests/Aiq.Gateway.Tests.csproj --configuration Release --no-build --settings tests/Aiq.Gateway.Tests/serial.runsettings"
# Agent (thư mục agent/)
docker run --rm -v "$PWD":/app -w /app -e PYTHONPATH=/app/src --entrypoint sh aiq-agent-pytest:latest -c \
  "pip install -q --target /tmp/r ruff==0.17.2; PYTHONPATH=/tmp/r python -m ruff check src tests; python -m pytest -q -p no:cacheprovider"
# Web (thư mục web/)
npx tsc --noEmit && npm test
# Dọn
docker rm -f aiq-test-pg; docker network rm aiq-test-net
```

Test DB-backed tự bỏ qua (pass rỗng) nếu thiếu `AIQ_TEST_POSTGRES` — kiểm tra log Postgres có `CREATE DATABASE`.

## PR1 — Guard an toàn ✅ (commit `addd106`, chờ review)

## PR2 — Validator ✅ (chờ review)

Phạm vi (§9 + QĐ 8, 9, 10, 11): validator thuần, chỉ tham khảo, chưa chặn. Migration 068 + down. `GET /vouchers/{id}/validation`.

- [x] 1. Đọc ngữ cảnh: dạng `vat_rate` trong DB (KCT/KKKNT?), hồ sơ công ty (MST), bảng journal suggestions, style migration 066
- [x] 2. Test unit `VoucherValidator` (viết trước, chạy đỏ)
- [x] 3. Cài `VoucherValidator` + `VatRateTable` + `MstRules` (xanh)
- [x] 4. Migration `068_voucher_validation.sql` + `down/068_voucher_validation.down.sql` + test up→down→up
- [x] 5. Repository (đọc `vat_rates`, `tenant_voucher_settings`, lưu `validation`) + `GET /vouchers/{id}/validation` + test tích hợp
- [x] 6. Chạy toàn bộ test (gateway, agent, web, ruff, tsc), commit, báo cáo PR2 (tóm tắt + test tay)

### Ghi chú PR2 (cập nhật dần)
- Bước 1: `vouchers.vat_rate` NUMERIC(5,2) = phần trăm (0/5/8/10), NULL khi nhiều mức (`infra/postgres/init/18_vouchers.sql:40`); KCT/KKKNT **không có cách biểu diễn** → bảng `vat_rates` có `rate NULL` cho KCT/KKKNT, validator chỉ so số.
- Input validator = `VoucherDetail` (`Vouchers/VoucherRecords.cs`, init props → dễ dựng trong test). Type constants: `VoucherTypes` (`Vouchers/VoucherTypeClassifier.cs:26-35`). MST công ty: `ITenantCompanyProfileRepository.GetAsync(...).TaxCode`.
- Migrator chỉ đọc `Data/Migrations/*.sql` cấp gốc (`DatabaseMigrator.cs:40`, csproj:45) → `down/` an toàn, không bị copy ra output; test đọc down qua `TaxVerificationDatabase.RepoPath`-style.
- Thiết kế: namespace `Aiq.Gateway.Vouchers.Validation`; `VoucherValidator.Validate(VoucherDetail, VoucherValidationContext)` thuần; `MstRules`; issue `{Code, Severity, Field(snake_case), Message}`.
- Ngoài PR2 (ghi rõ trong báo cáo): PERIOD_OPEN (PR4), DUPLICATE v2 (PR7), JOURNAL_ACCOUNT theo bảng TK (PR8, PR2 chỉ kiểm định dạng = warning), AMOUNT_IN_WORDS (sau).
- Bước 2: `tests/Aiq.Gateway.Tests/Vouchers/VoucherValidatorTests.cs` (pure, ~45 case). MST checksum: trọng số 31,29,23,19,17,13,7,5,3; d10 = 10 − (Σ mod 11), kết quả 10 = sai (thuật toán [CHỜ xác nhận] → chỉ warning). MST mẫu hợp lệ: 0312345673, 0101243150, 0100109106.
- Bước 3: `gateway/src/Aiq.Gateway/Vouchers/Validation/{VoucherValidator,MstRules}.cs` (không cần class `VatRateTable` riêng — `VatRateEntry` + list trong context). 52/52 unit xanh. Bẫy: app chạy globalization-invariant → **không dùng `CultureInfo("vi-VN")`** (throw); dùng `NumberFormatInfo` tự tạo. Mã lỗi đã cài: REQUIRED_FIELD, MST_FORMAT, MST_CHECKSUM(w), COMPANY_TAX_CODE_MISSING(w), BUYER_IS_COMPANY, SELLER_IS_COMPANY, AMOUNT_SUM, LINE_SUM, LINE_AMOUNT(w), VAT_LINE(w), VAT_AMOUNT(w), VAT_RATE, VAT_RATE_TABLE_EMPTY(w), DATE_FUTURE, JOURNAL_BALANCE, JOURNAL_ACCOUNT(w), JOURNAL_TOTAL, LOW_CONFIDENCE(w). Trường bắt buộc mặc định cho thu/chi (số, ngày, số tiền) và expense_claim (ngày, số tiền) là **[GIẢ ĐỊNH]** — QĐ 8 chỉ nêu danh sách cho hoá đơn.
- Bước 4: `068_voucher_validation.sql` tạo `vat_rates` (+seed 0/5/10/KCT/KKKNT từ 2009 "chờ kế toán duyệt", 8% 01/07/2025–31/12/2026 NĐ 174/2025; **không** seed các đợt 8% trước 07/2025), `tenant_voucher_settings`, `voucher_validations`. **Lệch tài liệu (cố ý):** kết quả lưu ở bảng riêng `voucher_validations`, không phải cột `vouchers.validation` (cột sẽ kích trigger `vouchers_touch_updated_at` + đụng chứng từ đã ghi sổ); `field_confidence`/`extraction_source` dời sang PR9/PR11 (backfill vouchers sẽ đụng trigger 059). Down: `down/068_voucher_validation.down.sql` (backup `*_backup_<ts>` rồi drop, xoá dòng schema_migrations). Test `Data/VoucherValidationMigrationTests.cs` xanh. Bẫy: `schema_migrations.checksum` NOT NULL + regex 64 hex.
- Bước 5: `Vouchers/Validation/VoucherValidationService.cs` (repo `IVoucherValidationRepository` + service; "hôm nay" = UTC+7, không cần tzdata; `TimeProvider` đăng ký ở `Program.cs`). Route `GET /vouchers/{id}/validation` → `VoucherEndpoints.ValidationAsync` (TryScope: USER/ADMIN cùng phòng ban; khác phòng ban 404; TENANT_OWNER/SUPER_ADMIN 403). Mỗi lần GET tính lại + upsert `voucher_validations`, không ghi vào `vouchers`. Test `Data/VoucherValidationIntegrationTests.cs` xanh.
- Bước 6: gateway Release `-warnaserror` + full suite serial **969/969**; agent ruff sạch, pytest 407 pass / 8 skip; web tsc sạch, vitest 102/102. Đã đồng bộ `chung-tu-proposed.md` (§3.3, §4.1, §9). Container `aiq-test-pg` + network `aiq-test-net` đã xoá.

## PR3 — Vòng đời + audit (chưa bắt đầu — chờ user review PR1, PR2)
