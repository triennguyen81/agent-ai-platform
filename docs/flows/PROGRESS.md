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

## PR1 — Guard an toàn ✅ (commit `addd106`, user duyệt 2026-10-29)

## PR2 — Validator ✅ (user duyệt 2026-10-29, sửa tiếp ở PR2.x)

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

## PR2.x — Sửa sau review PR2 ✅ (push `38f6404`; user 2026-10-29: chỉ test local, không cần kiểm CI)

Yêu cầu user (2026-10-29): (1) GET validation không ghi DB, chỉ lưu khi chứng từ đổi + khi submit (PR3), thêm snapshot phiên bản chứng từ (migration mới + down); (2) `JOURNAL_TOTAL` → warning vĩnh viễn, thêm ERROR tổng Nợ = tổng Có; (3) "hôm nay" theo Asia/Ho_Chi_Minh, cho phép lệch 1 ngày, test 23:30 / 00:30 giờ VN; (4) test VAT_RATE dùng vat_rates riêng, không dựa seed 068; (5) ghi mục chờ người dùng.

- [x] 1. Validator: `JOURNAL_TOTAL` warning; `JOURNAL_BALANCE` = tổng Nợ = tổng Có (error), kiểm từng dòng đổi tên `JOURNAL_LINE`; `DATE_FUTURE` > hôm nay VN + 1 ngày; `Version` = 2; test unit trước
- [x] 2. Migration `069_voucher_validation_snapshot.sql` (+ down) thêm `voucher_updated_at`, `voucher_fingerprint` vào `voucher_validations`; test up→down→up
- [x] 3. Service tách `ComputeAsync` (GET, không ghi) / `RecordAsync` (lưu); gọi Record sau tạo / PATCH / apply-extraction / job trích xuất (upload + reextract) / bút toán gợi ý / đổi loại; test tích hợp
- [x] 4. Test tích hợp VAT_RATE với bảng `vat_rates` của riêng test (các đợt 8% không liên tục)
- [x] 5. Chạy đủ test, đồng bộ tài liệu, commit, push, kiểm tra GitHub Actions (`gh run list --branch feat/chung-tu-flow-v2 --limit 3`); đỏ thì sửa

### Ghi chú PR2.x
- Bước 1: `VoucherValidator.cs` (Version 2, `FutureDateToleranceDays = 1`), `VoucherValidationService.TodayInVietnam` dùng tz `Asia/Ho_Chi_Minh` (fallback UTC+7 cố định). Mã mới `JOURNAL_LINE` (từng dòng, error); `JOURNAL_BALANCE` giờ là tổng Nợ = tổng Có (error) — với cấu trúc cặp hiện tại chỉ lệch khi dòng thiếu TK; `JOURNAL_TOTAL` warning. Test unit 61/61.
- Bước 2: `069_voucher_validation_snapshot.sql` thêm `voucher_updated_at TIMESTAMPTZ`, `voucher_fingerprint TEXT` (CHECK 64 hex), NULL = dòng cũ → coi là cũ. Down chỉ drop 2 cột (dữ liệu dẫn xuất, không backup), giữ kết quả. Test 068 "up again" giờ chạy 068+ để 069 không mất cột. 3/3 xanh.
- Bước 3: `VoucherValidationService` → `ComputeAsync` (GET, không ghi), `RecordAsync` (lưu + snapshot), `TryRecordAsync` (sau thay đổi; lỗi chỉ log vì thay đổi đã commit — dòng thiếu/cũ tự lộ qua snapshot). Report = `VoucherValidationReport` (thay `StoredVoucherValidation`). Fingerprint = SHA-256 JSON {voucher, line items, journal}. Hook: `CreateAsync`, `UpdateAsync`, `ApplyExtractionAsync`, `SaveJournalAsync`, `DecideJournalAsync`, `SetVoucherTypeAsync`, `VoucherExtractJobRunner` (sau khi ghi field + journal → upload và reextract). `UpdateAsync`/`DecideJournalAsync` đổi sang internal cho test. Job runner thêm tham số ctor (sửa 2 test dựng runner). Mutation check: GET ghi lại + bỏ hook create → 5 test đỏ.
- Bước 4: `VoucherValidationIntegrationTests` dùng `IAsyncLifetime` thay bảng `vat_rates` bằng dữ liệu riêng (3 đợt 8% có khoảng trống) trước mỗi test; Theory 5 ngày (trong/ngoài đợt). 116 test liên quan xanh.
- Bước 5: full suite — gateway Release `-warnaserror` serial **987/987**; agent ruff sạch, pytest 407 pass / 8 skip; web tsc sạch, vitest 102/102. Đồng bộ `chung-tu-proposed.md` (§3.3 bảng quy tắc, §4.1 thêm 069, §5 GET chỉ đọc, §9 PR2). **Đánh lại số migration dự kiến**: lifecycle 067 → **070** (067 tạo sau 068/069 sẽ chạy lệch thứ tự giữa DB mới và DB đã migrate), accounting_periods → 071, chart_of_accounts → 072, posting_integrity → 073, dedup_v2 → 074.
- CI: `ci.yml` chỉ chạy khi push `master`, PR vào `master` hoặc `workflow_dispatch` — push branch **không** kích CI. Máy không có `gh`.
- "lệch 1 ngày": user xác nhận là quyết định của họ ngày 29/10 → **QĐ 25** trong §10.

## Đang chờ người dùng / kế toán
- Kế toán bổ sung các đợt giảm thuế GTGT 8% trước 07/2025 theo từng nghị quyết/nghị định (thêm dòng `vat_rates`, không sửa code).
- Xác nhận trường bắt buộc cho phiếu thu/chi (`document_date`, `document_number`, `total_amount`) và đề nghị thanh toán (`document_date`, `total_amount`) — hiện là [GIẢ ĐỊNH].
- QĐ 8 (danh sách trường bắt buộc chính thức), QĐ 14 (bảng TK TT99/TT133 + ánh xạ, trước PR8), thuật toán checksum MST.

## PR3 — Vòng đời + audit ⏳

Phạm vi (§2, §3.7, §4.1 `070`, §4.2, §5, §6.3, QĐ 1, 2, 3, 20, 23, 24): chỉ test local (không kiểm CI).
Ngoài PR3: `post`/`reverse`/`{post:true}`/alias `apply-*` + gate L2 + `If-Match` (PR5), kỳ kế toán (PR4), `supersede` (PR6), unique dedup bỏ qua xoá mềm (PR7).

- [x] 1. QĐ 25 vào §10; ghi nhớ "chỉ test local"; kế hoạch PR3
- [ ] 2. `VoucherStateMachine` thuần + test ma trận trạng thái × hành động × vai trò (ADMIN tự duyệt chứng từ của mình; `reversed`/`rejected` cuối; agent bị từ chối)
- [ ] 3. Migration `070_voucher_lifecycle.sql` + down: cột vòng đời, `voucher_events` append-only, `tenant_feature_flags`, trigger đồng bộ `status`→`lifecycle_state` (+ `lifecycle_version`, `posted_at`), trigger ghi event (actor từ `set_config`), backfill (không đổi `updated_at`), chuyển `journalized`; test up→down→up
- [ ] 4. Repository vòng đời (chuyển trạng thái nguyên tử + event cùng transaction, đọc events, cờ tenant) + guard `voucher_locked` trên các đường sửa (PATCH, apply-extraction, reextract, job, bút toán, đổi loại) + thông báo agent/web
- [ ] 5. Validator → vòng đời (khi bật cờ): `extracted`/`needs_fix`/`validated` theo kết quả
- [ ] 6. Endpoint `submit`/`approve`/`return`/`reject` + `GET /events`, theo cờ `voucher_flow_v2`; test ACL (ADMIN tự duyệt OK, USER không duyệt, TO/SA 403, khác phòng 404, header agent bị từ chối)
- [ ] 7. Xoá mềm (khi bật cờ) + lọc `deleted_at` ở các đường đọc + chặn xoá chứng từ đã từng ghi sổ
- [ ] 8. Middleware xoá `X-AIQ-Actor` (QĐ 23) + `Security:AgentInternalNetworks` + test 4 trường hợp
- [ ] 9. Bỏ nhánh `journalized` trong code; chạy đủ test; đồng bộ tài liệu; commit; báo cáo

### Ghi chú PR3
- Thiết kế: một trigger AFTER ghi `voucher_events` `state_change` cho MỌI thay đổi `lifecycle_state` (đường cũ = `system`; đường v2 đặt `aiq.actor_id/actor_type/reason` bằng `set_config(..., true)` trong transaction) → một nơi ghi duy nhất, luôn cùng transaction.
- `voucher_events.voucher_id` không FK (lịch sử sống sót khi tenant chưa bật cờ còn xoá cứng); append-only, chỉ cho DELETE khi tenant đã bị xoá (cascade).
- Trigger 059 chỉ chặn khi đổi cột nội dung → cập nhật cột vòng đời không vướng.
